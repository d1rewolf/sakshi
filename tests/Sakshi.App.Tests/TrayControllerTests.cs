using Sakshi.App.Tray;
using Sakshi.Core.Ports;
using Sakshi.Core.State;

namespace Sakshi.App.Tests;

public sealed class TrayControllerTests : IAsyncDisposable
{
    private const string StillBusy = "Still busy — finish the window picker first.";
    private const string TypeTheName = "Type the meeting name in the prompt and press Enter.";

    private readonly TrayFixture _f = new();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask DisposeAsync() => _f.DisposeAsync();

    private static Task<CommandResult> Run(TrayController c, params string[] args) => c.RunAsync(args, Ct);

    // ---------------------------------------------------------------- toggle routing

    [Fact]
    public async Task Toggle_WhenIdle_Starts()
    {
        var r = await Run(_f.Controller(), "toggle");

        Assert.Equal(1, _f.Launcher.Calls);
        Assert.Equal(1, r.ExitCode); // the fake launcher fails: Starter's die
        Assert.Contains(_f.Sent, n => n is (Urgency.Critical, "Sakshi", "OBS is not available in tests"));
        Assert.Empty(_f.Obs.Calls);
    }

    [Fact]
    public async Task Toggle_WhenRecording_Stops()
    {
        _f.ObsRunning(true);
        _f.Obs.Recording = false;
        _f.Set(new Capture(CapturePhase.Recording, _f.Clock.Now));

        var r = await Run(_f.Controller(), "toggle");

        Assert.Equal(0, r.ExitCode);
        Assert.Equal(0, _f.Launcher.Calls);
        Assert.Contains("IsRecording", _f.Obs.Calls);
        Assert.Contains(_f.Sent, n => n is (Urgency.Low, "Not recording", "OBS has no active recording."));
        Assert.Equal(CapturePhase.Idle, _f.Capture.Phase);
    }

    [Fact]
    public async Task Toggle_WhenNaming_ResumesTheStop()
    {
        var video = _f.MkVideo(Path.Combine(_f.Rec, "2026-10-14 10-00-00.mp4"));
        _f.Set(new Capture(CapturePhase.Naming, Video: video, Stopped: _f.Clock.Now));
        _f.Prompter.Answer("Budget");

        var r = await Run(_f.Controller(), "toggle");

        Assert.Equal(0, r.ExitCode);
        Assert.Equal(["Meeting name"], _f.Prompter.Shown);
        Assert.Equal(0, _f.Launcher.Calls);
        Assert.False(File.Exists(video));
        Assert.Single(_f.Store.Load().Jobs, j => j.Name == "Budget");
        Assert.Equal(CapturePhase.Idle, _f.Capture.Phase);
    }

    [Theory]
    [InlineData("toggle")]
    [InlineData("start")]
    public async Task WhileAStartIsInProgress_ToggleAndStart_SayStillBusy_AndDoNothing(string second)
    {
        var c = _f.Controller();
        _f.Launcher.Block = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = Run(c, "start");
        await _f.Launcher.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);

        // Answered at once, not queued behind the start.
        var r = await Run(c, second).WaitAsync(TimeSpan.FromSeconds(5), Ct);

        Assert.Equal(0, r.ExitCode);
        Assert.Contains(_f.Sent, n => n is (Urgency.Low, "Sakshi", StillBusy));
        Assert.Equal(1, _f.Launcher.Calls);
        Assert.False(first.IsCompleted);

