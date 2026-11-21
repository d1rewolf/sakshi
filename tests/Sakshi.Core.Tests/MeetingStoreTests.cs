using Sakshi.Core.Store;
using Sakshi.TestSupport;
using static Sakshi.Core.Tests.StoreTestHelpers;

namespace Sakshi.Core.Tests;

[Collection("Environment")]
public sealed class MeetingStoreTests : IDisposable
{
    private readonly TempHome _home = new();
    private readonly RecordingLog _log = new();
    private readonly Func<string, string, bool> _sameVolume = SafeMove.SameVolume;
    private readonly Action<string, string> _copyFile = SafeMove.CopyFile;
    private readonly Action<string>? _afterVerify = SafeMove.AfterVerify;
    private readonly Func<string, string, int> _link = NativeMove.LinkImpl;
    private readonly Func<string, string, int> _renameNoReplace = NativeMove.RenameNoReplaceImpl;

    public void Dispose()
    {
        SafeMove.SameVolume = _sameVolume;
        SafeMove.CopyFile = _copyFile;
        SafeMove.AfterVerify = _afterVerify;
        NativeMove.LinkImpl = _link;
        NativeMove.RenameNoReplaceImpl = _renameNoReplace;
        // A read-only dir would block cleanup.
        foreach (var d in Directory.EnumerateDirectories(_home.Root, "*", SearchOption.AllDirectories))
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(d, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        _home.Dispose();
    }

    private MeetingStore Store() => new(_home.Meetings, Path.Combine(_home.Meetings, "archives"), _log);

    private string Src(string name = "rec.mp4") => MkVideo(Path.Combine(_home.Root, "src", name));

    [Fact]
    public void UniqueMeetingDir_SuffixesFrom2()
    {
        var s = Store();
        var day = "2026-01-05-Mon";
        var first = s.UniqueMeetingDir(day, "Sync");
        Assert.Equal(Path.Combine(_home.Meetings, day, "Sync"), first);
        Directory.CreateDirectory(first);
        var second = s.UniqueMeetingDir(day, "Sync");
        Assert.Equal(Path.Combine(_home.Meetings, day, "Sync-2"), second);
        Directory.CreateDirectory(second);
        Assert.Equal(Path.Combine(_home.Meetings, day, "Sync-3"), s.UniqueMeetingDir(day, "Sync"));
    }

    [Fact]
    public void StoreVideo_SecondSameNameGetsSuffix()
    {
        var s = Store();
        var a = s.StoreVideo(Src("a.mp4"), "2026-01-05-Mon", "Sync");
        var b = s.StoreVideo(Src("b.mp4"), "2026-01-05-Mon", "Sync");
        Assert.Equal(Path.Combine(_home.Meetings, "2026-01-05-Mon", "Sync", "Sync.mp4"), a);
        Assert.Equal(Path.Combine(_home.Meetings, "2026-01-05-Mon", "Sync-2", "Sync-2.mp4"), b);
        Assert.True(File.Exists(a));
        Assert.True(File.Exists(b));
    }

    [Fact]
    public void StoreVideo_KeepsExtension()
    {
        var s = Store();
        var v = s.StoreVideo(Src("x.mkv"), "2026-01-05-Mon", "Talk");
        Assert.EndsWith("Talk.mkv", v);
        Assert.True(File.Exists(v));
    }

    [Fact]
    public void StoreVideo_FailedMove_LeavesSourceAndNoEmptyDir()
    {
        var s = Store();
        var src = Src();
        var day = Path.Combine(_home.Meetings, "2026-01-05-Mon");
        FileStream? held = null;
        if (OperatingSystem.IsWindows())
        {
            held = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.None);
        }
        else
        {
            if (Environment.UserName == "root") Assert.Skip("root ignores directory permissions");
            Directory.CreateDirectory(day);
            // The meeting dir can be made, but not written into.
            File.SetUnixFileMode(day, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        }
        try
        {
            Assert.Throws<StoreFailedException>(() => s.StoreVideo(src, "2026-01-05-Mon", "Sync"));
        }
        finally
        {
            held?.Dispose();
        }
        Assert.True(File.Exists(src));
        Assert.Equal(4096, new FileInfo(src).Length);
        Assert.False(Directory.Exists(Path.Combine(day, "Sync")));
    }

    [Fact]
    public void StoreVideo_FailureAfterDirMade_RemovesEmptyDir()
    {
        var s = Store();
        var src = Path.Combine(_home.Root, "src", "missing.mp4");
        Assert.Throws<StoreFailedException>(() => s.StoreVideo(src, "2026-01-05-Mon", "Sync"));
        Assert.False(Directory.Exists(Path.Combine(_home.Meetings, "2026-01-05-Mon", "Sync")));
    }

    [Fact]
    public void SafeMove_CrossVolumeFallback_KeepsSourceOnCopyFailure()
    {
        var src = Src();
        var dest = Path.Combine(_home.Root, "dest", "out.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        SafeMove.SameVolume = (_, _) => false;
        SafeMove.CopyFile = (from, to) =>
        {
            File.WriteAllBytes(to, new byte[100]);
            throw new IOException("disk full");
        };
        Assert.Throws<IOException>(() => SafeMove.MoveFile(src, dest));
        Assert.True(File.Exists(src));
        Assert.Equal(4096, new FileInfo(src).Length);
        Assert.False(File.Exists(dest));
        Assert.False(File.Exists(dest + ".sakshi-partial"));
    }

    [Fact]
    public void SafeMove_CrossVolumeFallback_CopiesVerifiesAndRemovesSource()
    {
        var src = Src();
        var dest = Path.Combine(_home.Root, "dest", "out.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        SafeMove.SameVolume = (_, _) => false;
        SafeMove.MoveFile(src, dest);
        Assert.False(File.Exists(src));
        Assert.Equal(4096, new FileInfo(dest).Length);
        Assert.False(File.Exists(dest + ".sakshi-partial"));
    }

    [Fact]
    public void SafeMove_CrossVolumeDirectory_KeepsSourceOnCopyFailure()
    {
        var dir = Path.Combine(_home.Root, "src", "meet");
        MkVideo(Path.Combine(dir, "a.mp4"));
        MkVideo(Path.Combine(dir, "sub", "b.mp4"));
        var dest = Path.Combine(_home.Root, "dest", "meet");
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        SafeMove.SameVolume = (_, _) => false;
        var calls = 0;
        SafeMove.CopyFile = (from, to) =>
        {
            if (++calls == 2) throw new IOException("boom");
            File.Copy(from, to);
        };
        Assert.Throws<IOException>(() => SafeMove.MoveDirectory(dir, dest));
        Assert.True(File.Exists(Path.Combine(dir, "a.mp4")));
        Assert.True(File.Exists(Path.Combine(dir, "sub", "b.mp4")));
        Assert.False(Directory.Exists(dest));
        Assert.False(Directory.Exists(dest + ".sakshi-partial"));
    }

    [Fact]
    public void SafeMove_NeverOverwritesExistingDestination()
    {
        var src = Src();
        var dest = Path.Combine(_home.Root, "dest.mp4");
        File.WriteAllText(dest, "keep");
        Assert.Throws<IOException>(() => SafeMove.MoveFile(src, dest));
        Assert.Equal("keep", File.ReadAllText(dest));
        Assert.True(File.Exists(src));
    }

    [Fact]
    public void SafeMove_SameVolumeFileMove_NeverCopies()
    {
        var src = Src();
        var dest = Path.Combine(_home.Root, "dest.mp4");
        SafeMove.CopyFile = (_, _) => throw new InvalidOperationException("copy must not be used");
        SafeMove.MoveFile(src, dest);
        Assert.False(File.Exists(src));
        Assert.Equal(4096, new FileInfo(dest).Length);
        Assert.False(File.Exists(dest + ".sakshi-partial"));
    }

    [Fact]
    public void SafeMove_NonCrossVolumeIOException_PropagatesWithoutCopy()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("ACLs differ on Windows");
            return;
        }
        if (Environment.UserName == "root") Assert.Skip("root ignores directory permissions");
        var src = Src();
        var destDir = Path.Combine(_home.Root, "ro");
        Directory.CreateDirectory(destDir);
        File.SetUnixFileMode(destDir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        var copied = false;
        SafeMove.CopyFile = (_, _) => copied = true;
        Assert.ThrowsAny<IOException>(() => SafeMove.MoveFile(src, Path.Combine(destDir, "out.mp4")));
        Assert.False(copied);
        Assert.True(File.Exists(src));
        Assert.Empty(Directory.GetFileSystemEntries(destDir));
    }

    [Fact]
    public void SafeMove_CrossVolumeDirectory_FileAddedAfterVerify_StaysInSource()
    {
        var dir = Path.Combine(_home.Root, "src", "meet");
        MkVideo(Path.Combine(dir, "a.mp4"));
        var dest = Path.Combine(_home.Root, "dest", "meet");
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        SafeMove.SameVolume = (_, _) => false;
        SafeMove.AfterVerify = s => File.WriteAllText(Path.Combine(s, "late.txt"), "late");
        Assert.Throws<IOException>(() => SafeMove.MoveDirectory(dir, dest));
        Assert.Equal("late", File.ReadAllText(Path.Combine(dir, "late.txt")));
        Assert.True(File.Exists(Path.Combine(dest, "a.mp4")));
    }

    [Fact]
    public void SafeMove_CrossVolumeDirectory_Succeeds_RemovesSource()
    {
        var dir = Path.Combine(_home.Root, "src", "meet");
        MkVideo(Path.Combine(dir, "sub", "b.mp4"));
        var dest = Path.Combine(_home.Root, "dest", "meet");
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        SafeMove.SameVolume = (_, _) => false;
        SafeMove.MoveDirectory(dir, dest);
        Assert.False(Directory.Exists(dir));
        Assert.True(File.Exists(Path.Combine(dest, "sub", "b.mp4")));
    }

    private const int Eperm = 1, Exdev = 18, Einval = 22;

    private static void SkipOnWindows()
    {
        if (OperatingSystem.IsWindows()) Assert.Skip("Unix link/rename fallbacks");
    }

    [Theory]
    [InlineData(Eperm)]
    [InlineData(38)]  // ENOSYS (Linux)
    [InlineData(95)]  // ENOTSUP/EOPNOTSUPP (Linux)
    public void NativeMove_NoHardLinks_FallsBackToRenameNoReplace(int linkErrno)
    {
        SkipOnWindows();
        if (!OperatingSystem.IsLinux()) Assert.Skip("Linux errno values");
        var src = Src();
        var dest = Path.Combine(_home.Root, "dest.mp4");
        NativeMove.LinkImpl = (_, _) => linkErrno;
        SafeMove.CopyFile = (_, _) => throw new InvalidOperationException("copy must not be used");
        SafeMove.MoveFile(src, dest);
        Assert.False(File.Exists(src));
        Assert.Equal(4096, new FileInfo(dest).Length);

        var src2 = Src("two.mp4");
        Assert.Throws<IOException>(() => SafeMove.MoveFile(src2, dest));
        Assert.True(File.Exists(src2));
        Assert.Throws<IOException>(() => NativeMove.MoveFile(src2, dest)); // renameat2 says EEXIST
        Assert.True(File.Exists(src2));
        Assert.Equal(4096, new FileInfo(dest).Length);
    }

    [Fact]
    public void NativeMove_NoHardLinksNoNoReplace_LastResortRenameStillRefusesExistingDest()
    {
        SkipOnWindows();
        var src = Src();
        var dest = Path.Combine(_home.Root, "dest.mp4");
        NativeMove.LinkImpl = (_, _) => Eperm;
        NativeMove.RenameNoReplaceImpl = (_, _) => Einval;
        SafeMove.CopyFile = (_, _) => throw new InvalidOperationException("copy must not be used");
        SafeMove.MoveFile(src, dest);
        Assert.False(File.Exists(src));
        Assert.Equal(4096, new FileInfo(dest).Length);

        var src2 = Src("two.mp4");
        File.WriteAllText(Path.Combine(_home.Root, "keep"), "keep");
        Assert.Throws<IOException>(() => NativeMove.MoveFile(src2, dest));
        Assert.Throws<IOException>(() => NativeMove.MoveFile(src2, Path.Combine(_home.Root, "keep")));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(_home.Root, "keep")));
        Assert.True(File.Exists(src2));
    }

