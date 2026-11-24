using Sakshi.Core.Config;
using Sakshi.Core.Naming;
using Sakshi.Core.Ports;
using Sakshi.Core.State;

namespace Sakshi.Core.Tests;

public sealed class StopTests : IDisposable
{
    private readonly RecorderFixture _f = new();
    private const string Named = "named";

    public void Dispose() => _f.Dispose();

    private Task<int> Stop(string? preset = null) => _f.Recorder().StopAsync(preset, CancellationToken.None);

    // ---- port of test/keep-or-discard.sh

    [Fact]
    public async Task SavePath()
    {
        _f.Stranded();
        _f.Prompter.Answer("Save it", Named);

        Assert.Equal(0, await Stop());

        Assert.Equal("Recording stopped|Meeting name|", _f.Prompter.Sequence);
        Assert.Equal(1, _f.Stored(Named));
        Assert.Empty(_f.Trash.Paths);
        Assert.Equal(CapturePhase.Idle, _f.Capture.Phase);
    }

    [Fact]
    public async Task KeepPrompt_RowsAreVerbatimFromBash()
    {
        _f.Stranded();
        _f.Prompter.Answer("Don't save", "Keep it", Named);

        await Stop();

        Assert.Equal(
            [new PromptRow("\U000F0193", "Save it", "Name it and transcribe"), new PromptRow("\U000F01B4", "Don't save", "Throw this recording away")],
            _f.Prompter.Rows[0]);
        Assert.Equal(
            [new PromptRow("\U000F0193", "Keep it", "Go back to naming it"), new PromptRow("\U000F01B4", "Yes, delete it", "Moves the recording to " + Sakshi.Core.Recording.Recorder.TrashName)],
            _f.Prompter.Rows[1]);
    }

    [Fact]
    public async Task DiscardThenConfirmed_TrashesTheVideoAndNeverNames()
    {
        var video = _f.Stranded();
        _f.Prompter.Answer("Don't save", "Yes, delete it");

        Assert.Equal(0, await Stop());

        Assert.Equal("Recording stopped|Delete this recording?|", _f.Prompter.Sequence);
        Assert.Equal([video], _f.Trash.Paths);
        Assert.Empty(_f.Files(_f.Rec));
        Assert.Equal(CapturePhase.Idle, _f.Capture.Phase);
        Assert.Empty(_f.Files(_f.Meetings));
        Assert.Contains((Urgency.Low, "Recording discarded", "Moved to " + Sakshi.Core.Recording.Recorder.TrashName + ": " + Path.GetFileName(video)), _f.Notifier.Sent);
        Assert.Empty(_f.Jobs);
    }

    [Fact]
    public async Task DiscardThenBackedOut_FallsThroughToNaming()
    {
        _f.Stranded();
        _f.Prompter.Answer("Don't save", "Keep it", Named);

        Assert.Equal(0, await Stop());

        Assert.Equal("Recording stopped|Delete this recording?|Meeting name|", _f.Prompter.Sequence);
        Assert.Equal(1, _f.Stored(Named));
        Assert.Empty(_f.Trash.Paths);
    }

    [Fact]
    public async Task DiscardConfirmationDismissed_KeepsTheRecording()
    {
        _f.Stranded();
        _f.Prompter.Answer("Don't save", null, Named);

        await Stop();

        Assert.Equal(1, _f.Stored(Named));
        Assert.Empty(_f.Trash.Paths);
    }

    [Fact]
    public async Task KeepPromptDismissed_Saves()
    {
        _f.Stranded();
        _f.Prompter.Answer(null, Named);

        await Stop();

        Assert.Equal(1, _f.Stored(Named));
    }

    [Fact]
    public async Task KeepPromptSwitchedOff_NeverAsks()
    {
        _f.Config = _f.Config with { KeepPrompt = false };
        _f.Stranded();
        _f.Prompter.Answer(Named);

        await Stop();

        Assert.Equal("Meeting name|", _f.Prompter.Sequence);
    }

    [Fact]
    public async Task PresetName_SkipsEveryQuestion()
    {
        _f.Stranded();

        Assert.Equal(0, await Stop("preset"));

        Assert.Equal("", _f.Prompter.Sequence);
        Assert.Equal(1, _f.Stored("preset"));
    }

