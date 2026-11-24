using Sakshi.Core.Naming;
using Sakshi.Core.Obs;
using Sakshi.Core.Ports;
using Sakshi.Core.Recording;
using Sakshi.Core.Reminder;
using Sakshi.Core.State;
using Sakshi.Core.Store;

namespace Sakshi.App.Tray;

public sealed record CommandResult(int ExitCode, string StdOut, string StdErr);

/// <summary>
/// The tray's brain, without any UI: the Avalonia tray and the IPC server both call it. It owns the
/// one <see cref="StateStore"/>, the Recorder, the Starter, the transcription queue, the reminder and,
/// where the platform has a <see cref="ICaptureRecovery"/>, the freeze check.
///
/// One busy gate (a SemaphoreSlim) serialises start, stop, toggle, import, housekeep, migrate and the
/// reminder prompt, so at most one prompt is ever open. Waiters queue on it, except that a recording
/// command (start, stop, toggle, import) never queues behind another one: like bash's flock -n it is
/// answered at once ("Still busy — finish the window picker first." during a start, the meeting-name
/// hint while a recording waits for its name, else "a start or stop is already in progress"). A
/// recording command arriving while the reminder prompt is open closes that prompt and goes ahead.
/// The freeze check takes the gate only when it is free and no command is waiting for it; a command that
/// arrives during a check waits for it, up to a few seconds when the check reconnects the capture.
///
/// Every command runs on the thread pool, never on the caller's (UI) thread.
/// </summary>
public sealed class TrayController(TrayServices services) : IAsyncDisposable
{
    private const string StillBusy = "Still busy — finish the window picker first.";
    private const string TypeTheName = "Type the meeting name in the prompt and press Enter.";
    private const string AlreadyInProgress = "a start or stop is already in progress";
    private const string KeepRecording = "Keep recording";
    private const string StopRecording = "Stop recording";
    private static readonly TimeSpan ReminderPoll = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan QueueBackoff = TimeSpan.FromSeconds(5);

    private const string RepickUnsupported = "Re-picking the window while recording works only on Linux under Hyprland.";
    private const string NothingToRepick = "Not recording — nothing to re-pick.";
    internal const string SoundOnlyNoWindow = "This recording is sound only — there is no window to re-pick.";

    private enum Op { None, Toggle, Start, Stop, Import, Repick }

    private readonly TrayServices _s = services;

    internal TrayServices Services => _s;
    private readonly MeetingStore _meetings = new(services.Config.MeetingsDir, services.Config.EffectiveArchiveDir, services.Log);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _life = new();
    private readonly object _sync = new();

    // Under _sync.
    private Op _recordingOp = Op.None;
    private CancellationTokenSource? _reminderPrompt;
    private Task? _background;
    private bool _disposed;
    private readonly HashSet<Task> _inFlight = [];

    // Only CurrentStatus touches these (one UI timer).
    private Status? _lastStatus;
    private DateTimeOffset? _statusFailedAt;
    private static readonly TimeSpan StatusPause = TimeSpan.FromSeconds(5);
    private DateTimeOffset? _obsSeenAt;
    private static readonly TimeSpan ObsRecheck = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DisposeWait = TimeSpan.FromSeconds(5);

    // Only the reminder loop touches these.
    private ReminderSchedule? _schedule;
    private DateTimeOffset? _scheduleStarted;

    // Only the freeze loop touches these (a re-pick asks for a reset through the flag).
    private ObsClient? _freezeObs;
    private DateTimeOffset? _freezeStarted;
    private string? _freezeLastError;
    private volatile bool _freezeResetRequested;

    private bool StartInProgress
    {
        get { lock (_sync) return _recordingOp == Op.Start; }
    }

    // ---------------------------------------------------------------- commands

    /// <summary>Runs one command: start, stop [name], toggle, repick, import &lt;video&gt; [name], housekeep [-n], migrate [-n].</summary>
    public Task<CommandResult> RunAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        var copy = args.ToArray();
        var run = Task.Run(() => RunOffThreadAsync(copy, ct), CancellationToken.None);
        // DisposeAsync waits for commands in flight, so a cancelled start or stop finishes its cleanup first.
        lock (_sync) _inFlight.Add(run);
        _ = run.ContinueWith(t => { lock (_sync) _inFlight.Remove(t); },
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return run;
    }

