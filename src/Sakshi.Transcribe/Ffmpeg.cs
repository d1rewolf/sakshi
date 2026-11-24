using System.Diagnostics;

namespace Sakshi.Transcribe;

/// <summary>ffmpeg invocations, run directly (no shell), with the ffmpeg <see cref="FfmpegExecutable"/> picks.</summary>
public static class Ffmpeg
{
    private const int JpegQuality = 3;

    public static Task ExtractAudioAsync(string input, string wav, CancellationToken ct) =>
        RunCheckedAsync(
            ["-y", "-loglevel", "error", "-i", input, "-vn", "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le", wav],
            ct);

    /// <summary>
    /// The sound of <paramref name="input"/>, copied as it is (no re-encoding) into <paramref name="output"/>, an .m4a,
    /// next to it; the input is deleted once the copy is complete. Returns the .m4a's path.
    /// </summary>
    public static async Task<string> KeepAudioOnlyAsync(string input, CancellationToken ct)
    {
        var output = Path.ChangeExtension(input, ".m4a");
        await RunCheckedAsync(["-y", "-loglevel", "error", "-i", input, "-vn", "-c:a", "copy", output], ct);
        File.Delete(input);
        return output;
    }

    /// <summary>
    /// The audio of <paramref name="input"/> from <paramref name="from"/>, at most <paramref name="maxLength"/> of it,
    /// as 16 kHz mono 16-bit PCM. Works on a recording OBS is still writing (Hybrid MP4, MKV): it returns what has
    /// been written so far. Null when ffmpeg cannot read the file (yet), or does not start.
    /// </summary>
    public static async Task<byte[]?> ReadAudioAsync(string input, TimeSpan from, TimeSpan maxLength, CancellationToken ct)
    {
        var r = await RunAsync(
            ["-v", "error", "-ss", Seconds(from), "-i", input, "-t", Seconds(maxLength),
             "-vn", "-ac", "1", "-ar", "16000", "-f", "s16le", "-"],
            TimeSpan.FromMinutes(2), ct);
        return r is { ExitCode: 0 } ? r.Stdout : null;
    }

    private static string Seconds(TimeSpan t) => t.TotalSeconds.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Writes JPEGs, none when the filter passes no frame (a video that never holds still in "changes" mode): the
    /// explicit full-range pixel format lets the JPEG encoder start without a frame to copy it from; with frames it
    /// is what ffmpeg picks anyway. Does not create <paramref name="framesDir"/> or the metadata file. <paramref name="workingDirectory"/> lets the
    /// caller name the metadata file with a relative path in <paramref name="vf"/>, which sidesteps filtergraph
    /// escaping of drive letters, quotes and colons; input and framesDir must then be absolute.
    /// </summary>
    public static Task ExtractFramesAsync(
        string input, string framesDir, string vf, CancellationToken ct, string? workingDirectory = null) =>
        RunCheckedAsync(
            ["-y", "-loglevel", "error", "-i", input, "-vf", vf, "-fps_mode", "vfr", "-pix_fmt", "yuvj420p",
             "-q:v", JpegQuality.ToString(), Path.Combine(framesDir, "raw_%04d.jpg")],
            ct, workingDirectory);

    /// <summary>One ffmpeg decodes every file through the concat demuxer; null on any mismatch or failure.</summary>
    public static async Task<byte[]?> GrayPixelsAsync(IReadOnlyList<string> files, CancellationToken ct)
    {
        var list = Path.Combine(Path.GetTempPath(), $"sakshi-hashlist-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(list,
            string.Concat(files.Select(f =>
                $"file '{Path.GetFullPath(f).Replace("'", "'\\''")}'\n")), ct);
        try
        {
            var r = await RunAsync(
                ["-v", "error", "-f", "concat", "-safe", "0", "-i", list, "-vf", "scale=17:16",
                 "-fps_mode", "passthrough", "-f", "rawvideo", "-pix_fmt", "gray", "-"],
                TimeSpan.FromSeconds(60 + files.Count / 10), ct);
            return r is { ExitCode: 0 } && r.Stdout.Length == DHash.BytesPerFrame * files.Count
                ? r.Stdout : null;
        }
        finally
        {
            try { File.Delete(list); } catch (IOException) { }
        }
    }

    public static async Task<byte[]?> GrayPixelsAsync(string file, CancellationToken ct)
    {
        var r = await RunAsync(
            ["-v", "error", "-i", file, "-vf", "scale=17:16", "-frames:v", "1",
             "-f", "rawvideo", "-pix_fmt", "gray", "-"],
            TimeSpan.FromSeconds(30), ct);
        return r is { ExitCode: 0 } && r.Stdout.Length == DHash.BytesPerFrame ? r.Stdout : null;
    }

    private static async Task RunCheckedAsync(string[] args, CancellationToken ct, string? workingDirectory = null)
    {
        var r = await RunAsync(args, null, ct, workingDirectory)
            ?? throw new InvalidOperationException("ffmpeg could not be started.");
        if (r.ExitCode != 0)
            throw new InvalidOperationException($"ffmpeg failed (exit {r.ExitCode}): {r.Stderr.Trim()}");
    }

    private sealed record Result(int ExitCode, byte[] Stdout, string Stderr);

    /// <summary>Null when ffmpeg cannot start or the timeout expires; throws only on caller cancellation.</summary>
    private static async Task<Result?> RunAsync(
        string[] args, TimeSpan? timeout, CancellationToken ct, string? workingDirectory = null)
    {
        var psi = new ProcessStartInfo(FfmpegExecutable.Command)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (workingDirectory is not null) psi.WorkingDirectory = workingDirectory;
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = new Process { StartInfo = psi };
        try { proc.Start(); }
        catch (System.ComponentModel.Win32Exception) { return null; }
        proc.StandardInput.Close();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout is { } t) cts.CancelAfter(t);
        try
        {
            var stdout = new MemoryStream();
            var copy = proc.StandardOutput.BaseStream.CopyToAsync(stdout, cts.Token);
            var err = proc.StandardError.ReadToEndAsync(cts.Token);
            await Task.WhenAll(copy, err);
            await proc.WaitForExitAsync(cts.Token);
            return new Result(proc.ExitCode, stdout.ToArray(), err.Result);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Kill(proc);
            return null;
        }
        catch (OperationCanceledException)
        {
            Kill(proc);
            throw;
        }
    }

    private static void Kill(Process p)
    {
        try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
    }
}
