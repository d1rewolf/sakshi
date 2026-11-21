using System.Runtime.Versioning;
using Sakshi.Platform.MacOS;

namespace Sakshi.Platform.Tests;

// Runs only on macOS (CI's macos-latest): it really moves temp items to the user's Trash.
// The attribute only satisfies the platform analyzer; Assert.SkipUnless does the skipping elsewhere.
[SupportedOSPlatform("macos")]
public sealed class MacTrashTests
{
    private static string NewTempItemPath() =>
        Path.Combine(Path.GetTempPath(), $"sakshi-trash-test-{Guid.NewGuid():N}");

    // The test asks for the resulting URL instead of listing ~/.Trash, which macOS privacy
    // protection (TCC) may refuse to a CI process.
    private static void AssertInTrashAndRemove(string original, string trashed, bool folder)
    {
        Assert.Equal(Path.GetFileName(original), Path.GetFileName(trashed));
        Assert.Contains("/.Trash", trashed);
        if (folder)
        {
            Assert.True(Directory.Exists(trashed), $"{trashed} is not in the trash");
            Directory.Delete(trashed, recursive: true);
        }
        else
        {
            Assert.True(File.Exists(trashed), $"{trashed} is not in the trash");
            File.Delete(trashed);
        }
    }

    [Fact]
    public void Moves_a_file_to_the_trash()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "macOS Trash");
        var path = NewTempItemPath() + ".txt";
        File.WriteAllText(path, "x");

        var trashed = MacTrash.Trash(path);

        Assert.False(File.Exists(path));
        AssertInTrashAndRemove(path, trashed, folder: false);
    }

    [Fact]
    public void Moves_a_folder_to_the_trash()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "macOS Trash");
        var path = NewTempItemPath();
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "a.txt"), "x");

        var trashed = MacTrash.Trash(path);

        Assert.False(Directory.Exists(path));
        AssertInTrashAndRemove(path, trashed, folder: true);
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
            Assert.Throws<IOException>(() => new MacTrash().MoveToTrash(path + "\0x"));
            Assert.True(File.Exists(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Missing_path_throws_IOException()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "macOS Trash");
        var path = NewTempItemPath();

        var e = Assert.Throws<IOException>(() => new MacTrash().MoveToTrash(path));
        Assert.Contains(path, e.Message);
    }
}