    private async Task<CommandResult> RunOffThreadAsync(string[] args, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _life.Token);
        var token = linked.Token;
        string? Arg(int i) => args.Length > i && args[i].Length > 0 ? args[i] : null;
        try
        {
            switch (Arg(0))
            {
                case "start" or "toggle" when SoundOnly(Arg(1)) is null:
                    return Die($"usage: sakshi {args[0]} [--audio|--video]");
                case "start":
                {
                    var soundOnly = SoundOnly(Arg(1))!.Value;
                    return await RecordingOpAsync(Op.Start, t => StartLockedAsync(t, soundOnly), token).ConfigureAwait(false);
                }
                case "stop":
                    return await RecordingOpAsync(Op.Stop, t => StopLockedAsync(Arg(1), t), token).ConfigureAwait(false);
                case "toggle":
                {
                    var soundOnly = SoundOnly(Arg(1))!.Value;
                    return await RecordingOpAsync(Op.Toggle, t => ToggleLockedAsync(t, soundOnly), token).ConfigureAwait(false);
                }
                case "repick":
                    return await RecordingOpAsync(Op.Repick, RepickLockedAsync, token).ConfigureAwait(false);
                case "import":
                {
                    if (Arg(1) is not { } video) return Die("usage: sakshi import <video> [name]");
                    if (!File.Exists(video)) return Die($"No such file: {video}");
                    return await RecordingOpAsync(Op.Import, t => ImportLockedAsync(video, Arg(2), t), token).ConfigureAwait(false);
                }
                case "housekeep" or "migrate":
                {
                    var command = args[0];
                    bool dryRun;
                    if (args.Length == 1) dryRun = false;
                    else if (args is [_, "-n" or "--dry-run"]) dryRun = true;
                    else return new CommandResult(2, "", $"usage: sakshi {command} [-n]\n");
                    return await GatedAsync(() => command == "housekeep" ? Housekeep(dryRun) : Migrate(dryRun), token).ConfigureAwait(false);
                }
                default:
                    return new CommandResult(2, "", $"sakshi: unknown command '{(args.Length > 0 ? args[0] : "")}'\n");
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Every path leaves a resumable state; the caller (a Quit, a client gone) needs no message.
            _s.Log.Info($"sakshi {string.Join(' ', args)}: cancelled");
            return new CommandResult(130, "", "");
        }
        catch (Exception e)
        {
            // The public surface never faults: the IPC server and the tray get an answer either way.
            _s.Log.Info($"sakshi {string.Join(' ', args)} failed: {e}");
            return Die($"Something went wrong: {e.Message} (see sakshi.log)");
        }
    }

