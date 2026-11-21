using System.Text;
using Whisper.net.Ggml;

namespace Sakshi.Transcribe;

/// <summary>The transcribe-meeting flow: audio, whisper, frames, dedupe, meeting.md.</summary>
public static class TranscribeCommand
{
    public const string DefaultModelFile = "ggml-base.en.bin";

    // The metadata file is named relative to the output directory (ffmpeg runs there), so the
    // filtergraph never sees a drive letter, quote or colon from the user's path.
    private const string MetadataRelative = "frames/.metadata.txt";

    public static async Task<int> RunAsync(
        IReadOnlyList<string> args,
        IWhisper whisper,
        TextWriter stdout,
        TextWriter stderr,
        string modelsDir,
        CancellationToken ct,
        string? configModel = null,
        string? currentDirectory = null,
        Func<Stream, CancellationToken, Task>? downloadModel = null,
        TextWriter? log = null)
    {
        // --log: everything the command prints goes to the file (overwritten), as when the
        // tray starts it detached and no one is reading its output. The file is opened before
        // the arguments are checked, so a usage error reaches it too. A caller that has
        // already opened it (to report a config error there) passes it as log.
        StreamWriter? owned = null;
        if (log is null && TranscribeOptions.FindLogFile(args) is { } logFile)
        {
            if ((owned = await OpenLogAsync(logFile, stderr)) is null) return 1;
            log = owned;
        }
        try
        {
            TranscribeOptions o;
            try { o = TranscribeOptions.Parse(args); }
            catch (UsageException e)
            {
                await stderr.WriteLineAsync(e.Message);
                await stderr.WriteLineAsync(TranscribeUsage.Text);
                if (log is not null)
                {
                    await log.WriteLineAsync(e.Message);
                    await log.WriteLineAsync(TranscribeUsage.Text);
                }
                return 2;
            }
            if (o.Help)
            {
                await stdout.WriteLineAsync(TranscribeUsage.Text);
                return 0;
            }

            if (log is null)
                return await RunCoreAsync(o, whisper, stdout, stderr, modelsDir, ct, configModel, currentDirectory, downloadModel);
            var both = TextWriter.Synchronized(log);
            return await RunCoreAsync(o, whisper, both, both, modelsDir, ct, configModel, currentDirectory, downloadModel);
        }
        finally
        {
            if (owned is not null) await owned.DisposeAsync();
        }
    }

    /// <summary>Creates (or overwrites) the --log file. On failure says why on stderr and returns null.</summary>
    public static async Task<StreamWriter?> OpenLogAsync(string logFile, TextWriter stderr)
    {
        try
        {
            return new StreamWriter(new FileStream(logFile, FileMode.Create, FileAccess.Write, FileShare.Read), new UTF8Encoding(false))
            { AutoFlush = true };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            await stderr.WriteLineAsync($"Error: cannot write log file {logFile}: {e.Message}");
            return null;
        }
    }

