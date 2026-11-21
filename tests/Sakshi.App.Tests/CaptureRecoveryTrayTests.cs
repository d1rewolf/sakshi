using Sakshi.App.Tray;
using Sakshi.Core.Obs;
using Sakshi.Core.Ports;
using Sakshi.Core.Recording;
using Sakshi.Core.State;

namespace Sakshi.App.Tests;

/// <summary>The tray's side of mending a frozen capture: the "repick" command and the freeze check's turn at the gate.</summary>
public sealed class CaptureRecoveryTrayTests : IAsyncDisposable
{
    private readonly TrayFixture _f = new();
    private readonly FakeRecovery _recovery = new();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask DisposeAsync() => _f.DisposeAsync();

    private sealed class FakeRecovery : ICaptureRecovery
    {
        public int Repicks;
        public int SizeQueries;
        public Exception? RepickFailure { get; set; }

        public Task<IReadOnlySet<PixelSize>> WindowSizesAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref SizeQueries);
            return Task.FromResult<IReadOnlySet<PixelSize>>(new HashSet<PixelSize>());
        }

        public Task ReconnectAsync(ObsClient obs, CancellationToken ct) => Task.CompletedTask;

        public TaskCompletionSource? RepickBlock { get; set; }
        public TaskCompletionSource RepickEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task RepickAsync(ObsClient obs, string scene, string source, CancellationToken ct)
        {
            Interlocked.Increment(ref Repicks);
            RepickEntered.TrySetResult();
            if (RepickBlock is { } b) await b.Task.WaitAsync(ct);
            if (RepickFailure is { } e) throw e;
        }
    }

    private FreezeWatch Watch() => new(_f.Config, _recovery, _f.Notifier, _f.Log, _f.Clock);

    [Fact]
    public async Task Repick_without_a_recovery_says_where_it_works()
    {
        _f.Set(new Capture(CapturePhase.Recording, _f.Clock.Now));
        var r = await _f.Controller().RunAsync(["repick"], Ct);

        Assert.Equal(1, r.ExitCode);
        Assert.Contains(_f.Notifier.Sent, n => n is (Urgency.Critical, "Sakshi", "Re-picking the window while recording works only on Linux under Hyprland."));
    }

    [Fact]
    public async Task Repick_when_not_recording_does_nothing()
    {
        _f.Recovery = _recovery;
        var r = await _f.Controller().RunAsync(["repick"], Ct);

        Assert.Equal(0, r.ExitCode);
        Assert.Equal(0, _recovery.Repicks);
        Assert.Contains(_f.Notifier.Sent, n => n is (Urgency.Low, "Sakshi", "Not recording — nothing to re-pick."));
    }

    [Fact]
    public async Task Repick_while_recording_asks_the_recovery_and_keeps_recording()
    {
        _f.Recovery = _recovery;
        _f.UseObsServer();
        var started = _f.Clock.Now;
        _f.Set(new Capture(CapturePhase.Recording, started));

        var r = await _f.Controller().RunAsync(["repick"], Ct);

        Assert.Equal(0, r.ExitCode);
        Assert.Equal(1, _recovery.Repicks);
        Assert.Equal(new Capture(CapturePhase.Recording, started), _f.Capture);
    }

    [Theory]
    [InlineData("stop")]
    [InlineData("toggle")]
    public async Task A_stop_during_a_repick_is_told_to_finish_the_picker(string command)
    {
        _f.Recovery = _recovery;
        _f.UseObsServer();
        _recovery.RepickBlock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _f.Set(new Capture(CapturePhase.Recording, _f.Clock.Now));
        var c = _f.Controller();
        var repick = c.RunAsync(["repick"], Ct);
        await _recovery.RepickEntered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        var r = await c.RunAsync([command], Ct);

        Assert.Equal(0, r.ExitCode);
        Assert.Contains(_f.Notifier.Sent, n => n is (Urgency.Low, "Sakshi", "Still busy — finish the window picker first."));
        _recovery.RepickBlock.SetResult();
        Assert.Equal(0, (await repick).ExitCode);
    }

    [Fact]
    public async Task A_failed_repick_is_reported()
    {
        _f.Recovery = _recovery;
        _f.UseObsServer();
        _recovery.RepickFailure = new InvalidOperationException("OBS did not switch to the scratch scene collection");
        _f.Set(new Capture(CapturePhase.Recording, _f.Clock.Now));

        var r = await _f.Controller().RunAsync(["repick"], Ct);

        Assert.Equal(1, r.ExitCode);
        Assert.Contains(_f.Notifier.Sent, n => n.Body == "Could not re-pick the window: OBS did not switch to the scratch scene collection");
    }

    [Fact]
    public async Task The_freeze_check_runs_only_while_recording()
    {
        _f.Recovery = _recovery;
        _f.UseObsServer();
        var c = _f.Controller();
        var watch = Watch();

        await c.FreezeTickAsync(watch, Ct);
        Assert.Empty(_f.ObsServer!.Requests);

        _f.Set(new Capture(CapturePhase.Recording, _f.Clock.Now));
        await c.FreezeTickAsync(watch, Ct);
        Assert.Contains("GetRecordStatus", _f.ObsServer.Requests);
    }

    [Fact]
    public async Task The_freeze_check_steps_aside_while_the_gate_is_held()
    {
        // The reminder prompt holds the gate while it is open: the check must not wait behind it.
        _f.Recovery = _recovery;
        _f.UseObsServer();
        _f.Set(new Capture(CapturePhase.Recording, _f.Clock.Now));
        _f.Config = _f.Config with { RemindInterval = 60 };
        var open = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var shown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _f.Prompter.Gate = open.Task;
        _f.Prompter.OnShown = _ => shown.TrySetResult();
        var c = _f.Controller();
        _f.Clock.Now += TimeSpan.FromMinutes(1);
        var reminder = c.ReminderTickAsync(Ct);
        await shown.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        await c.FreezeTickAsync(Watch(), Ct).WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.Empty(_f.ObsServer!.Requests);

        open.SetResult();
        await reminder.WaitAsync(TimeSpan.FromSeconds(10), Ct);
    }
}
