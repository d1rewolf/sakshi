using System.Collections.Immutable;
using Sakshi.Core.Ports;
using Sakshi.Core.State;

namespace Sakshi.Core.Tests;

public sealed class StateTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sakshi-state-" + Guid.NewGuid().ToString("N"));
    private readonly string _path;
    private readonly FakeNotifier _notifier = new();
    private readonly FakeLog _log = new();

    public StateTests()
    {
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "state.json");
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private StateStore Store() => new(_path, _log, _notifier);

    private sealed class FakeLog : ILog
    {
        public List<string> Lines { get; } = [];
        public void Info(string message) => Lines.Add(message);
    }

    private sealed class FakeNotifier : INotifier
    {
        public List<(string Headline, string Body, Urgency Urgency)> Sent { get; } = [];
        public void Notify(string headline, string body, Urgency urgency) => Sent.Add((headline, body, urgency));
    }

    [Fact]
    public void SaveThenLoad_RoundTrips_WithJobs()
    {
        var t = new DateTimeOffset(2026, 11, 19, 10, 30, 5, TimeSpan.FromHours(2));
        var state = new SakshiState(
            new Capture(CapturePhase.Naming, t, "/v/a.mkv", t.AddMinutes(5), "x", new FrameChoice("--every 30", "file-meeting"), "1920x1080"),
            ImmutableList.Create(
                new TranscriptionJob(Guid.NewGuid(), "/d", "/d/a.mkv", "x", "--every 30", "file-meeting", 42, t),
                new TranscriptionJob(Guid.NewGuid(), "/e", "/e/b.mkv", "y", "", null)));

        Store().Save(state);
        var loaded = Store().Load();

        Assert.Equal(state.Capture, loaded.Capture);
        Assert.Equal(state.Jobs, loaded.Jobs);
        Assert.Contains("\"naming\"", File.ReadAllText(_path));
    }

    [Fact]
    public void Load_Missing_IsEmpty() => Assert.Same(SakshiState.Empty, Store().Load());

    [Fact]
    public void Load_Corrupt_KeepsBadFileNotifiesOnceStartsIdle()
    {
        File.WriteAllText(_path, "{\"capture\":");

        var loaded = Store().Load();

        Assert.Same(SakshiState.Empty, loaded);
        var bad = Assert.Single(Directory.GetFiles(_dir, "state.json.bad-*"));
        Assert.Equal("{\"capture\":", File.ReadAllText(bad));
        Assert.False(File.Exists(_path));
        var sent = Assert.Single(_notifier.Sent);
        Assert.Equal(Urgency.Critical, sent.Urgency);
        Assert.Equal("Sakshi", sent.Headline);
        Assert.Contains(bad, sent.Body);
        Assert.Single(_log.Lines);
    }

    [Fact]
    public void Save_IsAtomic_NoTmpLeftBehind()
    {
        Store().Save(SakshiState.Empty);
        Store().Save(SakshiState.Empty with { Capture = new Capture(CapturePhase.Recording, DateTimeOffset.UnixEpoch) });

        Assert.Equal(["state.json"], Directory.GetFiles(_dir).Select(Path.GetFileName).ToArray());
        Assert.Equal(CapturePhase.Recording, Store().Load().Capture.Phase);
    }

    [Fact]
    public void Load_CorruptTwiceInSameSecond_KeepsBothFiles()
    {
        File.WriteAllText(_path, "first{");
        Store().Load();
        File.WriteAllText(_path, "second{");
        Store().Load();

        var bad = Directory.GetFiles(_dir, "state.json.bad-*");
        Assert.Equal(2, bad.Length);
        Assert.Equal(["first{", "second{"], bad.Select(File.ReadAllText).Order().ToArray());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n")]
    [InlineData("{}")]
    [InlineData("{\"capture\":{\"phase\":\"idle\"}}")]
    [InlineData("{\"jobs\":[]}")]
    [InlineData("null")]
    public void Load_EmptyOrIncomplete_IsQuarantined(string content)
    {
        File.WriteAllText(_path, content);

        Assert.Same(SakshiState.Empty, Store().Load());
        var bad = Assert.Single(Directory.GetFiles(_dir, "state.json.bad-*"));
        Assert.Equal(content, File.ReadAllText(bad));
    }

    [Fact]
    public void Load_FileBrieflyLocked_RetriesAndLoads()
    {
        Store().Save(SakshiState.Empty with { Capture = new Capture(CapturePhase.Recording, DateTimeOffset.UnixEpoch) });
        using var held = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.None);
        var release = new Thread(() => { Thread.Sleep(250); held.Dispose(); });
        release.Start();

        var loaded = Store().Load();
        release.Join();

        Assert.Equal(CapturePhase.Recording, loaded.Capture.Phase);
        Assert.Empty(Directory.GetFiles(_dir, "state.json.bad-*"));
        Assert.Empty(_notifier.Sent);
    }

    [Fact]
    public void Load_FileStaysLocked_ThrowsAndKeepsFile()
    {
        Store().Save(SakshiState.Empty);
        using var held = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.None);

        Assert.ThrowsAny<IOException>(() => Store().Load());
        Assert.Empty(_notifier.Sent);
    }

    [Fact]
    public void Save_FileBrieflyLocked_RetriesAndSaves()
    {
        Store().Save(SakshiState.Empty);
        using var held = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.None);
        var release = new Thread(() => { Thread.Sleep(250); held.Dispose(); });
        release.Start();

        try { Store().Save(SakshiState.Empty with { Capture = new Capture(CapturePhase.Recording, DateTimeOffset.UnixEpoch) }); }
        finally { release.Join(); }

        Assert.Equal(CapturePhase.Recording, Store().Load().Capture.Phase);
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public void Save_TargetBrieflyInTheWay_RetriesTheRename()
    {
        // Something briefly occupying the name (here a directory; on Windows usually a reader's
        // lock) makes the final rename fail: it is retried rather than lost.
        Directory.CreateDirectory(_path);
        var release = new Thread(() => { Thread.Sleep(250); Directory.Delete(_path); });
        release.Start();

        try { Store().Save(SakshiState.Empty with { Capture = new Capture(CapturePhase.Recording, DateTimeOffset.UnixEpoch) }); }
        finally { release.Join(); }

        Assert.Equal(CapturePhase.Recording, Store().Load().Capture.Phase);
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public void Save_TargetStaysInTheWay_ThrowsAndLeavesNoTempFile()
    {
        Directory.CreateDirectory(_path);

        Assert.ThrowsAny<Exception>(() => Store().Save(SakshiState.Empty));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public void Save_CreatesMissingDirectory()
    {
        var nested = Path.Combine(_dir, "a", "b", "state.json");
        new StateStore(nested, _log, _notifier).Save(SakshiState.Empty);
        Assert.True(File.Exists(nested));
    }

    [Fact]
    public void Save_UsesCamelCasePropertyNames()
    {
        Store().Save(SakshiState.Empty);
        var text = File.ReadAllText(_path);
        Assert.Contains("\"capture\"", text);
        Assert.Contains("\"jobs\"", text);
        Assert.Contains("\"phase\": \"idle\"", text);
    }
}