    [Fact]
    public async Task TrashFailure_KeepsTheRecordingResumable()
    {
        var video = _f.Stranded();
        _f.Prompter.Answer("Don't save", "Yes, delete it");
        _f.Trash.Failure = new IOException("no trash here");

        Assert.Equal(1, await Stop());

        Assert.True(File.Exists(video));
        Assert.Equal(CapturePhase.Naming, _f.Capture.Phase);
        Assert.Equal(video, _f.Capture.Video);
        Assert.Contains((Urgency.Critical, "Sakshi",
            $"Could not move the recording to {Sakshi.Core.Recording.Recorder.TrashName}. It is still at {video} — click stop again to retry."), _f.Notifier.Sent);
    }

    // ---- stop: resume path, day and default name

    [Fact]
    public async Task Resume_DayFromTheStopTime_DefaultNameIsHourMinute()
    {
        var stopped = RecorderFixture.Local(2026, 2, 3, 13, 6);
        _f.Stranded(stopped);
        _f.Prompter.Answer("Save it", null, null); // name dismissed twice

        Assert.Equal(0, await Stop());

        Assert.Equal("Recording stopped|Meeting name|Meeting name (Esc again keeps 13-06)|", _f.Prompter.Sequence);
        Assert.True(File.Exists(Path.Combine(_f.Meetings, DayFolder.For(stopped), "13-06", "13-06.mp4")));
        Assert.Equal("2026-02-03-Tue", DayFolder.For(stopped));
    }

    [Fact]
    public async Task Resume_DoesNotAskObs()
    {
        _f.Stranded();

        await Stop("x");

        Assert.Empty(_f.Obs.Calls);
    }

    [Fact]
    public async Task Stop_SanitizedNameEmpty_UsesDefault()
    {
        var stopped = RecorderFixture.Local(2026, 2, 3, 9, 30);
        _f.Stranded(stopped);
        _f.Prompter.Answer("Save it", "///", "!!!"); // both sanitize to nothing

        await Stop();

        Assert.True(File.Exists(Path.Combine(_f.Meetings, DayFolder.For(stopped), "09-30", "09-30.mp4")));
    }

    [Fact]
    public async Task Stop_NamePromptTyped_IsSanitized()
    {
        _f.Stranded();
        _f.Prompter.Answer("Save it", "Weekly Sync!");

        await Stop();

        Assert.Equal(1, _f.Stored("Weekly_Sync"));
    }

    [Fact]
    public async Task Stop_TypedNameClashingWithAFolder_GetsASuffix()
    {
        _f.Stranded();
        Directory.CreateDirectory(Path.Combine(_f.Meetings, DayFolder.For(_f.Clock.Now), "dup"));
        _f.Prompter.Answer("Save it", "dup");

        await Stop();

        Assert.Equal(1, _f.Stored("dup-2"));
        Assert.Equal("dup-2", Assert.Single(_f.Jobs).Name);
    }

    [Fact]
    public async Task Stop_QueuesTheJobAndGoesIdle()
    {
        var stopped = RecorderFixture.Local(2026, 2, 3, 13, 6);
        _f.Stranded(stopped);
        _f.Config = _f.Config with { TranscribeArgs = "-F changes" };

        Assert.Equal(0, await Stop("job"));

        var job = Assert.Single(_f.Jobs);
        var dir = Path.Combine(_f.Meetings, DayFolder.For(stopped), "job");
        Assert.Equal(dir, job.Dir);
        Assert.Equal(Path.Combine(dir, "job.mp4"), job.Video);
        Assert.Equal("job", job.Name);
        Assert.Equal("-F changes", job.FrameArgs);
        Assert.Null(job.HandOff);
        Assert.Equal(CapturePhase.Idle, _f.Capture.Phase);
        Assert.Contains((Urgency.Low, "Saved job", "Transcribing in the background…"), _f.Notifier.Sent);
    }

    // ---- stop: asking OBS

