using Sakshi.Core.Ports;
using Sakshi.Platform.Autostart;

namespace Sakshi.Platform.Linux;

/// <summary>
/// Keeps the menu entry, the icon and the <c>~/.local/bin/sakshi</c> link pointing at the AppImage that is
/// running now, so moving or renaming the file only needs the next tray start to heal everything.
/// </summary>
public static class AppImageIntegration
{
    /// <summary><c>$XDG_DATA_HOME</c> when it is an absolute path, else <c>~/.local/share</c>.</summary>
    public static string DataHome(string home, string? xdgDataHome) =>
        xdgDataHome is { Length: > 0 } && Path.IsPathRooted(xdgDataHome) ? xdgDataHome : Path.Combine(home, ".local", "share");

    /// <summary>
    /// Idempotent: a file is written only when its content differs. Never throws: a failure is logged, and each
    /// piece is attempted on its own. <paramref name="autostart"/> is re-pointed only when it is already on.
    /// </summary>
    public static void Ensure(string appImage, string home, string dataHome, byte[] iconPng, IAutostart autostart, ILog log)
    {
        Attempt("menu entry", log, () => WriteIfChanged(Path.Combine(dataHome, "applications", "sakshi.desktop"),
            System.Text.Encoding.UTF8.GetBytes(DesktopFile(appImage))));
        Attempt("icon", log, () => WriteIfChanged(Path.Combine(dataHome, "icons", "hicolor", "256x256", "apps", "sakshi.png"), iconPng));
        Attempt("~/.local/bin link", log, () => EnsureLink(Path.Combine(home, ".local", "bin", "sakshi"), appImage, log));
        // The autostart entry holds the AppImage path too; a user who turned it off must not get it back.
        Attempt("autostart", log, () => { if (autostart.IsEnabled) autostart.Enable(appImage); });
    }

    private static string DesktopFile(string appImage) =>
        "[Desktop Entry]\nType=Application\nName=Sakshi\nComment=Record and transcribe meetings\n" +
        $"Exec={XdgAutostart.QuoteExec(appImage)}\nIcon=sakshi\nCategories=AudioVideo;Recorder;\nStartupWMClass=sakshi\n";

    private static void WriteIfChanged(string path, byte[] content)
    {
        if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(content)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
    }

    private static void EnsureLink(string link, string target, ILog log)
    {
        var info = new FileInfo(link);
        if (info.LinkTarget is { } current)
        {
            if (current == target) return;
        }
        else if (info.Exists || Directory.Exists(link))
        {
            log.Info($"appimage: {link} exists and is not a symlink; leaving it alone");
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        // Temp link + rename, so the name never points nowhere (and never vanishes) while it is replaced.
        var temp = link + ".new-" + Environment.ProcessId;
        File.CreateSymbolicLink(temp, target);
        try { File.Move(temp, link, overwrite: true); }
        catch { File.Delete(temp); throw; }
    }

    private static void Attempt(string what, ILog log, Action action)
    {
        try { action(); }
        catch (Exception e) { log.Info($"appimage: could not update the {what}: {e.Message}"); }
    }
}