    /// <summary>
    /// Claims the single recording-command slot, then takes the gate. A recording command never waits
    /// for another: it is answered at once, as bash's flock -n did.
    /// </summary>
    private async Task<CommandResult> RecordingOpAsync(Op op, Func<CancellationToken, Task<CommandResult>> body, CancellationToken ct)
    {
        Op busy;
        CancellationTokenSource? reminder;
        lock (_sync)
        {
            busy = _recordingOp;
            if (busy == Op.None) _recordingOp = op;
            reminder = _reminderPrompt;
        }
        if (busy != Op.None) return Busy(op, busy);

        // The click answers the reminder: close its prompt instead of queueing behind it.
        if (reminder is not null)
        {
            try { await reminder.CancelAsync().ConfigureAwait(false); }
            catch (ObjectDisposedException) { }
        }

        try
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            lock (_sync) _recordingOp = Op.None;
            throw;
        }
        try
        {
            return await body(ct).ConfigureAwait(false);
        }
        finally
        {
            // Free the slot before the gate, so whoever gets the gate next sees it free.
            lock (_sync) _recordingOp = Op.None;
            _gate.Release();
        }
    }

    private CommandResult Busy(Op op, Op busy)
    {
        if (op is Op.Toggle or Op.Start && busy == Op.Start) return Notice(StillBusy);
        if (busy == Op.Repick) return Notice(StillBusy);
        if (op is Op.Toggle or Op.Stop && _s.Store.Load().Capture.Phase == CapturePhase.Naming) return Notice(TypeTheName);
        return Die(AlreadyInProgress);
    }

    private async Task<CommandResult> GatedAsync(Func<CommandResult> body, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return body();
        }
        finally
        {
            _gate.Release();
        }
    }

    private void Claim(Op op)
    {
        lock (_sync) _recordingOp = op;
    }

    /// <summary>
    /// "--audio": sound only; "--video": the window and its sound; nothing: the recordAudioOnly setting decides. Null
    /// for anything else.
    /// </summary>
    private bool? SoundOnly(string? flag) => flag switch
    {
        null => _s.Config.RecordAudioOnly,
        "--audio" => true,
        "--video" => false,
        _ => null,
    };

    private Task<CommandResult> ToggleLockedAsync(CancellationToken ct, bool soundOnly)
    {
        switch (_s.Store.Load().Capture.Phase)
        {
            case CapturePhase.Recording or CapturePhase.Naming:
                Claim(Op.Stop);
                return StopLockedAsync(null, ct);
            case CapturePhase.Starting:
                return Task.FromResult(Notice(StillBusy));
            default:
                Claim(Op.Start);
                return StartLockedAsync(ct, soundOnly);
        }
    }

    private async Task<CommandResult> StartLockedAsync(CancellationToken ct, bool soundOnly)
    {
        // The Starter runs housekeep first (bash cmd_start did), so a start holds the recording slot
        // and the gate while old day folders are archived: a click meanwhile gets "Still busy".
        // Starter reports its own failures (log + notification) and rethrows only a cancellation.
        var code = await _s.Starter.StartAsync(ct, soundOnly).ConfigureAwait(false);
        return new CommandResult(code, "", "");
    }

    /// <summary>Asks the user to pick the recorded window again, while the recording carries on.</summary>
    private async Task<CommandResult> RepickLockedAsync(CancellationToken ct)
    {
        if (_s.CaptureRecovery is not { } recovery || _s.ConnectObs is not { } connect) return Die(RepickUnsupported);
        if (_s.Store.Load().Capture.Phase != CapturePhase.Recording) return Notice(NothingToRepick);
        if (_s.Store.Load().Capture.AudioOnly) return Notice(SoundOnlyNoWindow);
        try
        {
            await using var obs = await connect(ct).ConfigureAwait(false);
            await recovery.RepickAsync(obs, _s.Config.ObsScene, _s.Config.ObsSource, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is InvalidOperationException or ObsRequestException or TimeoutException)
        {
            _s.Log.Info($"repick: {e}");
            return Die($"Could not re-pick the window: {e.Message}");
        }
        _freezeResetRequested = true;
        _s.Log.Info("repick: done");
        return new CommandResult(0, "", "");
    }

    private Task<CommandResult> StopLockedAsync(string? name, CancellationToken ct) =>
        GuardAsync("stop", () => _s.Recorder.StopAsync(name, ct));

    private Task<CommandResult> ImportLockedAsync(string video, string? name, CancellationToken ct) =>
        GuardAsync("import", () => _s.Recorder.ImportAsync(video, name, ct));

    /// <summary>
    /// The Recorder reports its own failures; anything it throws besides a cancellation is a bug, caught
    /// here so the tray stays up. The state it leaves is resumable: the next stop picks it up.
    /// </summary>
    private async Task<CommandResult> GuardAsync(string what, Func<Task<int>> run)
    {
        try
        {
            return new CommandResult(await run().ConfigureAwait(false), "", "");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _s.Log.Info($"{what} failed: {e}");
            return Die($"Something went wrong during {what}: {e.Message} (see sakshi.log)");
        }
    }

    private CommandResult Housekeep(bool dryRun)
    {
        var cfg = _s.Config;
        if (!Directory.Exists(cfg.MeetingsDir)) return Die($"No meetings directory: {cfg.MeetingsDir}");
        IReadOnlyList<string> lines;
        try
        {
            var busy = _s.Store.Load().Jobs.Select(j => j.Dir).ToList();
            lines = _meetings.Housekeep(cfg.KeepDays, DateOnly.FromDateTime(_s.Clock.Now.LocalDateTime), busy, dryRun);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _s.Log.Info($"housekeep: {e}");
            return Die($"housekeep failed: {e.Message}");
        }
        // Bash printed the -n lines; a real run only logged them, and its log echoed to stderr.
        return dryRun
            ? new CommandResult(0, Lines(lines), "")
            : new CommandResult(0, "", Lines(lines.Select(l => "housekeep: " + l)));
    }

    private CommandResult Migrate(bool dryRun)
    {
        IReadOnlyList<string> lines;
        try
        {
            lines = _meetings.Migrate(dryRun);
        }
        catch (DirectoryNotFoundException e) when (!Directory.Exists(_s.Config.MeetingsDir))
        {
            return Die(e.Message); // "No meetings directory: …", as bash
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or StoreFailedException)
        {
            _s.Log.Info($"migrate: {e}");
            return Die($"migrate stopped: {e.Message}");
        }
        if (dryRun) return new CommandResult(0, Lines(lines), "");

        // Pass 1 lines start with a meeting folder, pass 2 lines with a day folder.
        var renamed = lines.Count(l => l.IndexOf(" -> ", StringComparison.Ordinal) is var i and >= 0 && DayFolder.TryParse(l[..i], out _));
        var moved = lines.Count - renamed;
        return new CommandResult(0, Lines(lines),
            $"migrate: moved {moved} folder(s) into day directories, renamed {renamed} day folder(s)\n");
    }

    private static string Lines(IEnumerable<string> lines) => string.Concat(lines.Select(l => l + "\n"));

    private CommandResult Notice(string message)
    {
        _s.Notifier.Notify("Sakshi", message, Urgency.Low);
        return new CommandResult(0, "", "");
    }

    /// <summary>Bash's die: log, a critical notification, the message on stderr, exit 1.</summary>
    private CommandResult Die(string message)
    {
        _s.Log.Info($"sakshi: {message}");
        _s.Notifier.Notify("Sakshi", message, Urgency.Critical);
        return new CommandResult(1, "", $"sakshi: {message}\n");
    }

    // ---------------------------------------------------------------- status

    /// <summary>
    /// The bar status. Never takes the gate (lockHeld = the gate is taken). Clears stale entries like
    /// bash's cmd_status: Recording/Starting while OBS is not running (unless a start is under way in
    /// this process: OBS may still be launching), and Naming whose video is gone.
    /// </summary>
    public Status CurrentStatus()
    {
        var now = _s.Clock.Now;
        // After a failed read, pause reads for a while: StateStore retries with sleeps, and this runs
        // on the UI thread every second.
        if (_statusFailedAt is { } failed && now - failed < StatusPause) return _lastStatus ?? Unreadable;
        try
        {
            var state = _s.Store.Load();
            if (!Disposed) state = ClearStale(state, now);
            if (_statusFailedAt is not null) _s.Log.Info("status: the state is readable again");
            _statusFailedAt = null;
            return _lastStatus = StatusReport.Compute(state, now, lockHeld: _gate.CurrentCount == 0);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            if (_statusFailedAt is null) _s.Log.Info($"status: cannot read the state ({e.Message})");
            _statusFailedAt = now;
            return _lastStatus ?? Unreadable;
        }
    }

    private static readonly Status Unreadable = new("idle", "State unreadable — see sakshi.log", "");

    private bool Disposed
    {
        get { lock (_sync) return _disposed; }
    }

    /// <summary>
    /// Whether OBS is running, for the status timer. A process scan every second is wasteful: a running
    /// OBS is believed for 5 s (a quit is noticed that much later). "Not running" is never remembered,
    /// so a recording started just after a clear is never cleared on old news.
    /// </summary>
    private bool ObsRunningRecently(DateTimeOffset now)
    {
        if (_obsSeenAt is { } seen && now - seen < ObsRecheck && now >= seen) return true;
        _obsSeenAt = _s.ObsRunning() ? now : null;
        return _obsSeenAt is not null;
    }

    private SakshiState ClearStale(SakshiState state, DateTimeOffset now)
    {
        var seen = state.Capture;
        switch (seen.Phase)
        {
            case CapturePhase.Recording or CapturePhase.Starting
                when !StartInProgress && !ObsRunningRecently(now):
                state = _s.Store.Update(s => s.Capture == seen && !StartInProgress ? s with { Capture = Capture.Idle } : s);
                if (state.Capture.Phase == CapturePhase.Idle)
                    _s.Log.Info($"status: state said {seen.Phase.ToString().ToLowerInvariant()} but OBS is not running; cleared");
                break;
            case CapturePhase.Naming when seen.Video is null || !File.Exists(seen.Video):
                state = _s.Store.Update(s => s.Capture == seen ? s with { Capture = Capture.Idle } : s);
                if (state.Capture.Phase == CapturePhase.Idle)
                    _s.Log.Info($"status: the unnamed recording {seen.Video} is gone; cleared");
                break;
        }
        return state;
    }

    // ---------------------------------------------------------------- background

    /// <summary>
    /// Starts the background work and returns the task running it, which completes (without throwing)
    /// once <paramref name="ct"/> is cancelled or the controller is disposed. Do not await it at startup.
    /// The transcription queue: Recover(), then RunAsync, restarted 5 s after any failure. The
    /// reminder: every 15 s, while recording. The freeze check: every 5 s, while recording.
    /// </summary>
    public Task StartBackgroundAsync(CancellationToken ct)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_background is not null) throw new InvalidOperationException("The background work is already running.");
            var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _life.Token);
            var token = linked.Token;
            _background = Task.Run(async () =>
            {
                try
                {
                    ClearStaleStarting();
                    await Task.WhenAll(QueueLoopAsync(token), ReminderLoopAsync(token), FreezeLoopAsync(token)).ConfigureAwait(false);
                }
                finally
                {
                    linked.Dispose();
                }
            }, CancellationToken.None);
            return _background;
        }
    }

    /// <summary>A start does not survive the process that ran it: Starting at tray start is stale.</summary>
    private void ClearStaleStarting()
    {
        try
        {
            var before = _s.Store.Load().Capture;
            if (before.Phase != CapturePhase.Starting) return;
            var after = _s.Store.Update(s => s.Capture.Phase == CapturePhase.Starting && !StartInProgress ? s with { Capture = Capture.Idle } : s);
            if (after.Capture.Phase == CapturePhase.Idle) _s.Log.Info("state said starting when the tray started; cleared");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _s.Log.Info($"could not clear a stale start ({e.Message})");
        }
    }

    private async Task QueueLoopAsync(CancellationToken ct)
    {
        // Recover before the first RunAsync, however long the state stays unreadable: running first
        // would re-run a job whose transcription is still alive.
        while (true)
        {
            try
            {
                _s.Queue.Recover();
                break;
            }
            catch (Exception e)
            {
                _s.Log.Info($"transcription queue: recovery failed: {e.Message}; retrying in 5 s");
            }
            try
            {
                await _s.Delay(QueueBackoff, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
        }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _s.Queue.RunAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                _s.Log.Info($"transcription queue stopped: {e.Message}; restarting in 5 s");
            }
            try
            {
                await _s.Delay(QueueBackoff, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private async Task ReminderLoopAsync(CancellationToken ct)
    {
        try
        {
            while (true)
            {
                await _s.Delay(ReminderPoll, ct).ConfigureAwait(false);
                try
                {
                    await ReminderTickAsync(ct).ConfigureAwait(false);
                }
                catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    _s.Log.Info($"reminder failed: {e.Message}");
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    /// <summary>
    /// One reminder check (port of cmd_remind's loop body). When a mark is due it takes the gate,
    /// waiting for any open prompt to close, checks the same recording is still running, and asks.
    /// Only "Stop recording" stops, inside the same gate hold.
    /// </summary>
    internal async Task ReminderTickAsync(CancellationToken ct)
    {
        var cfg = _s.Config;
        var capture = _s.Store.Load().Capture;
        if (capture.Phase != CapturePhase.Recording || capture.Started is not { } started)
        {
            _schedule = null;
            return;
        }
        if (cfg.RemindInterval <= 0) return;
        if (_schedule is null || _scheduleStarted != started)
        {
            _schedule = new ReminderSchedule(TimeSpan.FromSeconds(cfg.RemindInterval), started);
            _scheduleStarted = started;
        }
        if (!_schedule.IsDue(_s.Clock.Now, out var label)) return;

        _s.Log.Info($"reminder: recording for {label}");
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!StillRecording(started)) return;
            using var prompt = CancellationTokenSource.CreateLinkedTokenSource(ct);
            lock (_sync)
            {
                if (_recordingOp != Op.None) return; // a click is already stopping or starting: it wins
                _reminderPrompt = prompt;
            }
            string? pick;
            try
            {
                pick = await _s.Prompter.ChooseAsync($"Recording for {label}",
                    [new PromptRow("", KeepRecording, ""), new PromptRow("", StopRecording, "")],
                    TimeSpan.FromSeconds(cfg.RemindPromptTimeout), prompt.Token).WaitAsync(prompt.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _s.Log.Info("reminder: closed by a click on the tray");
                return;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _s.Log.Info($"reminder: prompt failed: {e.Message}");
                pick = null;
            }
            finally
            {
                lock (_sync) _reminderPrompt = null;
            }

            if (pick != StopRecording)
            {
                _s.Log.Info($"reminder: {(string.IsNullOrEmpty(pick) ? "no answer" : pick)}, still recording");
                return;
            }
            // The bar may have stopped it while the prompt was open.
            if (!StillRecording(started)) return;
            lock (_sync)
            {
                if (_recordingOp != Op.None) return;
                _recordingOp = Op.Stop;
            }
            try
            {
                _s.Log.Info("reminder: stop confirmed");
                await StopLockedAsync(null, ct).ConfigureAwait(false);
            }
            finally
            {
                lock (_sync) _recordingOp = Op.None;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task FreezeLoopAsync(CancellationToken ct)
    {
        if (_s.CaptureRecovery is not { } recovery || _s.ConnectObs is null) return;
        var watch = new FreezeWatch(_s.Config, recovery, _s.Notifier, _s.Log, _s.Clock);
        try
        {
            while (true)
            {
                await _s.Delay(FreezeWatch.Poll, ct).ConfigureAwait(false);
                try
                {
                    await FreezeTickAsync(watch, ct).ConfigureAwait(false);
                    _freezeLastError = null;
                }
                catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    // OBS busy or gone, hyprctl failing: log each new message once, connect afresh next time.
                    if (e.Message != _freezeLastError) _s.Log.Info($"freeze check failed: {e.Message}");
                    _freezeLastError = e.Message;
                    await DropFreezeObsAsync().ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        finally
        {
            await DropFreezeObsAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// One freeze check, while recording: only when the gate is free and no recording command is waiting
    /// for it, so a click never queues behind the check. The connection is kept for the recording.
    /// </summary>
    internal async Task FreezeTickAsync(FreezeWatch watch, CancellationToken ct)
    {
        var capture = _s.Store.Load().Capture;
        // Sound only: OBS's picture is black and still on purpose; there is no window to freeze.
        if (capture.Phase != CapturePhase.Recording || capture.AudioOnly || capture.Started is not { } started)
        {
            if (_freezeStarted is not null)
            {
                _freezeStarted = null;
                await DropFreezeObsAsync().ConfigureAwait(false);
            }
            return;
        }
        if (_freezeStarted != started || _freezeResetRequested)
        {
            _freezeResetRequested = false;
            _freezeStarted = started;
            watch.Reset();
        }
        if (!_gate.Wait(0)) return;
        try
        {
            lock (_sync)
            {
                if (_recordingOp != Op.None) return;
            }
            if (!StillRecording(started)) return;
            _freezeObs ??= await _s.ConnectObs!(ct).ConfigureAwait(false);
            await watch.CheckAsync(_freezeObs, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task DropFreezeObsAsync()
    {
        var obs = _freezeObs;
        _freezeObs = null;
        if (obs is null) return;
        try { await obs.DisposeAsync().ConfigureAwait(false); }
        catch (Exception e) { _s.Log.Info($"freeze check: closing the OBS connection failed: {e.Message}"); }
    }

    private bool StillRecording(DateTimeOffset started)
    {
        var c = _s.Store.Load().Capture;
        return c.Phase == CapturePhase.Recording && c.Started == started;
    }

    // ---------------------------------------------------------------- lifetime

    /// <summary>Cancels the background work and any command in flight, and waits for the background to end.</summary>
    public async ValueTask DisposeAsync()
    {
        Task? background;
        Task[] inFlight;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            background = _background;
            inFlight = [.. _inFlight];
        }
        await _life.CancelAsync().ConfigureAwait(false);
        // A cancelled start puts the state back to Idle, a cancelled stop leaves it resumable: let them finish.
        try
        {
            await Task.WhenAll(inFlight).WaitAsync(DisposeWait).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _s.Log.Info("quit: a command was still running after 5 s; not waiting for it");
        }
        if (background is not null)
        {
            try { await background.ConfigureAwait(false); }
            catch (Exception e) { _s.Log.Info($"background work ended with an error: {e.Message}"); }
        }
    }
}