    [Fact]
    public void NativeMove_NoHardLinks_CrossDeviceFromRenameTakesCopyPath()
    {
        SkipOnWindows();
        var src = Src();
        var dest = Path.Combine(_home.Root, "dest.mp4");
        NativeMove.LinkImpl = (_, _) => Eperm;
        NativeMove.RenameNoReplaceImpl = (_, _) => Exdev;
        Assert.Equal(NativeMove.Result.CrossDevice, NativeMove.MoveFile(src, dest));
        Assert.True(File.Exists(src));
        Assert.False(File.Exists(dest));
    }

    [Fact]
    public void NativeMove_OtherLinkErrno_Throws()
    {
        SkipOnWindows();
        var src = Src();
        NativeMove.LinkImpl = (_, _) => 13; // EACCES
        Assert.Throws<IOException>(() => NativeMove.MoveFile(src, Path.Combine(_home.Root, "d.mp4")));
        Assert.True(File.Exists(src));
    }

    [Fact]
    public void SafeMove_CrossVolumeDirectory_FileChangedAfterVerify_IsKept()
    {
        var dir = Path.Combine(_home.Root, "src", "meet");
        var a = MkVideo(Path.Combine(dir, "a.mp4"));
        var dest = Path.Combine(_home.Root, "dest", "meet");
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        SafeMove.SameVolume = (_, _) => false;
        SafeMove.AfterVerify = _ => File.AppendAllText(a, "more");
        Assert.Throws<IOException>(() => SafeMove.MoveDirectory(dir, dest));
        Assert.Equal(4100, new FileInfo(a).Length);
    }