        _f.Launcher.Block.SetResult();
        await first;
        Assert.Equal(1, _f.Launcher.Calls);
    }

    [Fact]
    public async Task Toggle_WithStartingStateWrittenByThisStart_SaysStillBusy()
    {
        var c = _f.Controller();
        _f.Launcher.Block = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = Run(c, "toggle");
        await _f.Launcher.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        _f.Set(new Capture(CapturePhase.Starting, _f.Clock.Now)); // the picker is open

        var r = await Run(c, "toggle").WaitAsync(TimeSpan.FromSeconds(5), Ct);

        Assert.Equal(0, r.ExitCode);
        Assert.Equal(1, _f.Sent.Count(n => n is (Urgency.Low, "Sakshi", StillBusy)));
        Assert.Empty(_f.Obs.Calls);
        _f.Launcher.Block.SetResult();
        await first;
    }

    [Fact]
    public async Task Toggle_WhenStateSaysStarting_SaysStillBusy_AsBash()
    {
        _f.ObsRunning(true);
        _f.Set(new Capture(CapturePhase.Starting, _f.Clock.Now));

        var r = await Run(_f.Controller(), "toggle");

        Assert.Equal(0, r.ExitCode);
        Assert.Contains(_f.Sent, n => n is (Urgency.Low, "Sakshi", StillBusy));
        Assert.Equal(0, _f.Launcher.Calls);
        Assert.Empty(_f.Obs.Calls);
    }

    [Fact]
    public async Task Toggle_WhileTheNamePromptIsOpen_AsksForTheName_AndDoesNotStartAfterwards()
    {
        var video = _f.MkVideo(Path.Combine(_f.Rec, "2026-10-14 10-00-00.mp4"));
        _f.Set(new Capture(CapturePhase.Naming, Video: video, Stopped: _f.Clock.Now));
        var open = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var shown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _f.Prompter.Gate = open.Task;
        _f.Prompter.OnShown = _ => shown.TrySetResult();
        _f.Prompter.Answer("Budget");
        var c = _f.Controller();
        var stop = Run(c, "stop");
        await shown.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);

        var r = await Run(c, "toggle").WaitAsync(TimeSpan.FromSeconds(5), Ct);

        Assert.Equal(0, r.ExitCode);
        Assert.Contains(_f.Sent, n => n is (Urgency.Low, "Sakshi", TypeTheName));
        open.SetResult();
        Assert.Equal(0, (await stop).ExitCode);
        Assert.Equal(0, _f.Launcher.Calls); // the second click did not turn into a start
    }

    [Fact]
    public async Task Start_WhileAStopIsInProgress_Dies_AsBash()
    {
        _f.ObsRunning(true);
        _f.Set(new Capture(CapturePhase.Recording, _f.Clock.Now));
        _f.ConnectBlock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var c = _f.Controller();
        var stop = Run(c, "stop");
        await _f.ConnectEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);

        var r = await Run(c, "start").WaitAsync(TimeSpan.FromSeconds(5), Ct);

        Assert.Equal(1, r.ExitCode);
        Assert.Equal("sakshi: a start or stop is already in progress\n", r.StdErr);
        Assert.Contains(_f.Sent, n => n is (Urgency.Critical, "Sakshi", "a start or stop is already in progress"));
        Assert.Equal(0, _f.Launcher.Calls);
        _f.Obs.Recording = false;
        _f.ConnectBlock.SetResult();
        await stop;
    }

    [Fact]
    public async Task Stop_PassesThePresetName()
    {
        var video = _f.MkVideo(Path.Combine(_f.Rec, "2026-10-14 10-00-00.mp4"));
        _f.Set(new Capture(CapturePhase.Naming, Video: video, Stopped: _f.Clock.Now));

        var r = await Run(_f.Controller(), "stop", "Weekly sync");

        Assert.Equal(0, r.ExitCode);
        Assert.Empty(_f.Prompter.Shown);
        Assert.Single(_f.Store.Load().Jobs, j => j.Name == "Weekly_sync");
    }

    [Fact]
    public async Task Import_StoresTheVideo_UnderThePresetName()
    {
        var video = _f.MkVideo(Path.Combine(_f.Rec, "talk.mp4"));

        var r = await Run(_f.Controller(), "import", video, "Talk");

        Assert.Equal(0, r.ExitCode);
        Assert.Single(_f.Store.Load().Jobs, j => j.Name == "Talk");
        Assert.False(File.Exists(video));
    }

    [Fact]
    public async Task Import_MissingFile_ReportsOnStderr()
    {
        var missing = Path.Combine(_f.Rec, "nope.mp4");

        var r = await Run(_f.Controller(), "import", missing);

        Assert.Equal(1, r.ExitCode);
        Assert.Equal($"sakshi: No such file: {missing}\n", r.StdErr);
        Assert.Contains(_f.Sent, n => n is (Urgency.Critical, "Sakshi", _) && n.Body == $"No such file: {missing}");
    }

    [Fact]
    public async Task Import_WithoutAVideo_PrintsUsage()
    {
        var r = await Run(_f.Controller(), "import");

        Assert.Equal(1, r.ExitCode);
        Assert.Equal("sakshi: usage: sakshi import <video> [name]\n", r.StdErr);
    }

    [Fact]
    public async Task UnknownCommand_Exits2()
    {
        var r = await Run(_f.Controller(), "dance");

        Assert.Equal(2, r.ExitCode);
        Assert.Equal("sakshi: unknown command 'dance'\n", r.StdErr);
    }

    [Fact]
    public async Task Stop_ThatThrows_IsCaught_AndTheStateStaysResumable()
    {
        var video = _f.MkVideo(Path.Combine(_f.Rec, "2026-10-14 10-00-00.mp4"));
        _f.Set(new Capture(CapturePhase.Naming, Video: video, Stopped: _f.Clock.Now));
        _f.Prompter.Answer("Budget");
        _f.RecorderDelayFailure = new FormatException("menu exploded");

        var r = await Run(_f.Controller(), "stop");

        Assert.Equal(1, r.ExitCode);
        Assert.Equal(CapturePhase.Naming, _f.Capture.Phase);
        Assert.True(File.Exists(video));
        Assert.Contains(_f.Log.Lines, l => l.Contains("menu exploded", StringComparison.Ordinal));
        Assert.Contains(_f.Sent, n => n.Urgency == Urgency.Critical && n.Body.Contains("menu exploded", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Commands_RunOffTheCallersSynchronizationContext()
    {
        var video = _f.MkVideo(Path.Combine(_f.Rec, "2026-10-14 10-00-00.mp4"));
        _f.Set(new Capture(CapturePhase.Naming, Video: video, Stopped: _f.Clock.Now));
        var ui = new MarkerContext();
        SynchronizationContext? seen = ui;
        _f.Prompter.OnShown = _ => seen = SynchronizationContext.Current;
        _f.Prompter.Answer("Budget");
        var c = _f.Controller();

        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(ui);
        Task<CommandResult> run;
        try { run = Run(c, "stop"); }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
        await run;

        Assert.NotSame(ui, seen);
        Assert.Equal(0, ui.Posts);
    }

    private sealed class MarkerContext : SynchronizationContext
    {
        public int Posts;
        public override void Post(SendOrPostCallback d, object? state)
        {
            Interlocked.Increment(ref Posts);
            ThreadPool.QueueUserWorkItem(_ => d(state));
        }
    }

    [Fact]
    public async Task Dispose_WaitsForACommandInFlight_SoTheStartCleansUpFirst()
    {
        _f.UseObsServer();
        _f.Picker.Block = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); // the picker stays open
        var c = _f.Controller();
        var start = Run(c, "start");
        await _f.Picker.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.Equal(CapturePhase.Starting, _f.Capture.Phase);

        await c.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.True(start.IsCompleted);
        Assert.Equal(CapturePhase.Idle, _f.Capture.Phase);
        Assert.Equal(130, (await start).ExitCode);
    }

    [Fact]
    public async Task RunAsync_NeverFaults_OnAnUnexpectedError()
    {
        // A directory where the state file should be: reading the state throws.
        Directory.CreateDirectory(_f.StateFile);

        var r = await Run(_f.Controller(), "toggle");

        Assert.Equal(1, r.ExitCode);
        Assert.StartsWith("sakshi: Something went wrong: ", r.StdErr, StringComparison.Ordinal);
        Assert.EndsWith(" (see sakshi.log)\n", r.StdErr, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- housekeep / migrate

    private void OldDay(out string day)
    {
        day = Sakshi.Core.Naming.DayFolder.For(_f.Clock.Now.AddDays(-30));
        Directory.CreateDirectory(Path.Combine(_f.Meetings, day, "Old"));
    }

    [Fact]
    public async Task Housekeep_DryRun_PrintsTheMoves_AndMovesNothing()
    {
        OldDay(out var day);

        var r = await Run(_f.Controller(), "housekeep", "-n");

        Assert.Equal(0, r.ExitCode);
        Assert.Equal($"{day} -> archives/{day}\n", r.StdOut);
        Assert.True(Directory.Exists(Path.Combine(_f.Meetings, day, "Old")));
    }

    [Fact]
    public async Task Housekeep_Real_MovesTheDay_AndReportsOnStderr()
    {
        OldDay(out var day);

        var r = await Run(_f.Controller(), "housekeep");

        Assert.Equal(0, r.ExitCode);
        Assert.Equal("", r.StdOut);
        Assert.Equal($"housekeep: {day} -> archives/{day}\n", r.StdErr);
        Assert.True(Directory.Exists(Path.Combine(_f.Archive, day, "Old")));
        Assert.False(Directory.Exists(Path.Combine(_f.Meetings, day)));
    }

    [Fact]
    public async Task Housekeep_SkipsTheFolderAJobIsWritingInto()
    {
        OldDay(out var day);
        var busy = Path.Combine(_f.Meetings, day, "Old");
        _f.Store.Update(s => s with { Jobs = s.Jobs.Add(new TranscriptionJob(Guid.NewGuid(), busy, Path.Combine(busy, "Old.mp4"), "Old", "", null)) });

        var r = await Run(_f.Controller(), "housekeep", "-n");

        Assert.Equal($"{day}: skipped, still transcribing Old\n", r.StdOut);
    }

    [Fact]
    public async Task Housekeep_WithoutMeetingsDir_Dies()
    {
        Directory.Delete(_f.Meetings, recursive: true);

        var r = await Run(_f.Controller(), "housekeep");

        Assert.Equal(1, r.ExitCode);
        Assert.Equal($"sakshi: No meetings directory: {_f.Meetings}\n", r.StdErr);
        Assert.Contains(_f.Sent, n => n is (Urgency.Critical, "Sakshi", _) && n.Body == $"No meetings directory: {_f.Meetings}");
    }

    [Fact]
    public async Task Housekeep_BadOption_PrintsUsage()
    {
        var r = await Run(_f.Controller(), "housekeep", "--all");

        Assert.Equal(2, r.ExitCode);
        Assert.Equal("usage: sakshi housekeep [-n]\n", r.StdErr);
    }

    [Fact]
    public async Task Migrate_DryRun_PrintsTheMoves_AndMovesNothing()
    {
        var flat = Path.Combine(_f.Meetings, "Standup");
        _f.MkVideo(Path.Combine(flat, "Standup.mp4"), "2026-01-05 10:00");

        var r = await Run(_f.Controller(), "migrate", "-n");

        Assert.Equal(0, r.ExitCode);
        Assert.Equal("Standup -> 2026-01-05-Mon/Standup\n", r.StdOut);
        Assert.True(Directory.Exists(flat));
    }

    [Fact]
    public async Task Migrate_Real_MovesTheFolder_AndPrintsTheMoves()
    {
        var flat = Path.Combine(_f.Meetings, "Standup");
        _f.MkVideo(Path.Combine(flat, "Standup.mp4"), "2026-01-05 10:00");

        var r = await Run(_f.Controller(), "migrate");

        Assert.Equal(0, r.ExitCode);
        Assert.Equal("Standup -> 2026-01-05-Mon/Standup\n", r.StdOut);
        Assert.Equal("migrate: moved 1 folder(s) into day directories, renamed 0 day folder(s)\n", r.StdErr);
        Assert.True(File.Exists(Path.Combine(_f.Meetings, "2026-01-05-Mon", "Standup", "Standup.mp4")));
    }

    [Fact]
    public async Task Migrate_WithoutMeetingsDir_Dies()
    {
        Directory.Delete(_f.Meetings, recursive: true);

        var r = await Run(_f.Controller(), "migrate", "-n");

        Assert.Equal(1, r.ExitCode);
        Assert.Equal($"sakshi: No meetings directory: {_f.Meetings}\n", r.StdErr);
    }

    // ---------------------------------------------------------------- status

    [Fact]
    public void Status_UnreadableState_DoesNotThrow_AndLogsOnce()
    {
        Directory.CreateDirectory(_f.StateFile);
        var c = _f.Controller();

        var first = c.CurrentStatus();
        _f.Clock.Now = _f.Clock.Now.AddSeconds(1);
        c.CurrentStatus();
        _f.Clock.Now = _f.Clock.Now.AddSeconds(10);
        c.CurrentStatus();

        Assert.Equal("idle", first.State);
        Assert.Equal("State unreadable — see sakshi.log", first.Tooltip);
        Assert.Single(_f.Log.Lines, l => l.StartsWith("status: cannot read the state", StringComparison.Ordinal));
    }

    [Fact]
    public void Status_UnreadableState_KeepsTheLastGoodStatus_AndRecovers()
    {
        _f.ObsRunning(true);
        _f.Set(new Capture(CapturePhase.Recording, _f.Clock.Now));
        var c = _f.Controller();
        Assert.Equal("recording", c.CurrentStatus().State);

        File.Delete(_f.StateFile);
        Directory.CreateDirectory(_f.StateFile);
        Assert.Equal("recording", c.CurrentStatus().State);

        Directory.Delete(_f.StateFile);
        _f.Clock.Now = _f.Clock.Now.AddSeconds(10); // past the pause after a failure
        Assert.Equal("idle", c.CurrentStatus().State);
    }

    [Fact]
    public async Task Status_AfterDispose_LeavesTheStateAlone()
    {
        _f.Set(new Capture(CapturePhase.Recording, _f.Clock.Now));
        var c = _f.Controller();
        await c.DisposeAsync();

        c.CurrentStatus();

        Assert.Equal(CapturePhase.Recording, _f.Capture.Phase);
    }

    [Fact]
    public void Status_RecordingWithoutObs_GoesIdle()
    {
        _f.Set(new Capture(CapturePhase.Recording, _f.Clock.Now.AddMinutes(-5)));

        var s = _f.Controller().CurrentStatus();

        Assert.Equal("idle", s.State);
        Assert.Equal(CapturePhase.Idle, _f.Capture.Phase);
    }

    [Fact]
    public void Status_RecordingWithObs_StaysRecording()
    {
        _f.ObsRunning(true);
        _f.Set(new Capture(CapturePhase.Recording, _f.Clock.Now.AddMinutes(-5)));

        var s = _f.Controller().CurrentStatus();

        Assert.Equal("recording", s.State);
        Assert.Equal("Recording meeting (5:00) — click to stop", s.Tooltip);
        Assert.Equal(CapturePhase.Recording, _f.Capture.Phase);
    }

    [Fact]
    public void Status_AsksTheServicesObsCheck_NotTheProcessList()
    {
        _f.ObsCheck = () => true;   // stage 3: OBS found some other way than a process named "obs"
        _f.Set(new Capture(CapturePhase.Recording, _f.Clock.Now.AddMinutes(-5)));

        Assert.Equal("recording", _f.Controller().CurrentStatus().State);
        Assert.Equal(CapturePhase.Recording, _f.Capture.Phase);
    }

    [Fact]
    public void Services_ObsRunning_DefaultsToAProcessNamedObs()
    {
        var o = _f.Controller().Services;
        var runner = new Sakshi.TestSupport.FakeProcessRunner();
        var s = new TrayServices(o.Paths, o.Os, o.Home, o.Config, o.Store, o.Recorder, o.Starter, o.Queue,
            o.Prompter, o.Notifier, o.Log, o.Clock, runner);

        Assert.False(s.ObsRunning());
        runner.Running.Add("obs");
        Assert.True(s.ObsRunning());
    }

    [Fact]
    public void Status_ChecksForARunningObs_AtMostOnceEvery5Seconds()
    {
        var checks = 0;
        _f.ObsCheck = () => { checks++; return true; };
        _f.Set(new Capture(CapturePhase.Recording, _f.Clock.Now.AddMinutes(-5)));
        var c = _f.Controller();

        for (var i = 0; i < 4; i++)
        {
            Assert.Equal("recording", c.CurrentStatus().State);
            _f.Clock.Now = _f.Clock.Now.AddSeconds(1);   // the UI timer: once a second
        }
        Assert.Equal(1, checks);

        _f.Clock.Now = _f.Clock.Now.AddSeconds(1);       // 5 s after the first check
        c.CurrentStatus();
        Assert.Equal(2, checks);
    }

    [Fact]
    public void Status_ObsNotRunning_IsNotRemembered()
    {
        var checks = 0;
        var running = false;
        _f.ObsCheck = () => { checks++; return running; };
        _f.Set(new Capture(CapturePhase.Recording, _f.Clock.Now));
        var c = _f.Controller();
        Assert.Equal("idle", c.CurrentStatus().State);

        running = true;                                   // a new recording a moment later
        _f.Set(new Capture(CapturePhase.Recording, _f.Clock.Now));
        Assert.Equal("recording", c.CurrentStatus().State);
        Assert.Equal(2, checks);
    }

    [Fact]
    public void Status_StartingWithoutObs_AndNoStartInProgress_GoesIdle()
    {
        _f.Set(new Capture(CapturePhase.Starting, _f.Clock.Now));

        Assert.Equal("idle", _f.Controller().CurrentStatus().State);
        Assert.Equal(CapturePhase.Idle, _f.Capture.Phase);
    }

    [Fact]
    public async Task Status_StartingWithoutObs_WhileAStartIsInProgress_StaysStarting()
    {
        var c = _f.Controller();
        _f.Launcher.Block = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var start = Run(c, "start");
        await _f.Launcher.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        _f.Set(new Capture(CapturePhase.Starting, _f.Clock.Now)); // OBS is still launching

        var s = c.CurrentStatus();

        Assert.Equal("starting", s.State);
        Assert.Equal(CapturePhase.Starting, _f.Capture.Phase);
        _f.Launcher.Block.SetResult();
        await start;
    }

    [Fact]
    public void Status_NamingWithTheVideoGone_GoesIdle()
    {
        _f.Set(new Capture(CapturePhase.Naming, Video: Path.Combine(_f.Rec, "gone.mp4"), Stopped: _f.Clock.Now));

        Assert.Equal("idle", _f.Controller().CurrentStatus().State);
        Assert.Equal(CapturePhase.Idle, _f.Capture.Phase);
    }

    [Fact]
    public void Status_NamingWithTheVideo_WaitsToBeNamed()
    {
        var video = _f.MkVideo(Path.Combine(_f.Rec, "a.mp4"));
        _f.Set(new Capture(CapturePhase.Naming, Video: video, Stopped: _f.Clock.Now));

        var s = _f.Controller().CurrentStatus();

        Assert.Equal("naming", s.State);
        Assert.Equal("Unnamed recording waiting — click to name and store it", s.Tooltip);
    }

    [Fact]
    public async Task Status_LockHeld_WhileAPromptIsOpen()
    {
        var video = _f.MkVideo(Path.Combine(_f.Rec, "2026-10-14 10-00-00.mp4"));
        _f.Set(new Capture(CapturePhase.Naming, Video: video, Stopped: _f.Clock.Now));
        var open = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var shown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _f.Prompter.Gate = open.Task;
        _f.Prompter.OnShown = _ => shown.TrySetResult();
        var c = _f.Controller();
        var stop = Run(c, "stop");
        await shown.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);

        Assert.Equal("Waiting for the meeting name…", c.CurrentStatus().Tooltip);
        open.SetResult();
        await stop;
    }
}