    [Fact]
    public async Task Stop_ObsReportsNoPath_FindsNewestInRecordDir()
    {
        var now = _f.Clock.Now.LocalDateTime;
        string At(string name, TimeSpan ago)
        {
            var p = Path.Combine(_f.Rec, name);
            _f.MkVideo(p);
            File.SetLastWriteTime(p, now - ago);
            return p;
        }
        var stale = At("stale.mp4", TimeSpan.FromMinutes(30));
        var older = At("older.mkv", TimeSpan.FromMinutes(5));
        var newest = At("newest.mov", TimeSpan.FromMinutes(2));
        var notVideo = At("newer.txt", TimeSpan.FromMinutes(1));
        _f.Obs.OutputPath = null;
        _f.Obs.RecordDirectory = _f.Rec;

        Assert.Equal(0, await Stop("found"));

        Assert.Equal(["IsRecording", "StopRecord", "GetRecordDirectory"], _f.Obs.Calls);
        Assert.False(File.Exists(newest));
        Assert.All(new[] { stale, older, notVideo }, p => Assert.True(File.Exists(p)));
        Assert.Equal(1, _f.Stored("found"));
        Assert.Contains(TimeSpan.FromSeconds(1), _f.Delays);
    }

    [Fact]
    public async Task Once_OBS_has_stopped_its_settings_are_handed_back_even_if_that_fails()
    {
        var video = _f.MkVideo(Path.Combine(_f.Rec, "obs.mp4"));
        _f.Obs.OutputPath = video;
        var handedBack = 0;
        _f.AfterObsStopped = _ => { handedBack++; throw new InvalidOperationException("OBS went away"); };

        Assert.Equal(0, await Stop("direct"));

        Assert.Equal(1, handedBack);
        Assert.Equal(1, _f.Stored("direct"));   // the recording is stored all the same
    }

    [Fact]
    public async Task A_recording_stopped_in_OBS_itself_still_hands_OBS_back()
    {
        _f.Obs.Recording = false;
        var handedBack = 0;
        _f.AfterObsStopped = _ => { handedBack++; return Task.CompletedTask; };

        await Stop("direct");

        Assert.Equal(1, handedBack);
    }

    [Fact]
    public async Task A_sound_only_recording_keeps_only_its_sound_and_asks_nothing_about_pictures()
    {
        var video = _f.MkVideo(Path.Combine(_f.Rec, "obs.mp4"));
        _f.Obs.OutputPath = video;
        _f.Set(new Capture(CapturePhase.Recording, _f.Clock.Now, AudioOnly: true));
        _f.KeepSoundOnly = (v, _) =>
        {
            var m4a = Path.ChangeExtension(v, ".m4a");
            File.Move(v, m4a);
            return Task.FromResult(m4a);
        };

        Assert.Equal(0, await Stop("talk"));

        Assert.DoesNotContain("Frames to keep", _f.Prompter.Shown);
        Assert.True(File.Exists(Path.Combine(_f.Meetings, DayFolder.For(_f.Clock.Now.ToLocalTime()), "talk", "talk.m4a")));
        Assert.Equal("-F none", _f.Jobs.Single().FrameArgs);
    }

    [Fact]
    public async Task If_the_sound_cannot_be_kept_alone_the_whole_recording_is_stored()
    {
        var video = _f.MkVideo(Path.Combine(_f.Rec, "obs.mp4"));
        _f.Obs.OutputPath = video;
        _f.Set(new Capture(CapturePhase.Recording, _f.Clock.Now, AudioOnly: true));
        _f.KeepSoundOnly = (_, _) => throw new InvalidOperationException("ffmpeg broke");

        Assert.Equal(0, await Stop("talk"));

        Assert.Equal(1, _f.Stored("talk"));
    }

    [Fact]
    public async Task Stop_ObsReportsAPath_UsesIt()
    {
        var video = _f.MkVideo(Path.Combine(_f.Rec, "obs.mp4"));
        _f.Obs.OutputPath = video;

        Assert.Equal(0, await Stop("direct"));

        Assert.Equal(["IsRecording", "StopRecord"], _f.Obs.Calls);
        Assert.Equal(1, _f.Stored("direct"));
    }

    [Fact]
    public async Task Stop_ObsStopsButNoFileFound_ClearsStateAndFails()
    {
        _f.Obs.OutputPath = Path.Combine(_f.Rec, "ghost.mp4");
        _f.Obs.RecordDirectory = _f.Rec;
        _f.Set(new Capture(CapturePhase.Recording, Started: _f.Clock.Now));

        Assert.Equal(1, await Stop());

        Assert.Equal(CapturePhase.Idle, _f.Capture.Phase);
        Assert.Contains((Urgency.Critical, "Sakshi",
            $"Recording stopped but no file was found. Check OBS's recording folder ({_f.Rec})."), _f.Notifier.Sent);
    }

