using System.Runtime.Versioning;
using Sakshi.Platform.Windows;

namespace Sakshi.Platform.Tests;

// Runs only on Windows (CI's windows-latest): it really sends tiny temp items to the Recycle Bin.
// The bin itself is not inspected; the item being gone without an exception is the check.
// The attribute only satisfies the platform analyzer; Assert.SkipUnless does the skipping elsewhere.
[SupportedOSPlatform("windows")]
public sealed class RecycleBinTests
{
    private static string NewTempItemPath() =>
        Path.Combine(Path.GetTempPath(), $"sakshi-trash-test-{Guid.NewGuid():N}");

    [Fact]
    public void Moves_a_file_to_the_trash()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows Recycle Bin");
        var path = NewTempItemPath() + ".txt";
        File.WriteAllText(path, "x");

        new RecycleBin().MoveToTrash(path);

        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Moves_a_folder_to_the_trash()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows Recycle Bin");
        var path = NewTempItemPath();
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "a.txt"), "x");

        new RecycleBin().MoveToTrash(path);

        Assert.False(Directory.Exists(path));
    }

    // Runs on every OS: the check comes before any native call. Native code would read the path only up
    // to the NUL (macOS) or as a list of two paths (Windows) and trash something else.
    [Fact]
    public void A_path_with_a_NUL_throws_and_touches_nothing()
    {
        var path = NewTempItemPath();
        File.WriteAllText(path, "x");
        try
        {
            Assert.Throws<IOException>(() => new RecycleBin().MoveToTrash(path + "\0x"));
            Assert.True(File.Exists(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Missing_path_throws_IOException()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows Recycle Bin");
        var path = NewTempItemPath();

        var e = Assert.Throws<IOException>(() => new RecycleBin().MoveToTrash(path));
        Assert.Contains(path, e.Message);
    }
}
