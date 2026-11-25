using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Sakshi.Transcribe;

namespace Sakshi.Speakers;

/// <summary>sakshi speakers --add: transcribe a meeting's audio again with word times, find its speakers, and
/// rewrite its transcript with labels. A meeting where nothing works is left exactly as it was.</summary>
public static partial class AddSpeakersCommand
{
    private static readonly string[] RecordingExtensions = [".mp4", ".mkv", ".m4a", ".mov"];

    [GeneratedRegex(@"^frame_(\d+)-(\d+)-(\d+)(?:_\d+)?\.jpg$", RegexOptions.IgnoreCase)]
    private static partial Regex FrameName();

    [GeneratedRegex(@"^- Source: `(.+)`\s*$", RegexOptions.Multiline)]
    private static partial Regex SourceLine();

    public static async Task<int> RunAsync(IReadOnlyList<string> meetings, IReadOnlyList<string> roots, IWhisper whisper,
        string model, ISpeakerStep step, TextWriter stdout, TextWriter stderr, CancellationToken ct,
        Func<bool>? ffmpegAvailable = null)
    {
        ffmpegAvailable ??= () => FfmpegExecutable.Available;
        var dirs = new List<string>();
        foreach (var m in meetings)
        {
            try { dirs.Add(MeetingLocator.Find(m, roots)); }
            catch (UsageException e)
            {
                stderr.WriteLine(e.Message);
                return 2;
            }
        }

        var failed = false;
        foreach (var dir in dirs)
        {
            ct.ThrowIfCancellationRequested();
            stdout.WriteLine($"==> {dir}");
            try
            {
                if (!await AddOneAsync(dir, whisper, model, step, ffmpegAvailable, stdout, stderr, ct)) failed = true;
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                stderr.WriteLine($"Warning: {dir}: speaker labels skipped: {e.Message}");
                failed = true;
            }
        }
        return failed ? 1 : 0;
    }

    private static async Task<bool> AddOneAsync(string dir, IWhisper whisper, string model, ISpeakerStep step,
        Func<bool> ffmpegAvailable, TextWriter stdout, TextWriter stderr, CancellationToken ct)
    {
        var wav = Path.Combine(dir, "audio.wav");
        var recording = Directory.EnumerateFiles(dir)
            .Where(f => RecordingExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal)
            .FirstOrDefault();
        var hasWav = File.Exists(wav);
        if (!hasWav && recording is null)
        {
            stderr.WriteLine($"{dir} has no audio.wav or recording to label");
            return false;
        }

        var speakersPath = Path.Combine(dir, "speakers.json");
        SpeakersFile? existing = null;
        if (File.Exists(speakersPath))
        {
            stdout.WriteLine("   replacing the existing speaker labels");
            try { existing = SpeakersFile.Read(speakersPath); } catch (InvalidDataException) { }
        }

        var language = "en";
        try { language = TranscriptFiles.ReadJson(Path.Combine(dir, "transcript.json")).Language; }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException or KeyNotFoundException
            or InvalidOperationException or UnauthorizedAccessException) { }

        var source = recording is not null ? Path.GetFileName(recording) : SourceFromMeetingMd(dir) ?? "audio.wav";
        IReadOnlyList<FrameRecord>? frames = existing is not null ? existing.Frames : ReadFrames(dir);

        if (!hasWav)
        {
            if (!ffmpegAvailable())
                throw new InvalidOperationException(
                    $"ffmpeg was not found; it is needed to take the sound out of {Path.GetFileName(recording)}");
            stdout.WriteLine("==> Extracting audio");
            var part = Path.Combine(dir, "audio.part.wav"); // ffmpeg picks the format from the extension
            try
            {
                await Ffmpeg.ExtractAudioAsync(recording!, part, ct);
                File.Move(part, wav, overwrite: true);
            }
            finally
            {
                try { File.Delete(part); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }

        stdout.WriteLine("==> Transcribing with Whisper.net");
        var segments = await whisper.TranscribeAsync(wav, model, language, 0, ct, words: true);

        stdout.WriteLine("==> Finding speakers");
        // The step writes speakers.json and the clips into a staging folder; the meeting's own copies are replaced
        // only after the transcript has been rewritten, so a failure leaves them matching the old transcript.
        var staging = Path.Combine(dir, ".speakers-staging");
        try
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            Directory.CreateDirectory(staging);
            var outcome = await step.LabelAsync(wav, staging, segments, source, frames, stdout, ct);
            if (outcome.Speakers <= 0)
            {
                stdout.WriteLine($"{dir}: no speakers found");
                return true;
            }

            var labelled = outcome.Segments;
            var md = MeetingMarkdown.Render(source, frames ?? [], labelled, frames is null);
            var stagedJson = Path.Combine(staging, "speakers.json");
            // speakers.json goes with the transcript, all or nothing, so it never names speakers the transcript
            // does not carry.
            TranscribeCommand.ReplaceFiles(
            [
                (Path.Combine(dir, "transcript.json"), p => TranscriptFiles.WriteJson(p, labelled, language)),
                (Path.Combine(dir, "transcript.srt"), p => TranscriptFiles.WriteSrt(p, labelled)),
                (Path.Combine(dir, "meeting.md"), p => File.WriteAllText(p, md, new UTF8Encoding(false))),
                (speakersPath, p => File.Copy(stagedJson, p, overwrite: true)),
            ]);
            try { ReplaceClips(dir, Path.Combine(staging, "speakers"), speakersPath, stderr); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                stderr.WriteLine($"Warning: {dir}: could not replace the voice clips: {e.Message}");
            }
            stdout.WriteLine(
                $"   found {outcome.Speakers} {(outcome.Speakers == 1 ? "speaker" : "speakers")} ({outcome.Suggested + outcome.Unknown} to name)");
        }
        finally
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return true;
    }

