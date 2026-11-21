namespace Sakshi.TestSupport;

/// <summary>
/// Locates the sakshi-test-helper executable (tests/Sakshi.TestHelper). The
/// test projects that need it reference that project with
/// ReferenceOutputAssembly=false and copy its output to "test-helper" next to
/// the test assembly after build, so this works the same on every OS.
/// </summary>
public static class TestHelper
{
    public const string Name = "sakshi-test-helper";

    /// <summary>Directory holding the helper's apphost, dll and runtimeconfig.</summary>
    public static string SourceDir { get; } = Path.Combine(AppContext.BaseDirectory, "test-helper");

    /// <summary>
    /// Copies the helper into a fresh "bin" directory under <paramref name="root"/>
    /// so it can be put on PATH or passed as an extra directory; returns that directory.
    /// </summary>
    public static string Install(string root)
    {
        var bin = Path.Combine(root, "bin");
        Directory.CreateDirectory(bin);
        foreach (var f in Directory.GetFiles(SourceDir))
        {
            var dest = Path.Combine(bin, Path.GetFileName(f));
            File.Copy(f, dest, overwrite: true);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(dest, File.GetUnixFileMode(f));
        }
        return bin;
    }
}
