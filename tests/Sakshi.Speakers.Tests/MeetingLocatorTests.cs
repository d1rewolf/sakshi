using Sakshi.Speakers;
using Sakshi.Transcribe;

namespace Sakshi.Speakers.Tests;

public sealed class MeetingLocatorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sakshi-loc-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    [Fact]
    public void ExistingPathWins()
    {
        var d = Path.Combine(_root, "a", "b");
        Directory.CreateDirectory(d);
        Assert.Equal(d, MeetingLocator.Find(d, []));
    }

    [Fact]
    public void NameUnderTwoRootsReturnsTheNewest()
    {
        var meetings = Path.Combine(_root, "m");
        var archive = Path.Combine(_root, "a");
        Directory.CreateDirectory(Path.Combine(meetings, "2026-01-05-Mon", "standup"));
        Directory.CreateDirectory(Path.Combine(archive, "2026-02-09-Mon", "standup"));
        Directory.CreateDirectory(Path.Combine(archive, "2025-12-01-Mon", "standup"));
        Assert.Equal(Path.Combine(archive, "2026-02-09-Mon", "standup"), MeetingLocator.Find("standup", [meetings, archive]));
    }

    [Fact]
    public void NoMatchIsAUsageError()
    {
        var ex = Assert.Throws<UsageException>(() => MeetingLocator.Find("nope", [_root]));
        Assert.Contains("no meeting named nope under", ex.Message);
    }
}