    private static async Task<int> RunCoreAsync(
        TranscribeOptions o,
        IWhisper whisper,
        TextWriter stdout,
        TextWriter stderr,
        string modelsDir,
        CancellationToken ct,
        string? configModel,
        string? currentDirectory,
        Func<Stream, CancellationToken, Task>? downloadModel)
    {
        var noFrames = o.MarkdownOnly || o.Frames == FramesMode.None;
        string? mdFile = null;
        string outputDir;
        var tempDir = false;
        if (o.MarkdownOnly)
        {
            mdFile = StemUnderscored(o.Input) + ".md";
            outputDir = Path.Combine(Path.GetTempPath(), "sakshi-md-" + Guid.NewGuid().ToString("N"));
            tempDir = true;
        }
        else
            outputDir = Path.GetFullPath(o.OutputDir ?? StemUnderscored(o.Input));

        try
        {
            if (!File.Exists(o.Input))
            {
                await stderr.WriteLineAsync($"Error: input file not found: {o.Input}");
                return 1;
            }

            var explicitModel = o.Model ?? configModel;
            var model = explicitModel ?? Path.Combine(modelsDir, DefaultModelFile);
            if (!File.Exists(model))
            {
                string? why = null;
                if (explicitModel is null)
                    why = await TryDownloadAsync(model, stdout, downloadModel ?? DownloadDefaultModelAsync, ct);
                if (!File.Exists(model))
                {
                    await stderr.WriteLineAsync($"Error: whisper model not found: {model}");
                    if (why is not null) await stderr.WriteLineAsync($"Download failed: {why}");
                    return 1;
                }
            }

            if (!FfmpegExecutable.Available)
            {
                await stderr.WriteLineAsync("Missing dependency: ffmpeg");
                return 1;
            }

            var input = Path.GetFullPath(o.Input);
            var framesDir = Path.Combine(outputDir, "frames");
            Directory.CreateDirectory(noFrames ? outputDir : framesDir);
            var audio = Path.Combine(outputDir, "audio.wav");

            await stdout.WriteLineAsync($"==> Extracting audio to {audio}");
            await Ffmpeg.ExtractAudioAsync(input, audio, ct);

            await stdout.WriteLineAsync($"==> Transcribing with Whisper.net (model: {Path.GetFileName(model)})");
            IReadOnlyList<Segment> segments;
            try { segments = await whisper.TranscribeAsync(audio, model, o.Language, o.MaxContext, ct); }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                await stderr.WriteLineAsync($"Error: transcription failed (model: {model}): {e.Message}");
                return 1;
            }
            TranscriptFiles.WriteJson(Path.Combine(outputDir, "transcript.json"), segments, o.Language);
            TranscriptFiles.WriteSrt(Path.Combine(outputDir, "transcript.srt"), segments);

            var metaFile = Path.Combine(outputDir, MetadataRelative);
            if (!noFrames)
            {
                await stdout.WriteLineAsync(FramesLine(o));
                if (o.Frames == FramesMode.All)
                    await stderr.WriteLineAsync(
                        "Warning: --frames all extracts every frame; a 1-hour 30fps video yields ~108k JPEGs.");
                FrameFiles.ClearDerived(framesDir); // an interrupted earlier run's frames would be mixed in
                await File.WriteAllTextAsync(metaFile, "", ct);
                await Ffmpeg.ExtractFramesAsync(
                    input, framesDir, FrameFilters.VideoFilter(o, MetadataRelative), ct, outputDir);
            }

            await stdout.WriteLineAsync("==> Building meeting.md");
            IReadOnlyList<FrameRecord> frames = [];
            if (File.Exists(metaFile))
                frames = FrameFiles.RenameRaw(framesDir, FrameFiles.ParseMetadata(await File.ReadAllTextAsync(metaFile, ct)));
            else if (Directory.Exists(framesDir))
                frames = FrameFiles.RenameRaw(framesDir, []);
            if (o.Dedupe is { } distance && frames.Count > 0)
                frames = await Dedupe.RunAsync(framesDir, frames, distance, stdout, ct);

            var md = MeetingMarkdown.Render(Path.GetFileName(o.Input), frames, segments, noFrames);
            await File.WriteAllTextAsync(Path.Combine(outputDir, "meeting.md"), md, new UTF8Encoding(false), ct);
            var spoken = MeetingMarkdown.CountSpoken(segments);
            await stdout.WriteLineAsync(noFrames
                ? $"   wrote {spoken} transcript segments"
                : $"   wrote {frames.Count} frames and {spoken} transcript segments");

            if (mdFile is not null)
            {
                var target = Path.Combine(currentDirectory ?? Environment.CurrentDirectory, mdFile);
                File.Move(Path.Combine(outputDir, "meeting.md"), target, overwrite: true);
                await stdout.WriteLineAsync("==> Done.");
                await stdout.WriteLineAsync($"    Output: {mdFile}");
            }
            else
            {
                await stdout.WriteLineAsync("==> Done.");
                await stdout.WriteLineAsync($"    Output: {outputDir}");
                await stdout.WriteLineAsync($"    Read:   {Path.Combine(outputDir, "meeting.md")}");
            }
            return 0;
        }
        catch (InvalidOperationException e)   // ffmpeg failed or could not start
        {
            await stderr.WriteLineAsync($"Error: {e.Message}");
            return 1;
        }
        finally
        {
            if (tempDir)
                try { Directory.Delete(outputDir, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    private static string StemUnderscored(string input) =>
        Path.GetFileNameWithoutExtension(input).Replace(' ', '_');

    private static string FramesLine(TranscribeOptions o) => o.Frames switch
    {
        FramesMode.Scene =>
            $"==> Extracting frames (mode=scene threshold={Num(o.Threshold)} strong={Num(o.Strong)} min-gap={o.MinGap}s max-width={o.MaxWidth}px)",
        FramesMode.Changes => $"==> Extracting frames (mode=changes max-width={o.MaxWidth}px)",
        _ => $"==> Extracting frames (mode=all max-width={o.MaxWidth}px)",
    };

    private static string Num(double d) => d.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Downloads the default model to <paramref name="model"/>; returns the failure reason, or null on success.</summary>
    public static Task<string?> DownloadModelAsync(string model, CancellationToken ct) =>
        TryDownloadAsync(model, TextWriter.Null, DownloadDefaultModelAsync, ct);

    private static async Task DownloadDefaultModelAsync(Stream destination, CancellationToken ct)
    {
        await using var src = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(GgmlType.BaseEn, cancellationToken: ct);
        await src.CopyToAsync(destination, ct);
    }

    /// <summary>
    /// Downloads to a .part file, then renames it. Returns the failure reason, or null on success. Any failure
    /// (including an HttpClient timeout) removes the .part file; caller cancellation does too, then propagates.
    /// </summary>
    private static async Task<string?> TryDownloadAsync(
        string model, TextWriter stdout, Func<Stream, CancellationToken, Task> download, CancellationToken ct)
    {
        var part = model + ".part";
        var done = false;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(model)!);
            await stdout.WriteLineAsync($"==> Downloading whisper model {Path.GetFileName(model)} to {model}");
            await using (var dst = File.Create(part))
                await download(dst, ct);
            File.Move(part, model, overwrite: true);
            done = true;
            return null;
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            return e.Message;
        }
        finally
        {
            if (!done)
                try { File.Delete(part); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>Whether <paramref name="exe"/> is on PATH.</summary>
    public static bool OnPath(string exe)
    {
        var names = OperatingSystem.IsWindows() ? new[] { exe + ".exe", exe } : [exe];
        return (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(d => names.Any(n => File.Exists(Path.Combine(d, n))));
    }
}
