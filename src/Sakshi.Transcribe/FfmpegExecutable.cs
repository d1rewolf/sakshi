namespace Sakshi.Transcribe;

/// <summary>
/// Which ffmpeg Sakshi runs: the one the installers ship next to the sakshi executable
/// (packaging/build-ffmpeg.sh), else the one on PATH (a build from source).
/// </summary>
public static class FfmpegExecutable
{
    public static string FileName => OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";

    /// <summary>The ffmpeg in <paramref name="dir"/>, or null when there is none.</summary>
    public static string? Bundled(string dir, Func<string, bool> exists)
    {
        var path = Path.Combine(dir, FileName);
        return exists(path) ? path : null;
    }

    /// <summary>The ffmpeg shipped with this copy of Sakshi, or null.</summary>
    public static string? Bundled() => Bundled(AppContext.BaseDirectory, File.Exists);

    /// <summary>What to start: the shipped ffmpeg's path, or "ffmpeg" to look it up on PATH.</summary>
    public static string Command => Bundled() ?? "ffmpeg";

    public static bool Available => Bundled() is not null || TranscribeCommand.OnPath("ffmpeg");
}
