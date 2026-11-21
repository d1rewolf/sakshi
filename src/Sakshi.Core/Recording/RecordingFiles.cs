namespace Sakshi.Core.Recording;

/// <summary>Finding OBS's recording in its record folder.</summary>
public static class RecordingFiles
{
    private static readonly string[] VideoExtensions = [".mp4", ".mkv", ".mov", ".ts", ".flv"];

    /// <summary>The newest video in <paramref name="dir"/> last written at or after <paramref name="sinceUtc"/>; null if none.</summary>
    public static string? NewestSince(string? dir, DateTime sinceUtc)
    {
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return null;
        return Directory.EnumerateFiles(dir)
            .Where(f => VideoExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .Select(f => (Path: f, Time: File.GetLastWriteTimeUtc(f)))
            .Where(x => x.Time >= sinceUtc)
            .OrderBy(x => x.Time).ThenBy(x => x.Path, StringComparer.Ordinal)
            .Select(x => x.Path)
            .LastOrDefault();
    }
}