    [Fact]
    public async Task Stop_ObsNotRunning_ClearsStateAndFails()
    {
        _f.ObsReachable = false;
        _f.Set(new Capture(CapturePhase.Recording, Started: _f.Clock.Now));

        Assert.Equal(1, await Stop());

        Assert.Equal(CapturePhase.Idle, _f.Capture.Phase);
        Assert.Contains((Urgency.Critical, "Sakshi", "OBS is not running"), _f.Notifier.Sent);
    }

    [Fact]
    public async Task Stop_NotRecording_ClearsStateAndExitsZero()
    {
        _f.Obs.Recording = false;
        _f.Set(new Capture(CapturePhase.Recording, Started: _f.Clock.Now));

        Assert.Equal(0, await Stop());

        Assert.Equal(CapturePhase.Idle, _f.Capture.Phase);
        Assert.Contains((Urgency.Low, "Not recording", "OBS has no active recording."), _f.Notifier.Sent);
        Assert.DoesNotContain("StopRecord", _f.Obs.Calls);
    }

    [Fact]
    public async Task Stop_ObsRefusesToStop_Fails()
    {
        _f.Obs.StopFails = true;

        Assert.Equal(1, await Stop());

        Assert.Contains((Urgency.Critical, "Sakshi", "OBS refused to stop recording"), _f.Notifier.Sent);
    }

    [Fact]
    public async Task Stop_PointsStateAtTheVideoBeforeAnyPrompt()
    {
        var video = _f.MkVideo(Path.Combine(_f.Rec, "obs.mp4"));
        _f.Obs.OutputPath = video;
        _f.Set(new Capture(CapturePhase.Recording, Started: _f.Clock.Now));
        Capture? atFirstPrompt = null;
        _f.Prompter.OnShown = _ => atFirstPrompt ??= _f.Capture;

        await Stop();

        Assert.Equal(CapturePhase.Naming, atFirstPrompt!.Phase);
        Assert.Equal(video, atFirstPrompt.Video);
        Assert.Equal(_f.Clock.Now, atFirstPrompt.Stopped);
    }

    // ---- crash between Enqueue and Idle

    [Fact]
    public async Task Stop_NamingWithAVideoThatNoLongerExists_ClearsItAndAsksObs()
    {
        // The process died after queueing the job but before the state went idle: the
        // recording was moved, so the old path is gone. Stop must not wedge on it.
        _f.Set(new Capture(CapturePhase.Naming, Video: Path.Combine(_f.Rec, "moved-away.mp4"), Stopped: _f.Clock.Now, Name: "x"));
        _f.Obs.Recording = false;

        Assert.Equal(0, await Stop());

        Assert.Equal(CapturePhase.Idle, _f.Capture.Phase);
    }

    [Fact]
    public async Task Stop_FileDisappearsBeforeItIsStored_ClearsStateAndFails()
    {
        var video = _f.Stranded();
        _f.Prompter.Answer("Save it", "n");
        _f.Prompter.OnShown = title => { if (title == "Meeting name") File.Delete(video); };

        Assert.Equal(1, await Stop());

        Assert.Equal(CapturePhase.Idle, _f.Capture.Phase);
        Assert.Contains((Urgency.Critical, "Sakshi", $"The recording file disappeared before it could be stored: {video}"), _f.Notifier.Sent);
    }

    // ---- store failures keep everything for the retry

