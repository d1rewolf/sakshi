namespace Sakshi.Platform.MacOS;

public static class MacPath
{
    public static readonly string[] HomebrewDirs = ["/opt/homebrew/bin", "/usr/local/bin"];

    /// <summary>
    /// A GUI app started from Finder or at login gets PATH=/usr/bin:/bin:/usr/sbin:/sbin, so Homebrew's
    /// ffmpeg is not found. The Homebrew dirs go last, so the user's own PATH order still wins.
    /// </summary>
    public static string WithHomebrew(string? path, Func<string, bool> dirExists)
    {
        var entries = (path ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries).ToList();
        foreach (var dir in HomebrewDirs)
            if (!entries.Contains(dir) && dirExists(dir)) entries.Add(dir);
        return string.Join(':', entries);
    }
}