    [Fact]
    public void MoveMerge_RenamesWhenDestMissing()
    {
        var src = Path.Combine(_home.Root, "a");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "x"), "1");
        var dest = Path.Combine(_home.Root, "b");
        MeetingStore.MoveMerge(src, dest);
        Assert.False(Directory.Exists(src));
        Assert.Equal("1", File.ReadAllText(Path.Combine(dest, "x")));
    }

    [Fact]
    public void MoveMerge_FoldsIntoExistingSuffixingClashes_IncludingHidden()
    {
        var src = Path.Combine(_home.Root, "a");
        var dest = Path.Combine(_home.Root, "b");
        Directory.CreateDirectory(Path.Combine(src, "M"));
        Directory.CreateDirectory(Path.Combine(dest, "M"));
        Directory.CreateDirectory(Path.Combine(dest, "M-2"));
        File.WriteAllText(Path.Combine(src, "M", "f"), "new");
        File.WriteAllText(Path.Combine(dest, "M", "f"), "old");
        File.WriteAllText(Path.Combine(src, "note.txt"), "n1");
        File.WriteAllText(Path.Combine(dest, "note.txt"), "n0");
        File.WriteAllText(Path.Combine(src, ".hidden"), "h");
        MeetingStore.MoveMerge(src, dest);
        Assert.False(Directory.Exists(src));
        Assert.Equal("old", File.ReadAllText(Path.Combine(dest, "M", "f")));
        Assert.Equal("new", File.ReadAllText(Path.Combine(dest, "M-3", "f")));
        Assert.Equal("n0", File.ReadAllText(Path.Combine(dest, "note.txt")));
        Assert.Equal("n1", File.ReadAllText(Path.Combine(dest, "note.txt-2")));
        Assert.Equal("h", File.ReadAllText(Path.Combine(dest, ".hidden")));
    }

    [Fact]
    public async Task WaitForSettled_ReturnsAfterTwoEqualNonZeroSizes()
    {
        var f = Src();
        var polls = 0;
        await MeetingStore.WaitForSettledAsync(f, (_, _) => { polls++; return Task.CompletedTask; }, CancellationToken.None);
        Assert.Equal(1, polls);
    }

    [Fact]
    public async Task WaitForSettled_WaitsWhileGrowing()
    {
        var f = Path.Combine(_home.Root, "grow.mp4");
        File.WriteAllBytes(f, new byte[10]);
        var polls = 0;
        await MeetingStore.WaitForSettledAsync(f, (_, _) =>
        {
            polls++;
            if (polls < 4) File.WriteAllBytes(f, new byte[10 + polls]);
            return Task.CompletedTask;
        }, CancellationToken.None);
        Assert.Equal(4, polls);
    }

    [Fact]
    public async Task WaitForSettled_GivesUpAfter120PollsAndNeverThrows()
    {
        var polls = 0;
        await MeetingStore.WaitForSettledAsync(Path.Combine(_home.Root, "never.mp4"),
            (_, _) => { polls++; return Task.CompletedTask; }, CancellationToken.None);
        Assert.Equal(120, polls);

        polls = 0;
        var empty = Path.Combine(_home.Root, "empty.mp4");
        File.WriteAllBytes(empty, []);
        await MeetingStore.WaitForSettledAsync(empty, (_, _) => { polls++; return Task.CompletedTask; }, CancellationToken.None);
        Assert.Equal(120, polls);
    }

    [Fact]
    public async Task WaitForSettled_UsesHalfSecondDelay()
    {
        var f = Src();
        TimeSpan seen = default;
        await MeetingStore.WaitForSettledAsync(f, (d, _) => { seen = d; return Task.CompletedTask; }, CancellationToken.None);
        Assert.Equal(TimeSpan.FromMilliseconds(500), seen);
    }
}