    [Fact]
    public async Task Stop_StoreFails_StateStaysNamingWithNameAndFrames_RetryAsksNothing()
    {
        _f.Config = _f.Config with { FramesPrompt = true, TranscribeArgs = "-F changes" };
        var stopped = RecorderFixture.Local(2026, 2, 3, 13, 6);
        var video = _f.Stranded(stopped);
        var blocker = Path.Combine(_f.Meetings, DayFolder.For(stopped)); // a file where the day folder must go
        File.WriteAllText(blocker, "in the way");
        _f.Prompter.Answer("Save it", "Retry Me", "None");

        Assert.Equal(1, await Stop());

        Assert.True(File.Exists(video));
        var c = _f.Capture;
        Assert.Equal(CapturePhase.Naming, c.Phase);
        Assert.Equal(video, c.Video);
        Assert.Equal("Retry_Me", c.Name);
        Assert.Equal(new FrameChoice("-F none", null), c.Frames);
        Assert.Empty(_f.Jobs);
        Assert.Contains(_f.Notifier.Sent, n => n.Urgency == Urgency.Critical && n.Body.StartsWith("Could not move the recording into ", StringComparison.Ordinal)
            && n.Body.EndsWith($"It is safe at {video} — click stop again to retry.", StringComparison.Ordinal));

        File.Delete(blocker);
        var asked = _f.Prompter.Shown.Count;

        Assert.Equal(0, await Stop());

        Assert.Equal(asked, _f.Prompter.Shown.Count);
        Assert.Equal(1, _f.Stored("Retry_Me"));
        Assert.Equal("-F none", Assert.Single(_f.Jobs).FrameArgs);
        Assert.Equal(CapturePhase.Idle, _f.Capture.Phase);
    }

    // ---- port of test/handoff.sh 158-177 (stop side)

    [Fact]
    public async Task Retry_ReusesTheHandOffAlreadyInTheState()
    {
        _f.Config = _f.Config with { FramesPrompt = true };
        _f.Stranded(RecorderFixture.Local(2026, 1, 5, 10, 0), "resumed", new FrameChoice("-F none", "handoff-tool again"));
        _f.Prompter.Answer("Changes");

        Assert.Equal(0, await Stop());

        Assert.Equal("", _f.Prompter.Sequence);
        var job = Assert.Single(_f.Jobs);
        Assert.Equal("handoff-tool again", job.HandOff);
        Assert.Equal("-F none", job.FrameArgs);
    }

    [Fact]
    public async Task OldStateWithoutHandOff_StillStoresAndQueues()
    {
        _f.Stranded(RecorderFixture.Local(2026, 1, 5, 10, 0), "oldstate", new FrameChoice("-F none", null));

        Assert.Equal(0, await Stop());

        var job = Assert.Single(_f.Jobs);
        Assert.Null(job.HandOff);
        Assert.Equal("oldstate", job.Name);
        Assert.True(File.Exists(Path.Combine(_f.Meetings, "2026-01-05-Mon", "oldstate", "oldstate.mp4")));
    }

    // ---- review fixes

    [Fact]
    public async Task Stop_CancelledRightAfterObsStops_StateStillPointsAtTheVideo()
    {
        using var cts = new CancellationTokenSource();
        var video = _f.MkVideo(Path.Combine(_f.Rec, "late.mp4"));
        File.SetLastWriteTime(video, _f.Clock.Now.LocalDateTime);
        _f.Obs.OutputPath = null;
        _f.Obs.RecordDirectory = _f.Rec;
        _f.Obs.AfterStop = cts.Cancel;
        _f.Set(new Capture(CapturePhase.Recording, Started: _f.Clock.Now));
        _f.Prompter.OnShown = _ => throw new OperationCanceledException();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _f.Recorder().StopAsync(null, cts.Token));

