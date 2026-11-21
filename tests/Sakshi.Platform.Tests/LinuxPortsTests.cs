using Sakshi.Core.Ports;
using Sakshi.Platform;
using Sakshi.Platform.Linux;
using Sakshi.TestSupport;

namespace Sakshi.Platform.Tests;

public sealed class LinuxPortsTests : IDisposable
{
    private sealed class ListLog : ILog
    {
        public List<string> Lines { get; } = [];
        public void Info(string message) => Lines.Add(message);
    }

    private sealed class ThrowingRunner : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(string f, IReadOnlyList<string> a, TimeSpan t, CancellationToken c) => throw new InvalidOperationException("boom");
        public Task<int> RunAttachedAsync(string f, IReadOnlyList<string> a, IReadOnlyDictionary<string, string?> e, CancellationToken c) => throw new InvalidOperationException("boom");
        public int StartDetached(string f, IReadOnlyList<string> a, string? w = null) => throw new InvalidOperationException("boom");
        public bool IsRunning(string n) => false;
        public string? Which(string c) => throw new InvalidOperationException("boom");
    }

    private readonly string _root = Path.Combine(Path.GetTempPath(), "sakshi-ports-" + Guid.NewGuid().ToString("N"));
    private readonly FakeProcessRunner _runner = new();
    private readonly ListLog _log = new();

    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    [Theory]
    [InlineData(Urgency.Low, "low")]
    [InlineData(Urgency.Normal, "normal")]
    [InlineData(Urgency.Critical, "critical")]
    public async Task NotifySend_argv(Urgency u, string name)
    {
        _runner.OnPath["notify-send"] = "/usr/bin/notify-send";
        var n = new NotifySendNotifier(_runner, _log);
        n.Notify("Head", "Body", u);
        await n.Drain();
        var c = Assert.Single(_runner.Calls);
        Assert.Equal("notify-send", c.File);
        Assert.Equal(["-a", "Sakshi", "-u", name, "Head", "Body"], c.Args);
    }

    [Fact]
    public async Task Omarchy_variant_is_preferred()
    {
        _runner.OnPath["omarchy-notification-send"] = "/usr/bin/omarchy-notification-send";
        _runner.OnPath["notify-send"] = "/usr/bin/notify-send";
        var n = new NotifySendNotifier(_runner, _log);
        n.Notify("H", "B", Urgency.Critical);
        await n.Drain();
        var c = Assert.Single(_runner.Calls);
        Assert.Equal("omarchy-notification-send", c.File);
        Assert.Equal([0xf3, 0xb0, 0x91, 0x8a], System.Text.Encoding.UTF8.GetBytes(c.Args[3]).Select(b => (int)b).ToArray());
        Assert.Equal(["--app-name", "Sakshi", "-g", "\U000F044A", "-u", "critical", "H", "B"], c.Args);
    }

    [Fact]
    public async Task Notifier_swallows_failures()
    {
        var t = new NotifySendNotifier(new ThrowingRunner(), _log);
        t.Notify("H", "B", Urgency.Normal);
        await t.Drain();
        Assert.NotEmpty(_log.Lines);
        _runner.OnPath["notify-send"] = "/x";
        _runner.OnRun = (_, _) => throw new IOException("x");
        var n = new NotifySendNotifier(_runner, _log);
        n.Notify("H", "B", Urgency.Normal);
        n.Notify("H2", "B", Urgency.Normal);
        await n.Drain();
        Assert.Equal(2, _runner.Calls.Count);
    }

    [Fact]
    public async Task Notifier_skips_quietly_when_nothing_installed()
    {
        var n = new NotifySendNotifier(_runner, _log);
        n.Notify("H", "B", Urgency.Normal);
        await n.Drain();
        Assert.Empty(_runner.Calls);
        Assert.Empty(_log.Lines);
    }

    [Fact]
    public async Task Notify_does_not_block_and_keeps_order()
    {
        _runner.OnPath["notify-send"] = "/x";
        var n = new NotifySendNotifier(_runner, _log);
        for (var i = 0; i < 5; i++) n.Notify("h" + i, "b", Urgency.Low);
        await n.Drain();
        Assert.Equal(["h0", "h1", "h2", "h3", "h4"], _runner.Calls.Select(c => c.Args[4]).ToArray());
    }

    [Fact]
    public void Trash_argv()
    {
        new GioTrash(_runner).MoveToTrash("/m/a b");
        var c = Assert.Single(_runner.Calls);
        Assert.Equal("gio", c.File);
        Assert.Equal(["trash", "--", "/m/a b"], c.Args);
    }

    [Fact]
    public void Trash_failure_throws_with_stderr()
    {
        _runner.OnRun = (_, _) => new ProcessResult(1, "", "gio: nope");
        var e = Assert.Throws<IOException>(() => new GioTrash(_runner).MoveToTrash("/x"));
        Assert.Contains("gio: nope", e.Message);
    }

    [Fact]
    public void Trash_missing_gio_throws_IOException()
    {
        Assert.Throws<IOException>(() => new GioTrash(new ThrowingRunner()).MoveToTrash("/x"));
    }
}
