using Sakshi.App.Tray;
using Sakshi.Core.Ports;
using Sakshi.Core.State;

namespace Sakshi.App.Tests;

public sealed class TrayBackgroundTests : IAsyncDisposable
{
    private readonly TrayFixture _f = new();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask DisposeAsync() => _f.DisposeAsync();

    private static readonly DateTimeOffset Started = new(new DateTime(2026, 10, 14, 10, 0, 0, DateTimeKind.Local));

    private void Recording()
    {
        _f.ObsRunning(true);
        _f.Set(new Capture(CapturePhase.Recording, Started));
    }

    private int Reminders => _f.Prompter.Shown.Count(s => s.StartsWith("Recording for", StringComparison.Ordinal));

    // ---------------------------------------------------------------- reminder

    [Fact]
    public async Task Reminder_FiresOncePerMark_WithBashTextAndRows()
    {
        Recording();
        var c = _f.Controller();
        _f.Prompter.Answer("Keep recording");

        _f.Clock.Now = Started.AddMinutes(29);
        await c.ReminderTickAsync(Ct);
        Assert.Equal(0, Reminders);

        _f.Clock.Now = Started.AddMinutes(31);
        await c.ReminderTickAsync(Ct);
        await c.ReminderTickAsync(Ct);
        Assert.Equal(["Recording for 30 min"], _f.Prompter.Shown);
        Assert.Equal(["Keep recording", "Stop recording"], _f.Prompter.Rows[0].Select(r => r.Label));
        Assert.Equal(TimeSpan.FromSeconds(600), _f.Prompter.Timeouts[0]);
        Assert.Equal(CapturePhase.Recording, _f.Capture.Phase);
        Assert.Empty(_f.Obs.Calls);

        _f.Clock.Now = Started.AddMinutes(61);
        await c.ReminderTickAsync(Ct); // dismissed (no answer): keeps recording
        Assert.Equal(["Recording for 30 min", "Recording for 1 h"], _f.Prompter.Shown);
        Assert.Equal(CapturePhase.Recording, _f.Capture.Phase);
        Assert.Empty(_f.Obs.Calls);
    }

    [Fact]
    public async Task Reminder_StopRecording_Stops()
    {
        Recording();
        _f.Obs.Recording = false;
        var c = _f.Controller();
        _f.Prompter.Answer("Stop recording");
        _f.Clock.Now = Started.AddMinutes(31);

        await c.ReminderTickAsync(Ct);

        Assert.Contains("IsRecording", _f.Obs.Calls);
        Assert.Equal(CapturePhase.Idle, _f.Capture.Phase);
        Assert.Contains("reminder: stop confirmed", _f.Log.Lines);
    }

    [Fact]
    public async Task Reminder_NewRecording_GetsANewSchedule()
    {
        Recording();
        var c = _f.Controller();
        _f.Clock.Now = Started.AddMinutes(31);
        await c.ReminderTickAsync(Ct);

        var second = Started.AddMinutes(40);
        _f.Set(new Capture(CapturePhase.Recording, second));
        _f.Clock.Now = second.AddMinutes(10);
        await c.ReminderTickAsync(Ct);
        _f.Clock.Now = second.AddMinutes(30);
        await c.ReminderTickAsync(Ct);

        Assert.Equal(["Recording for 30 min", "Recording for 30 min"], _f.Prompter.Shown);
    }

    [Fact]
    public async Task Reminder_Off_WhenIntervalIsZero()
    {
        _f.Config = _f.Config with { RemindInterval = 0 };
        Recording();
        var c = _f.Controller();
        _f.Clock.Now = Started.AddHours(3);

        await c.ReminderTickAsync(Ct);

        Assert.Empty(_f.Prompter.Shown);
    }

    [Fact]
    public async Task Reminder_WaitsForAStopInProgress_AndPromptsOnlyAfterIt()
    {
        Recording();
        _f.Obs.StatusFails = true; // the stop fails and the recording goes on
        _f.ConnectBlock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sentWhenShown = -1;
        _f.Prompter.OnShown = _ => sentWhenShown = _f.Sent.Count;
        var c = _f.Controller();
        var stop = c.RunAsync(["stop"], Ct);
        await _f.ConnectEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);

        _f.Clock.Now = Started.AddMinutes(31);
        var tick = c.ReminderTickAsync(Ct);
        await Task.Delay(100, Ct);
        Assert.False(tick.IsCompleted);
        Assert.Empty(_f.Prompter.Shown);

