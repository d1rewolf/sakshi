using System.Globalization;
using Sakshi.Core.Store;
using Sakshi.TestSupport;
using static Sakshi.Core.Tests.StoreTestHelpers;

namespace Sakshi.Core.Tests;

[Collection("Environment")]
public sealed class HousekeepTests : IDisposable
{
    private readonly TempHome _home = new();
    private readonly RecordingLog _log = new();
    private readonly string _h;
    private readonly string _archive;
    private readonly DateTime _today = DateTime.Today;
    private readonly string _old, _edge, _recent, _now, _busyDay;

    public HousekeepTests()
    {
        _h = _home.Meetings;
        _archive = Path.Combine(_h, "archives");
        _old = Day(_today.AddDays(-20));
        _edge = Day(_today.AddDays(-14));
        _recent = Day(_today.AddDays(-3));
        _now = Day(_today);
        _busyDay = Day(_today.AddDays(-30));
        foreach (var p in new[]
        {
            Path.Combine(_h, _old, "Old"), Path.Combine(_h, _edge, "Edge"), Path.Combine(_h, _recent, "Recent"),
            Path.Combine(_h, _now, "Now"), Path.Combine(_h, "Flat"), Path.Combine(_archive, _old, "Clash"),
            Path.Combine(_h, _old, "Clash"), Path.Combine(_h, _busyDay, "Busy"),
        })
            Directory.CreateDirectory(p);
        MkVideo(Path.Combine(_h, _old, "Old", "Old.mp4"), _today.AddDays(-20).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " 09:00");
        File.WriteAllText(Path.Combine(_h, _old, "Clash", "x"), "new");
        File.WriteAllText(Path.Combine(_archive, _old, "Clash", "x"), "old");
    }

    public void Dispose() => _home.Dispose();

    private MeetingStore Store() => new(_h, _archive, _log);
    private string Busy => Path.Combine(_h, _busyDay, "Busy");
    private DateOnly Today => DateOnly.FromDateTime(_today);

    [Fact]
    public void DryRun_MovesNothing_ListsMergeAndBusySkip()
    {
        var lines = Store().Housekeep(14, Today, [Busy], dryRun: true);
        Assert.True(Directory.Exists(Path.Combine(_h, _old, "Old")));
        Assert.True(Directory.Exists(Busy));
        Assert.Contains($"{_old} -> archives/{_old} (merge)", lines);
        Assert.Contains($"{_busyDay}: skipped, still transcribing Busy", lines);
    }

    [Fact]
    public void Housekeep_DryRunLines_MatchBash()
    {
        var lines = Store().Housekeep(14, Today, [Busy], dryRun: true);
        Assert.Equal(
            [$"{_busyDay}: skipped, still transcribing Busy", $"{_old} -> archives/{_old} (merge)"],
            lines.OrderBy(l => l, StringComparer.Ordinal).ToArray());
        Assert.Empty(_log.Lines);
    }

    [Fact]
    public void FailedMove_SaysExactBashLine_AndLogsReasonSeparately()
    {
        // A file where the archive dir should be makes the archive unusable.
        var blocker = Path.Combine(_home.Root, "blocker");
        File.WriteAllText(blocker, "file in the way");
        var s = new MeetingStore(_h, blocker, _log);
        var lines = s.Housekeep(14, Today, [], dryRun: false);
        var line = lines.Single(l => l.StartsWith($"could not move {Path.Combine(_h, _old)} ", StringComparison.Ordinal));
        Assert.Equal($"could not move {Path.Combine(_h, _old)} to {Path.Combine(blocker, _old)}", line);
        Assert.True(Directory.Exists(Path.Combine(_h, _old)));
        Assert.Contains(_log.Lines, l => l == "housekeep: " + line);
        Assert.Contains(_log.Lines, l => l.StartsWith("housekeep: ", StringComparison.Ordinal) && l != "housekeep: " + line);
    }

    [Fact]
    public void ArchivesOldDay_MergeKeepsBoth_EdgeRecentTodayAndNonDayStay()
    {
        Store().Housekeep(14, Today, [Busy], dryRun: false);
        Assert.True(File.Exists(Path.Combine(_archive, _old, "Old", "Old.mp4")));
        Assert.False(Directory.Exists(Path.Combine(_h, _old)));
        Assert.Equal("old", File.ReadAllText(Path.Combine(_archive, _old, "Clash", "x")));
        Assert.Equal("new", File.ReadAllText(Path.Combine(_archive, _old, "Clash-2", "x")));
        Assert.True(Directory.Exists(Path.Combine(_h, _edge, "Edge")), "day exactly keepDays old stays");
        Assert.True(Directory.Exists(Path.Combine(_h, _recent, "Recent")));
        Assert.True(Directory.Exists(Path.Combine(_h, _now, "Now")));
        Assert.True(Directory.Exists(Busy), "busy folder stays");
        Assert.True(Directory.Exists(Path.Combine(_h, "Flat")));
        Assert.Contains(_log.Lines, l => l.StartsWith("housekeep: ", StringComparison.Ordinal));
    }

    [Fact]
    public void OnceTranscribingIsOver_TheBusyDayGoes_AndSecondRunIsNoOp()
    {
        var s = Store();
        s.Housekeep(14, Today, [Busy], dryRun: false);
        s.Housekeep(14, Today, [], dryRun: false);
        Assert.True(Directory.Exists(Path.Combine(_archive, _busyDay, "Busy")));
        Assert.False(Directory.Exists(Path.Combine(_h, _busyDay)));
        Assert.Empty(s.Housekeep(14, Today, [], dryRun: true));
    }

    [Fact]
    public void KeepDaysZero_ArchivesNothing()
    {
        Directory.CreateDirectory(Path.Combine(_h, _old, "Again"));
        Assert.Empty(Store().Housekeep(0, Today, [], dryRun: false));
        Assert.Empty(Store().Housekeep(-3, Today, [], dryRun: false));
        Assert.True(Directory.Exists(Path.Combine(_h, _old, "Again")));
    }

    [Fact]
    public void OldSpellingDayFolder_IsArchivedUnderItsOwnName()
    {
        var d = _today.AddDays(-40);
        var name = d.ToString("MM-dd-yyyy", CultureInfo.InvariantCulture);
        Directory.CreateDirectory(Path.Combine(_h, name, "M"));
        Store().Housekeep(14, Today, [], dryRun: false);
        Assert.True(Directory.Exists(Path.Combine(_archive, name, "M")));
    }

    [Fact]
    public void BusyDirNotUnderADay_DoesNotBlockSiblingDays()
    {
        var other = Path.Combine(_h, _recent, "Recent");
        var lines = Store().Housekeep(14, Today, [other], dryRun: true);
        Assert.DoesNotContain(lines, l => l.Contains("skipped", StringComparison.Ordinal));
    }

    [Fact]
    public void Migrate_LeavesTheArchiveAlone()
    {
        Store().Housekeep(14, Today, [], dryRun: false);
        Store().Migrate(dryRun: false);
        Assert.True(Directory.Exists(Path.Combine(_archive, _old, "Old")));
        Assert.False(Directory.Exists(Path.Combine(_h, _now, "archives")));
    }

    [Fact]
    public void MissingMeetingsDir_ReturnsNothing()
    {
        var s = new MeetingStore(Path.Combine(_home.Root, "nope"), Path.Combine(_home.Root, "nope", "archives"), _log);
        Assert.Empty(s.Housekeep(14, Today, [], dryRun: true));
    }
}
