using System.Text;

namespace Sakshi.App.Tests;

/// <summary>
/// A fresh, short, private directory for Unix socket paths. sun_path holds only 104 bytes on macOS
/// and 108 on Linux, and TempHome's root (under macOS's $TMPDIR or %TEMP%) is too long for that.
/// </summary>
public sealed class ShortDir : IDisposable
{
    public string Root { get; }

    public ShortDir()
    {
        var name = "sk-" + Guid.NewGuid().ToString("N")[..8];
        Root = OperatingSystem.IsWindows() ? Create(name) : MakeUnix(Path.Combine("/tmp", name));
        // Fail loudly rather than with an obscure bind error: the longest path a test adds is
        // sakshi-<user>/tray.sock under the root.
        Assert.True(Encoding.UTF8.GetByteCount(Root) <= 60,
            $"the short socket root '{Root}' is too long ({Encoding.UTF8.GetByteCount(Root)} bytes) for socket paths on this OS");
    }

    private static string MakeUnix(string path)
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    private static string Create(string name)
    {
        // The drive root is shortest; fall back to the temp directory when it is not writable.
        var candidates = new[]
        {
            Path.Combine(Path.GetPathRoot(Path.GetTempPath())!, name),
            Path.Combine(Path.GetTempPath(), name),
        };
        foreach (var c in candidates)
        {
            try { Directory.CreateDirectory(c); return c; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        throw new IOException("no writable place for a short socket directory");
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
