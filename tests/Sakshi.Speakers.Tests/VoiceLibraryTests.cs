using Sakshi.Speakers;

namespace Sakshi.Speakers.Tests;

public sealed class VoiceLibraryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sakshi-vl-" + Guid.NewGuid().ToString("N"));
    private string File_ => Path.Combine(_dir, "voices", "voices.json");
    private static readonly DateTimeOffset T0 = new(2026, 11, 25, 10, 0, 0, TimeSpan.Zero);

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void MissingFileIsEmpty()
    {
        var lib = VoiceLibrary.Load(File_);
        Assert.Empty(lib.People);
        Assert.Null(lib.BackgroundSum.Sum);
        Assert.Equal(0, lib.BackgroundSum.Count);
    }

    [Fact]
    public void TeachSaveLoadRoundTrips()
    {
        var lib = VoiceLibrary.Load(File_);
        var p = lib.Teach("Ada", [1f, 2f], "2026-11-20-Fri/steering", "S2", T0);
        lib.AddToBackground([[1f, 1f], [2f, 3f]]);
        lib.Save();
        var back = VoiceLibrary.Load(File_);
        var ada = Assert.Single(back.People);
        Assert.Equal(p.Id, ada.Id);
        Assert.Equal(32, ada.Id.Length);
        Assert.Equal("Ada", ada.Name);
        var pr = Assert.Single(ada.Prints);
        Assert.Equal([1f, 2f], pr.Vector);
        Assert.Equal("S2", pr.Speaker);
        Assert.Equal(T0, pr.Added);
        Assert.Equal(2, back.BackgroundSum.Count);
        Assert.Equal([3.0, 4.0], back.BackgroundSum.Sum!);
    }

    [Fact]
    public void TeachFindsCaseInsensitivelyAndKeepsName()
    {
        var lib = VoiceLibrary.Load(File_);
        lib.Teach("Ada", [1f], "m", "S1", T0);
        lib.Teach("ada", [2f], "m", "S2", T0.AddMinutes(1));
        var ada = Assert.Single(lib.People);
        Assert.Equal("Ada", ada.Name);
        Assert.Equal(2, ada.Prints.Count);
        Assert.Same(lib.Find("ADA"), lib.People[0]);
    }

    [Fact]
    public void EleventhPrintDropsOldest()
    {
        var lib = VoiceLibrary.Load(File_);
        for (var i = 0; i < 11; i++) lib.Teach("Ada", [i], "m", "S" + i, T0.AddMinutes(i));
        var ada = lib.Find("Ada")!;
        Assert.Equal(10, ada.Prints.Count);
        Assert.DoesNotContain(ada.Prints, p => p.Speaker == "S0");
        Assert.Contains(ada.Prints, p => p.Speaker == "S10");
    }

    [Fact]
    public void RenameToOtherPersonThrows()
    {
        var lib = VoiceLibrary.Load(File_);
        lib.Teach("Ada", [1f], "m", "S1", T0);
        lib.Teach("Ben", [1f], "m", "S2", T0);
        Assert.Throws<VoiceLibraryException>(() => lib.Rename("Ada", "ben"));
        Assert.Throws<VoiceLibraryException>(() => lib.Rename("Cy", "Dee"));
        lib.Rename("Ada", "Ada L");
        Assert.NotNull(lib.Find("ada l"));
        Assert.Null(lib.Find("Ada"));
    }

    [Fact]
    public void ForgetRemoves()
    {
        var lib = VoiceLibrary.Load(File_);
        lib.Teach("Ada", [1f], "m", "S1", T0);
        Assert.True(lib.Forget("ada"));
        Assert.False(lib.Forget("ada"));
        Assert.Empty(lib.People);
    }

    [Fact]
    public void BackgroundLengthMismatchThrows()
    {
        var lib = VoiceLibrary.Load(File_);
        lib.AddToBackground([[1f, 2f]]);
        Assert.Throws<ArgumentException>(() => lib.AddToBackground([[1f]]));
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("{\"version\":2,\"people\":[]}")]
    public void UnreadableFileThrowsAndIsUntouched(string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(File_)!);
        File.WriteAllText(File_, text);
        var before = File.ReadAllBytes(File_);
        var ex = Assert.Throws<VoiceLibraryException>(() => VoiceLibrary.Load(File_));
        Assert.Contains(File_, ex.Message);
        Assert.Equal(before, File.ReadAllBytes(File_));
    }

    [Fact]
    public void SavedFileIsPrivateOnUnix()
    {
        if (OperatingSystem.IsWindows()) return;
        var lib = VoiceLibrary.Load(File_);
        lib.Teach("Ada", [1f], "m", "S1", T0);
        lib.Save();
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, System.IO.File.GetUnixFileMode(File_));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            System.IO.File.GetUnixFileMode(Path.GetDirectoryName(File_)!));
    }
}
