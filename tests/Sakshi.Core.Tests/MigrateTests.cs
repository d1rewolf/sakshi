using Sakshi.Core.Store;
using Sakshi.TestSupport;
using static Sakshi.Core.Tests.StoreTestHelpers;

namespace Sakshi.Core.Tests;

[Collection("Environment")]
public sealed class MigrateTests : IDisposable
{
    private readonly TempHome _home = new();
    private readonly RecordingLog _log = new();
    private readonly string _m;

    public MigrateTests()
    {
        _m = _home.Meetings;
        var m = _m;
        Directory.CreateDirectory(Path.Combine(m, "Flat_A"));
        Directory.CreateDirectory(Path.Combine(m, "Flat_B"));
        Directory.CreateDirectory(Path.Combine(m, "No_Video", "frames"));
        Directory.CreateDirectory(Path.Combine(m, "2026-03-03-Tue", "Already"));
        MkVideo(Path.Combine(m, "Flat_A", "Flat_A.mp4"), "2026-03-01 09:00");
        File.WriteAllText(Path.Combine(m, "Flat_A", "meeting.md"), "");
        MkVideo(Path.Combine(m, "Flat_B", "Flat_B.mkv"), "2026-03-02 09:00");
        File.WriteAllText(Path.Combine(m, "No_Video", "meeting.md"), "x");
        Touch(Path.Combine(m, "No_Video", "meeting.md"), "2026-03-04 09:00");
        Touch(Path.Combine(m, "No_Video", "frames"), "2026-03-04 09:00");
        MkVideo(Path.Combine(m, "2026-03-03-Tue", "Already", "Already.mp4"), "2026-03-03 09:00");
        File.WriteAllText(Path.Combine(m, "stray.txt"), "x");
        MkVideo(Path.Combine(m, "2026-03-05", "Old", "Old.mp4"), "2026-03-05 09:00");
        MkVideo(Path.Combine(m, "03-06-2026", "Older", "Older.mp4"), "2026-03-06 09:00");
        MkVideo(Path.Combine(m, "2026-03-03", "Clash", "Clash.mp4"), "2026-03-03 10:00");
        MkVideo(Path.Combine(m, "2026-03-03", "Already", "Already.mp4"), "2026-03-03 11:00");
        MkVideo(Path.Combine(m, "03-05-2026-Thu", "Suffixed", "Suffixed.mp4"), "2026-03-05 12:00");
        // An imported day folder from an earlier step of the bash test.
        MkVideo(Path.Combine(m, "2026-01-05-Mon", "Weekly_Sync", "Weekly_Sync.mp4"));
    }

    public void Dispose() => _home.Dispose();

    private MeetingStore Store() => new(_m, Path.Combine(_m, "archives"), _log);
    private string P(params string[] parts) => Path.Combine([_m, .. parts]);

    [Fact]
    public void DryRun_MovesNothing()
    {
        var lines = Store().Migrate(dryRun: true);
        Assert.NotEmpty(lines);
        Assert.True(Directory.Exists(P("Flat_A")) && Directory.Exists(P("Flat_B")) && Directory.Exists(P("No_Video"))
            && Directory.Exists(P("2026-03-05")) && Directory.Exists(P("03-06-2026")));
    }

    [Fact]
    public void DryRun_Lines()
    {
        var lines = Store().Migrate(dryRun: true);
        Assert.Contains("Flat_A -> 2026-03-01-Sun/Flat_A", lines);
        Assert.Contains("03-06-2026 -> 2026-03-06-Fri", lines);
        Assert.Contains("2026-03-03 -> 2026-03-03-Tue (merge)", lines);
        Assert.Contains("2026-03-05 -> 2026-03-05-Thu", lines);
    }

    [Fact]
    public void FlatA_MovesUnderItsVideosDay_WithItsFiles()
    {
        Store().Migrate(false);
        Assert.True(File.Exists(P("2026-03-01-Sun", "Flat_A", "Flat_A.mp4")));
        Assert.True(File.Exists(P("2026-03-01-Sun", "Flat_A", "meeting.md")));
        Assert.False(Directory.Exists(P("Flat_A")));
    }

    [Fact]
    public void FlatB_Mkv()
    {
        Store().Migrate(false);
        Assert.True(File.Exists(P("2026-03-02-Mon", "Flat_B", "Flat_B.mkv")));
        Assert.False(Directory.Exists(P("Flat_B")));
    }

    [Fact]
    public void FolderWithoutVideo_UsesNewestFile()
    {
        Store().Migrate(false);
        Assert.True(File.Exists(P("2026-03-04-Wed", "No_Video", "meeting.md")));
        Assert.False(Directory.Exists(P("No_Video")));
    }