        Assert.Equal(CapturePhase.Naming, _f.Capture.Phase);
        Assert.Equal(video, _f.Capture.Video);
        Assert.True(File.Exists(video));
    }

    [Fact]
    public async Task Stop_PrompterThrowsOnEveryCall_StoresUnderTheDefaultNameWithTranscribeArgs()
    {
        _f.Config = _f.Config with { FramesPrompt = true, TranscribeArgs = "-F changes" };
        var stopped = RecorderFixture.Local(2026, 2, 3, 9, 30);
        _f.Stranded(stopped);
        _f.Prompter.Failure = new InvalidOperationException("menu crashed");

        Assert.Equal(0, await Stop());

        Assert.True(File.Exists(Path.Combine(_f.Meetings, DayFolder.For(stopped), "09-30", "09-30.mp4")));
        var job = Assert.Single(_f.Jobs);
        Assert.Equal("-F changes", job.FrameArgs);
        Assert.Null(job.HandOff);
    }

    [Fact]
    public async Task Stop_AfterACrashedStop_NewRecordingDoesNotInheritNameOrFrames()
    {
        _f.Config = _f.Config with { FramesPrompt = true };
        _f.Set(new Capture(CapturePhase.Naming, Video: Path.Combine(_f.Rec, "gone.mp4"), Stopped: _f.Clock.Now,
            Name: "stale", Frames: new FrameChoice("-F all", "old-tool")));
        _f.Obs.OutputPath = _f.MkVideo(Path.Combine(_f.Rec, "fresh.mp4"));
        _f.Prompter.Answer("Save it", "fresh", "None");

        Assert.Equal(0, await Stop());

        Assert.Equal("Recording stopped|Meeting name|Frames to keep|", _f.Prompter.Sequence);
        var job = Assert.Single(_f.Jobs);
        Assert.Equal("fresh", job.Name);
        Assert.Equal("-F none", job.FrameArgs);
        Assert.Null(job.HandOff);
    }

    [Fact]
    public async Task Stop_ObsStatusThrows_FailsAndKeepsTheState()
    {
        _f.Obs.StatusFails = true;
        _f.Set(new Capture(CapturePhase.Recording, Started: _f.Clock.Now));

        Assert.Equal(1, await Stop());

        Assert.Equal(CapturePhase.Recording, _f.Capture.Phase);
        Assert.Contains(_f.Notifier.Sent, n => n.Urgency == Urgency.Critical && n.Body.StartsWith("Could not ask OBS whether it is recording", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Stop_ConnectThrows_BodyCarriesTheReason()
    {
        _f.ConnectFailure = new IOException("connection refused");

        Assert.Equal(1, await Stop());

        Assert.Contains((Urgency.Critical, "Sakshi", "OBS WebSocket port is closed (connection refused)"), _f.Notifier.Sent);
    }

    [Fact]
    public async Task Stop_StoreFails_MessageNamesTheAttemptedDirectory()
    {
        var stopped = RecorderFixture.Local(2026, 2, 3, 13, 6);
        var video = _f.Stranded(stopped, "x");
        File.WriteAllText(Path.Combine(_f.Meetings, DayFolder.For(stopped)), "in the way");

        await Stop();

        var dir = Path.Combine(_f.Meetings, DayFolder.For(stopped), "x");
        Assert.Contains((Urgency.Critical, "Sakshi",
            $"Could not move the recording into {dir}. It is safe at {video} — click stop again to retry."), _f.Notifier.Sent);
    }

    [Fact]
    public async Task PromptTimeouts_ComeFromTheConfig()
    {
        _f.Config = _f.Config with { FramesPrompt = true, NamePromptTimeout = 11, KeepPromptTimeout = 12, FramesPromptTimeout = 13 };
        _f.Stranded();
        _f.Prompter.Answer("Save it", "n", "None");

        await Stop();

        Assert.Equal("Recording stopped|Meeting name|Frames to keep|", _f.Prompter.Sequence);
        Assert.Equal([12, 11, 13], _f.Prompter.Timeouts.Select(t => (int)t.TotalSeconds));
    }

    [Fact]
    public async Task SecondStop_WhileNaming_TellsTheUserToTypeTheName_ImportIsRefused()
    {
        var src = _f.MkVideo(Path.Combine(_f.Root, "src", "busy.mp4"));
        var video = _f.Stranded();
        var gate = new TaskCompletionSource();
        _f.Prompter.Gate = gate.Task;
        _f.Prompter.Answer("Save it", "first");
        var recorder = _f.Recorder();

        var first = recorder.StopAsync(null, CancellationToken.None);
        while (_f.Prompter.Shown.Count == 0) await Task.Delay(5, TestContext.Current.CancellationToken);

        Assert.Equal(0, await recorder.StopAsync(null, CancellationToken.None));
        Assert.Contains((Urgency.Low, "Sakshi", "Type the meeting name in the prompt and press Enter."), _f.Notifier.Sent);
        Assert.Equal(1, await recorder.ImportAsync(src, "x", CancellationToken.None));
        Assert.Contains((Urgency.Critical, "Sakshi", "a start or stop is already in progress"), _f.Notifier.Sent);
        Assert.True(File.Exists(src));

        gate.SetResult();
        Assert.Equal(0, await first);
        Assert.Equal(1, _f.Stored("first"));
        Assert.False(File.Exists(video));
    }

    [Fact]
    public async Task SecondStop_WhileNotNaming_IsRefused()
    {
        var gate = new TaskCompletionSource();
        _f.Prompter.Gate = gate.Task;
        _f.Obs.OutputPath = _f.MkVideo(Path.Combine(_f.Rec, "a.mp4"));
        var recorder = _f.Recorder();
        // Hold the lock inside the OBS phase: the fake's AfterStop blocks until released.
        var entered = new TaskCompletionSource();
        _f.Obs.AfterStop = () => { entered.SetResult(); gate.Task.Wait(); };
        var first = Task.Run(() => recorder.StopAsync("p", CancellationToken.None));
        await entered.Task;

        Assert.Equal(1, await recorder.StopAsync(null, CancellationToken.None));
        Assert.Contains((Urgency.Critical, "Sakshi", "a start or stop is already in progress"), _f.Notifier.Sent);

        gate.SetResult();
        Assert.Equal(0, await first);
    }
    // ---- final review fixes

    [Fact]
    public async Task Stop_Success_ReleasesTheObsConnection()
    {
        _f.Obs.OutputPath = _f.MkVideo(Path.Combine(_f.Rec, "obs.mp4"));

        Assert.Equal(0, await Stop("done"));

        Assert.Equal(1, _f.Obs.Disposed);
    }

    [Fact]
    public async Task Stop_NotRecording_ReleasesTheObsConnection()
    {
        _f.Obs.Recording = false;

        Assert.Equal(0, await Stop());

        Assert.Equal(1, _f.Obs.Disposed);
    }

    [Fact]
    public async Task Stop_ObsRefusesToStop_ReleasesTheObsConnection()
    {
        _f.Obs.StopFails = true;

        Assert.Equal(1, await Stop());

        Assert.Equal(1, _f.Obs.Disposed);
    }

    [Fact]
    public async Task Stop_ObsStatusThrows_ReleasesTheObsConnection()
    {
        _f.Obs.StatusFails = true;

        Assert.Equal(1, await Stop());

        Assert.Equal(1, _f.Obs.Disposed);
    }

    [Fact]
    public async Task Stop_ObsNoFileFound_ReleasesTheObsConnection()
    {
        _f.Obs.OutputPath = Path.Combine(_f.Rec, "ghost.mp4");

        Assert.Equal(1, await Stop());

        Assert.Equal(1, _f.Obs.Disposed);
    }

    [Fact]
    public async Task Stop_CancelledWhileStopRecordIsInFlight_StateStillPointsAtTheVideo()
    {
        // The cancel lands after OBS stopped but before its reply is read: StopRecord must not
        // be cancellable, or the state stays Recording and the video is forgotten.
        using var cts = new CancellationTokenSource();
        var video = _f.MkVideo(Path.Combine(_f.Rec, "inflight.mp4"));
        _f.Obs.OutputPath = video;
        _f.Obs.AfterStop = cts.Cancel;
        _f.Set(new Capture(CapturePhase.Recording, Started: _f.Clock.Now));
        _f.Prompter.OnShown = _ => throw new OperationCanceledException();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _f.Recorder().StopAsync(null, cts.Token));

        Assert.Equal(CapturePhase.Naming, _f.Capture.Phase);
        Assert.Equal(video, _f.Capture.Video);
        Assert.Equal(1, _f.Obs.Disposed);
    }

    [Fact]
    public async Task Stop_CancelledWhileWaitingForTheFileToSettle_DoesNotMoveIt()
    {
        // A Quit while OBS may still be finalising the file: leave it where it is, resumable.
        using var cts = new CancellationTokenSource();
        var video = _f.Stranded();
        _f.OnDelay = d => { if (d == TimeSpan.FromMilliseconds(500)) cts.Cancel(); };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _f.Recorder().StopAsync("quit", cts.Token));

        Assert.True(File.Exists(video));
        Assert.Equal(0, _f.Stored("quit"));
        Assert.Empty(_f.Jobs);
        Assert.Equal(CapturePhase.Naming, _f.Capture.Phase);
        Assert.Equal(video, _f.Capture.Video);
        Assert.Equal("quit", _f.Capture.Name);
    }
}