    /// <summary>Best effort, after the labels are in place. Each new clip is moved in over the old one. A clip that
    /// cannot be replaced (on Windows, one open in a player) goes in under the first free name S3-2.wav, S3-3.wav, …
    /// and speakers.json is pointed at it, so a speaker's clip never plays someone else. Every other clip is then
    /// deleted where it can be.</summary>
    private static void ReplaceClips(string dir, string staged, string speakersPath, TextWriter stderr)
    {
        var clips = Path.Combine(dir, "speakers");
        Directory.CreateDirectory(clips);
        var placed = new HashSet<string>(StringComparer.Ordinal);
        var elsewhere = new Dictionary<string, string>(StringComparer.Ordinal); // speaker id -> clip file name
        if (Directory.Exists(staged))
            foreach (var f in Directory.EnumerateFiles(staged))
            {
                var name = Path.GetFileName(f);
                var id = Path.GetFileNameWithoutExtension(name);
                try
                {
                    File.Move(f, Path.Combine(clips, name), overwrite: true);
                    placed.Add(name);
                    continue;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                try
                {
                    var other = FreeName(clips, id);
                    File.Move(f, Path.Combine(clips, other));
                    placed.Add(other);
                    elsewhere[id] = other;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    stderr.WriteLine($"Warning: {dir}: could not put the new voice clip for {id} in place: {e.Message}; " +
                        $"speakers/{name} may still play an earlier voice (close any program using it and run sakshi speakers --add again)");
                }
            }

        if (elsewhere.Count > 0)
            try
            {
                var file = SpeakersFile.Read(speakersPath);
                (file with
                {
                    Speakers = file.Speakers
                        .Select(s => elsewhere.TryGetValue(s.Id, out var n) ? s with { Clip = "speakers/" + n } : s).ToList(),
                }).Write(speakersPath);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                // The clips speakers.json still names were not replaced; the deletes below take them away if they can.
                stderr.WriteLine($"Warning: {dir}: could not point speakers.json at the new voice clips: {e.Message}");
                foreach (var id in elsewhere.Keys.Order(StringComparer.Ordinal))
                    stderr.WriteLine($"Warning: {dir}: speakers/{id}.wav may still play an earlier voice " +
                        "(close any program using it and run sakshi speakers --add again)");
            }

        foreach (var old in Directory.EnumerateFiles(clips))
        {
            var name = Path.GetFileName(old);
            if (!SpeakerClips.IsClipName(name) || placed.Contains(name)) continue;
            try { File.Delete(old); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    private static string FreeName(string clips, string id)
    {
        for (var k = 2; ; k++)
        {
            var name = $"{id}-{k}.wav";
            if (!Path.Exists(Path.Combine(clips, name))) return name;
        }
    }

    private static string? SourceFromMeetingMd(string dir)
    {
        try
        {
            var md = Path.Combine(dir, "meeting.md");
            if (!File.Exists(md)) return null;
            var m = SourceLine().Match(File.ReadAllText(md));
            return m.Success ? m.Groups[1].Value : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    private static List<FrameRecord>? ReadFrames(string dir)
    {
        var folder = Path.Combine(dir, "frames");
        if (!Directory.Exists(folder)) return null;
        var records = new List<FrameRecord>();
        foreach (var f in Directory.EnumerateFiles(folder))
        {
            var name = Path.GetFileName(f);
            if (FrameName().Match(name) is not { Success: true } m) continue;
            // Only whole seconds survive in the file name, so a picture's time can be up to a second early, and a
            // line spoken right then may land under the neighbouring picture in the rebuilt meeting.md.
            var seconds = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * 3600.0
                + int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) * 60
                + int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
            records.Add(new FrameRecord(seconds, name));
        }
        return records.OrderBy(r => r.Seconds).ThenBy(r => r.FileName, StringComparer.Ordinal).ToList();
    }
}
