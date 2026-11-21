using Sakshi.Core.Ports;
using Sakshi.Platform;
using Sakshi.Platform.MacOS;
using Sakshi.TestSupport;

namespace Sakshi.Platform.Tests;

public sealed class NotifierTests
{
    private sealed class ListLog : ILog
    {
        public List<string> Lines { get; } = [];
        public void Info(string message) { lock (Lines) Lines.Add(message); }
    }

    private readonly ListLog _log = new();

    [Fact]
    public void Osascript_passes_text_as_arguments()
    {
        var (file, args) = OsascriptNotifier.Command("Say \"hi\" \\ there", "a \"body\"\\");
        Assert.Equal("osascript", file);
        Assert.Equal(["-e", "on run argv",
            "-e", "display notification (item 2 of argv) with title \"Sakshi\" subtitle (item 1 of argv)",
            "-e", "end run",
            "Say \"hi\" \\ there", "a \"body\"\\"], args);
    }

    [Fact]
    public async Task Osascript_notifier_runs_the_command_and_ignores_urgency()
    {
        var runner = new FakeProcessRunner();
        var n = new OsascriptNotifier(runner, _log);
        n.Notify("H", "B", Urgency.Critical);
        await n.Drain();
        var c = Assert.Single(runner.Calls);
        Assert.Equal("osascript", c.File);
        Assert.Equal(OsascriptNotifier.Command("H", "B").Args, c.Args);
    }

    [Fact]
    public async Task Osascript_failure_is_logged_not_thrown()
    {
        var runner = new FakeProcessRunner { OnRun = (_, _) => new ProcessResult(1, "", "no permission") };
        var n = new OsascriptNotifier(runner, _log);
        n.Notify("H", "B", Urgency.Normal);
        await n.Drain();
        Assert.Contains(_log.Lines, l => l.Contains("osascript exit 1") && l.Contains("no permission"));
    }

    [Fact]
    public async Task QueuedNotifier_sends_in_order_and_survives_a_throwing_send()
    {
        var sent = new List<string>();
        var n = new QueuedNotifier(async (h, b, u) =>
        {
            await Task.Yield();
            if (h == "bad") throw new InvalidOperationException("boom");
            lock (sent) sent.Add($"{h}/{b}/{u}");
        }, _log);
        n.Notify("a", "1", Urgency.Low);
        n.Notify("bad", "2", Urgency.Normal);
        n.Notify("c", "3", Urgency.Critical);
        await n.Drain();
        Assert.Equal(["a/1/Low", "c/3/Critical"], sent);
        Assert.Contains(_log.Lines, l => l.Contains("boom"));
    }

    [Fact]
    public async Task QueuedNotifier_survives_a_send_that_throws_before_returning_a_task()
    {
        var n = new QueuedNotifier((_, _, _) => throw new InvalidOperationException("sync boom"), _log);
        n.Notify("a", "1", Urgency.Low);
        await n.Drain();
        Assert.Contains(_log.Lines, l => l.Contains("sync boom"));
    }

    [Fact]
    public async Task QueuedNotifier_does_not_block_the_caller()
    {
        var gate = new TaskCompletionSource();
        var n = new QueuedNotifier((_, _, _) => gate.Task, _log);
        n.Notify("a", "1", Urgency.Low);   // returns while the send waits
        Assert.False(n.Drain().IsCompleted);
        gate.SetResult();
        await n.Drain();
    }
}