    [Fact]
    public void SuffixedDayFolders_AreLeftAlone()
    {
        Store().Migrate(false);
        Assert.True(File.Exists(P("2026-03-03-Tue", "Already", "Already.mp4")));
    }

    [Fact]
    public void DayFolderWithoutSuffix_GetsWeekday()
    {
        Store().Migrate(false);
        Assert.True(File.Exists(P("2026-03-05-Thu", "Old", "Old.mp4")));
        Assert.False(Directory.Exists(P("2026-03-05")));
    }

    [Fact]
    public void OldSpelling_IsTurnedAround()
    {
        Store().Migrate(false);
        Assert.True(File.Exists(P("2026-03-06-Fri", "Older", "Older.mp4")));
        Assert.False(Directory.Exists(P("03-06-2026")));
    }

    [Fact]
    public void OldSpellingWithSuffix_MergesIntoExistingDay()
    {
        Store().Migrate(false);
        Assert.True(File.Exists(P("2026-03-05-Thu", "Suffixed", "Suffixed.mp4")));
        Assert.False(Directory.Exists(P("03-05-2026-Thu")));
    }

    [Fact]
    public void Merge_IntoSuffixedFolder()
    {
        Store().Migrate(false);
        Assert.True(File.Exists(P("2026-03-03-Tue", "Clash", "Clash.mp4")));
        Assert.False(Directory.Exists(P("2026-03-03")));
    }

    [Fact]
    public void MergeClash_GetsSuffix_NothingOverwritten()
    {
        Store().Migrate(false);
        Assert.True(File.Exists(P("2026-03-03-Tue", "Already-2", "Already.mp4")));
        Assert.Equal(new DateTime(2026, 3, 3, 9, 0, 0), File.GetLastWriteTime(P("2026-03-03-Tue", "Already", "Already.mp4")));
    }

    [Fact]
    public void StrayFiles_AreLeftAlone()
    {
        Store().Migrate(false);
        Assert.True(File.Exists(P("stray.txt")));
    }

    [Fact]
    public void ExistingDayFolders_Stay()
    {
        Store().Migrate(false);
        Assert.True(File.Exists(P("2026-01-05-Mon", "Weekly_Sync", "Weekly_Sync.mp4")));
    }

    [Fact]
    public void SecondRun_IsNoOp()
    {
        var s = Store();
        s.Migrate(false);
        Assert.Empty(s.Migrate(dryRun: true));
        s.Migrate(false);
        Assert.True(File.Exists(P("2026-03-01-Sun", "Flat_A", "Flat_A.mp4")));
    }

    [Fact]
    public void ArchiveDirAndHiddenDirs_AreSkipped()
    {
        Directory.CreateDirectory(P("archives", "2026-01-01-Thu", "Old"));
        Directory.CreateDirectory(P(".git"));
        File.WriteAllText(P(".git", "HEAD"), "x");
        Store().Migrate(false);
        Assert.True(Directory.Exists(P("archives", "2026-01-01-Thu", "Old")));
        Assert.True(File.Exists(P(".git", "HEAD")));
    }

    [Fact]
    public void ImpossibleDateInDayLookingName_IsSkippedNotMoved()
    {
        Directory.CreateDirectory(P("2026-13-45", "M"));
        Store().Migrate(false);
        Assert.True(Directory.Exists(P("2026-13-45", "M")));
    }

    [Fact]
    public void NewestVideoWinsOverNewerNonVideo_AndNestedFilesCountWhenNoVideo()
    {
        Directory.CreateDirectory(P("Mixed"));
        MkVideo(P("Mixed", "m.mp4"), "2026-04-01 09:00");
        File.WriteAllText(P("Mixed", "meeting.md"), "x");
        Touch(P("Mixed", "meeting.md"), "2026-04-09 09:00");
        Directory.CreateDirectory(P("Deep", "frames"));
        File.WriteAllText(P("Deep", "frames", "f.jpg"), "x");
        Touch(P("Deep", "frames", "f.jpg"), "2026-05-02 09:00");
        Touch(P("Deep", "frames"), "2026-05-02 09:00");
        Touch(P("Deep"), "2026-01-01 09:00");
        Store().Migrate(false);
        Assert.True(Directory.Exists(P("2026-04-01-Wed", "Mixed")));
        Assert.True(Directory.Exists(P("2026-05-02-Sat", "Deep")));
    }

    [Fact]
    public void MissingMeetingsDir_Throws()
    {
        var s = new MeetingStore(P("nope"), P("nope", "archives"), _log);
        Assert.Throws<DirectoryNotFoundException>(() => s.Migrate(true));
    }
}