        _f.ConnectBlock.SetResult();
        Assert.Equal(1, (await stop).ExitCode);
        await tick.WaitAsync(TimeSpan.FromSeconds(5), Ct);

        Assert.Equal(["Recording for 30 min"], _f.Prompter.Shown);
        // The stop's failure was reported before the reminder prompt opened.
        var dieAt = _f.Sent.FindIndex(n => n.Urgency == Urgency.Critical);
        Assert.InRange(dieAt, 0, sentWhenShown - 1);
    }

    [Fact]
    public async Task Reminder_WhoseRecordingEndedWhileWaiting_DoesNotPrompt()
    {
        Recording();
        _f.Obs.Recording = false; // the stop finds nothing recording and clears the state
        _f.ConnectBlock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var c = _f.Controller();
        var stop = c.RunAsync(["stop"], Ct);
        await _f.ConnectEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);

        _f.Clock.Now = Started.AddMinutes(31);
        var tick = c.ReminderTickAsync(Ct);
        _f.ConnectBlock.SetResult();
        await stop;
        await tick.WaitAsync(TimeSpan.FromSeconds(5), Ct);

        Assert.Empty(_f.Prompter.Shown);
    }

    [Fact]
    public async Task Toggle_WhileTheReminderIsOpen_ClosesIt_AndStops()
    {
        Recording();
        _f.Obs.Recording = false;
        var open = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var shown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _f.Prompter.Gate = open.Task; // never answered: only the cancellation ends it
        _f.Prompter.OnShown = _ => shown.TrySetResult();
        var c = _f.Controller();
        _f.Clock.Now = Started.AddMinutes(31);
        var tick = c.ReminderTickAsync(Ct);
        await shown.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);

        var r = await c.RunAsync(["toggle"], Ct).WaitAsync(TimeSpan.FromSeconds(5), Ct);
        await tick.WaitAsync(TimeSpan.FromSeconds(5), Ct);

        Assert.Equal(0, r.ExitCode);
        Assert.Contains("IsRecording", _f.Obs.Calls);
        Assert.Equal(CapturePhase.Idle, _f.Capture.Phase);
    }

    [Fact]
    public async Task ReminderLoop_WakesEvery15Seconds()
    {
        Recording();
        var wakes = new SemaphoreSlim(0);
        _f.OnDelay = async (d, ct) =>
        {
            if (d == TimeSpan.FromSeconds(15)) await wakes.WaitAsync(ct);
        };
        _f.Prompter.Answer("Keep recording");
        var c = _f.Controller();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var bg = c.StartBackgroundAsync(cts.Token);

        _f.Clock.Now = Started.AddMinutes(31);
        wakes.Release();
        await TrayFixture.Until(() => Reminders == 1, "the reminder prompt");
        wakes.Release();
        wakes.Release();
        await TrayFixture.Until(() => _f.Delays.Count(d => d == TimeSpan.FromSeconds(15)) >= 4, "three more wakes");
        Assert.Equal(1, Reminders);

        await cts.CancelAsync();
        await bg.WaitAsync(TimeSpan.FromSeconds(5), Ct);
    }

    // ---------------------------------------------------------------- queue

    [Fact]
    public async Task Background_RecoversJobs_ThenRunsTheQueue()
    {
        var dir = Path.Combine(_f.Meetings, "2026-10-14-Tue", "Budget");
        var job = new TranscriptionJob(Guid.NewGuid(), dir, Path.Combine(dir, "Budget.mp4"), "Budget", "", null);
        _f.Store.Update(s => s with { Jobs = s.Jobs.Add(job) });
        var c = _f.Controller();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var bg = c.StartBackgroundAsync(cts.Token);
        var ran = await _f.Transcriber.Ran.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);

        Assert.Equal(job.Id, ran.Id);
        await TrayFixture.Until(() => _f.Store.Load().Jobs.IsEmpty, "the job to finish");
        await cts.CancelAsync();
        await bg.WaitAsync(TimeSpan.FromSeconds(5), Ct);
    }

    [Fact]
    public async Task Background_RetriesRecover_BeforeTheQueueRuns()
    {
        Directory.CreateDirectory(_f.StateFile); // the state cannot be read at first
        var dir = Path.Combine(_f.Meetings, "2026-10-14-Tue", "Budget");
        var job = new TranscriptionJob(Guid.NewGuid(), dir, Path.Combine(dir, "Budget.mp4"), "Budget", "", null, Pid: 4242, Started: Started);
        var backoffs = 0;
        _f.OnDelay = (d, ct) =>
        {
            if (d == TimeSpan.FromSeconds(15)) return Task.Delay(Timeout.Infinite, ct);
            if (d == TimeSpan.FromSeconds(5) && Interlocked.Increment(ref backoffs) == 1)
            {
                Directory.Delete(_f.StateFile); // readable again, with a job whose run died
                _f.Store.Update(s => s with { Jobs = s.Jobs.Add(job) });
            }
            return Task.CompletedTask;
        };
        var c = _f.Controller();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var bg = c.StartBackgroundAsync(cts.Token);

        var ran = await _f.Transcriber.Ran.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);

        Assert.Equal(job.Id, ran.Id);
        Assert.Equal(1, Volatile.Read(ref backoffs));
        // Recover ran (it reported the interrupted run) and RunAsync never ran on the unreadable state.
        Assert.Contains(_f.Sent, n => n.Headline == "Transcription interrupted: Budget");
        Assert.Contains(_f.Log.Lines, l => l.StartsWith("transcription queue: recovery failed:", StringComparison.Ordinal)
            && l.EndsWith("retrying in 5 s", StringComparison.Ordinal));
        Assert.DoesNotContain(_f.Log.Lines, l => l.StartsWith("transcription queue stopped:", StringComparison.Ordinal));
        await cts.CancelAsync();
        await bg.WaitAsync(TimeSpan.FromSeconds(5), Ct);
    }

    [Fact]
    public async Task Background_QueueRestarts_AfterAnIoError()
    {
        var dir = Path.Combine(_f.Meetings, "2026-10-14-Tue", "Budget");
        var first = new TranscriptionJob(Guid.NewGuid(), dir, Path.Combine(dir, "Budget.mp4"), "Budget", "", null);
        var second = first with { Id = Guid.NewGuid(), Name = "Retro" };
        _f.Store.Update(s => s with { Jobs = s.Jobs.Add(first) });
        // While the first job runs the disk goes bad: removing the finished job fails and RunAsync ends.
        _f.Transcriber.OnRun = j =>
        {
            if (j.Id != first.Id) return;
            File.Delete(_f.StateFile);
            Directory.CreateDirectory(_f.StateFile);
        };
        var backoffs = 0;
        _f.OnDelay = (d, ct) =>
        {
            if (d == TimeSpan.FromSeconds(15)) return Task.Delay(Timeout.Infinite, ct);
            if (d == TimeSpan.FromSeconds(5) && Interlocked.Increment(ref backoffs) == 1)
            {
                Directory.Delete(_f.StateFile); // the disk recovers, and another job is waiting
                _f.Store.Update(s => s with { Jobs = s.Jobs.Add(second) });
            }
            return Task.CompletedTask;
        };
        var c = _f.Controller();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var bg = c.StartBackgroundAsync(cts.Token);

        await TrayFixture.Until(() => _f.Transcriber.Runs.Any(j => j.Id == second.Id), "the queue to run again");

        Assert.Equal(1, Volatile.Read(ref backoffs));
        Assert.Contains(_f.Log.Lines, l => l.StartsWith("transcription queue stopped:", StringComparison.Ordinal)
            && l.EndsWith("restarting in 5 s", StringComparison.Ordinal));
        await cts.CancelAsync();
        await bg.WaitAsync(TimeSpan.FromSeconds(5), Ct);
    }

    [Fact]
    public async Task Background_ClearsAStartingStateLeftByAnEarlierTray()
    {
        _f.ObsRunning(true);
        _f.Set(new Capture(CapturePhase.Starting, Started));
        var c = _f.Controller();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var bg = c.StartBackgroundAsync(cts.Token);

        await TrayFixture.Until(() => _f.Capture.Phase == CapturePhase.Idle, "the stale start to be cleared");
        await cts.CancelAsync();
        await bg.WaitAsync(TimeSpan.FromSeconds(5), Ct);
    }

    [Fact]
    public async Task Background_KeepsARecordingLeftByAnEarlierTray()
    {
        Recording();
        var c = _f.Controller();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var bg = c.StartBackgroundAsync(cts.Token);
        await cts.CancelAsync();
        await bg.WaitAsync(TimeSpan.FromSeconds(5), Ct);

        Assert.Equal(CapturePhase.Recording, _f.Capture.Phase);
    }

    [Fact]
    public async Task Background_EndsQuietly_OnCancellation()
    {
        var c = _f.Controller();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var bg = c.StartBackgroundAsync(cts.Token);

        await cts.CancelAsync();

        await bg.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.True(bg.IsCompletedSuccessfully);
    }
}
