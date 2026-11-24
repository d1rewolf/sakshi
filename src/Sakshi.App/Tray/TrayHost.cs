using Sakshi.Core.Config;
using Sakshi.Core.Paths;
using Sakshi.Core.Ports;
using Sakshi.Core.State;
using Sakshi.Platform;
using Sakshi.Platform.Doctor;
using Sakshi.Platform.Linux;

namespace Sakshi.App.Tray;

/// <summary>
/// Everything the tray icon calls into and Quit tears down: the controller, the IPC server, the
/// background work and the instance lock. No Avalonia in here, so it is tested headlessly.
/// </summary>
internal sealed class TrayHost(TrayServices services, TrayController controller, TrayInstance instance)
{
    private readonly CancellationTokenSource _quit = new();
    private int _stopped;

    public TrayServices Services => services;
    public CancellationToken QuitToken => _quit.Token;
    public Task Ipc { get; private set; } = Task.CompletedTask;
    public Task Background { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// The checks to open the setup window with at this start, or null to leave it shut. It opens when something
    /// Sakshi needs is missing, or at the first start of an installed copy. Null at once when the services carry
    /// no probes; never faults.
    /// </summary>
    public Task<IReadOnlyList<DoctorCheck>?> SetupAtStart { get; private set; } = Task.FromResult<IReadOnlyList<DoctorCheck>?>(null);

    /// <summary>Starts the IPC server, the background work and the setup check. None is awaited.</summary>
    public void Start()
    {
        Ipc = Task.Run(async () =>
        {
            try { await TrayIpc.ServeAsync(instance.SocketPath, controller.RunAsync, _quit.Token).ConfigureAwait(false); }
            catch (Exception e) when (!_quit.IsCancellationRequested)
            {
                services.Log.Info($"ipc server stopped: {e}");
                services.Notifier.Notify("Sakshi",
                    "Could not open the command socket — keybinding commands won't reach the tray. See sakshi.log", Urgency.Critical);
            }
        });
        Background = controller.StartBackgroundAsync(_quit.Token);
        SetupAtStart = Task.Run(CheckAtStart);
        // OBS stopped while on Sakshi's settings (it or Sakshi was killed): point it back at the user's.
        if (services.ObsSettings is { } obsSettings && !services.ObsRunning()) _ = Task.Run(obsSettings.RepairWhileObsStopped);
    }

    private IReadOnlyList<DoctorCheck>? CheckAtStart()
    {
        if (services.DoctorProbes is null) return null;
        try
        {
            var checks = RunDoctor();
            // Advice (an optional picker) never opens the window by itself, or it would open at every login.
            var missing = checks.Where(c => !c.Ok && c.Required).Select(c => c.Name).ToList();
            if (missing.Count > 0)
            {
                services.Log.Info($"doctor: missing {string.Join(", ", missing)}");
                return checks;
            }
            return services.IsInstalledCopy() && !new Install.SetupSeen(services.Paths.StateDir).Seen ? checks : null;
        }
        catch (Exception e)
        {
            services.Log.Info($"doctor: start check failed: {e.Message}");
            return null;
        }
    }

    /// <summary>The setup window's checks (doctor's). Blocking: call off the UI thread. Never throws.</summary>
    public IReadOnlyList<DoctorCheck> RunDoctor() => services.DoctorProbes is { } probes
        ? Doctor.Run(services.Os, services.Config, services.Paths, probes)
        : [];

    /// <summary>
    /// The setup window has been shown: an installed copy's later starts open it only when something is missing.
    /// Off the UI thread; never throws.
    /// </summary>
    public Task MarkSetupSeen() => Task.Run(() =>
    {
        try
        {
            var seen = new Install.SetupSeen(services.Paths.StateDir);
            if (!seen.Seen) seen.Record();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { services.Log.Info($"setup: could not record it was shown: {e.Message}"); }
    });

    /// <summary>The setup window's "Open settings": the URL in the desktop's handler, off the UI thread.</summary>
    public void OpenUrl(string url) => OpenWithDesktop(url, () => { });

    /// <summary>The default autostart and the AppImage integration (completed at once until started).</summary>
    public Task InstallUpkeep { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Off the UI thread, in this order in one task, because both write the autostart entry: turn start at login on
    /// for an installed copy that has no recorded choice, then (Linux AppImage) point the menu entry, icon, link and
    /// autostart at where the AppImage is now. Failures are logged and never fatal.
    /// </summary>
    public Task StartInstallUpkeep() => InstallUpkeep = Task.Run(() =>
    {
        EnsureDefaultAutostart();
        EnsureAppImageIntegration();
    });

    /// <summary>
    /// The AppImage has no installer and a silent Windows install may never reach the first-run hook, so the tray
    /// applies the "on by default" itself. Whichever runs first, this or <see cref="Install.InstallHooks.OnFirstRun"/>,
    /// records the choice, and a recorded choice (also the user's own) is never overridden.
    /// </summary>
    private void EnsureDefaultAutostart()
    {
        if (services.Autostart is not { } autostart) return;
        try
        {
            if (!services.IsInstalledCopy()) return;
            var choice = new Install.AutostartChoice(services.Paths.StateDir);
            if (choice.Made) return;
            autostart.Enable(services.TrayExecutable);
            choice.Record();
            services.Log.Info($"autostart: turned on (the default for an installed copy) for {services.TrayExecutable}");
        }
        catch (Exception e) { services.Log.Info($"autostart: could not turn on the default: {e.Message}"); }
    }

    private void EnsureAppImageIntegration()
    {
        if (services.AppImage is not { } appImage) return;
        try
        {
            using var icon = typeof(TrayHost).Assembly.GetManifestResourceStream("sakshi.png")
                ?? throw new InvalidOperationException("the icon resource is missing");
            using var bytes = new MemoryStream();
            icon.CopyTo(bytes);
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            AppImageIntegration.Ensure(appImage, home,
                AppImageIntegration.DataHome(home, Environment.GetEnvironmentVariable("XDG_DATA_HOME")),
                bytes.ToArray(), services.Autostart ?? PlatformAdapters.CreateAutostart(HostOs.Linux), services.Log);
        }
        catch (Exception e) { services.Log.Info($"appimage: integration failed: {e.Message}"); }
    }

    public void Log(string message) => services.Log.Info(message);

    /// <summary>Cheap and never throws; call from the UI timer only.</summary>
    public Status CurrentStatus()
    {
        var status = controller.CurrentStatus();
        // The recording stopped: the live transcript finishes what is left and ends.
        if (status.State != "recording") Volatile.Read(ref _live)?.RecordingEnded();
        return status;
    }

    private readonly SemaphoreSlim _liveGate = new(1, 1);
    private LiveTranscript? _live;
    private DateTimeOffset? _liveStarted;

    /// <summary>
    /// The live transcript of the recording in progress, started the first time it is asked for; after the recording,
    /// the last one (ended). Off the UI thread. Throws <see cref="InvalidOperationException"/> with a message for the user.
    /// </summary>
    public async Task<LiveTranscript> LiveTranscriptAsync()
    {
        await _liveGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var capture = services.Store.Load().Capture;
            if (capture.Phase != CapturePhase.Recording || capture.Started is not { } started)
                return _live ?? throw new InvalidOperationException(
                    "Nothing is being recorded. Start a recording, then open the live transcript.");
            if (_live is { } current && _liveStarted == started) return current;
            if (_live is { } old) await old.DisposeAsync().ConfigureAwait(false);
            var video = await FindRecordingAsync(started).ConfigureAwait(false);
            var live = LiveTranscript.Start(CliExecutable.Resolve(), video, services.Log.Info);
            Volatile.Write(ref _live, live);
            _liveStarted = started;
            return live;
        }
        finally
        {
            _liveGate.Release();
        }
    }

    /// <summary>OBS names the file itself: the newest video in its record folder written since the recording began.</summary>
    private async Task<string> FindRecordingAsync(DateTimeOffset started)
    {
        if (services.ConnectObs is not { } connect) throw new InvalidOperationException("Sakshi cannot reach OBS.");
        string? dir;
        await using (var obs = await connect(_quit.Token).ConfigureAwait(false))
        {
            var r = await obs.RequestAsync("GetRecordDirectory", null, _quit.Token).ConfigureAwait(false);
            dir = r.TryGetProperty("recordDirectory", out var d) ? d.GetString() : null;
        }
        // OBS creates the file as it starts; allow a moment when the window is opened right away.
        for (var i = 0; i < 10; i++)
        {
            // A few seconds' slack only: an older file (the previous meeting's) must not be taken for this one.
            if (Core.Recording.RecordingFiles.NewestSince(dir, started.UtcDateTime - TimeSpan.FromSeconds(5)) is { } video)
                return video;
            await Task.Delay(500, _quit.Token).ConfigureAwait(false);
        }
        throw new InvalidOperationException($"Sakshi could not find OBS's recording in {dir ?? "its recording folder"}.");
    }

    /// <summary>Left click or the menu item: off the UI thread (the controller never faults).</summary>
    public void Toggle() => _ = Task.Run(() => controller.RunAsync(["toggle"], CancellationToken.None));

    /// <summary>A menu item's toggle: "--video" or "--audio" says which kind a start is, whatever the setting.</summary>
    public void Toggle(string kind) => _ = Task.Run(() => controller.RunAsync(["toggle", kind], CancellationToken.None));

    /// <summary>The "Re-pick window" item: pick the recorded window again while recording.</summary>
    public void Repick() => _ = Task.Run(() => controller.RunAsync(["repick"], CancellationToken.None));

    /// <summary>Whether the "Start at login" item is checked: never throws.</summary>
    public bool StartAtLoginEnabled
    {
        get
        {
            try { return services.Autostart?.IsEnabled ?? false; }
            catch (Exception e) { services.Log.Info($"autostart: could not read the setting: {e.Message}"); return false; }
        }
    }

    /// <summary>
    /// The "Start at login" click: flips the real setting (not the menu's check mark, which a platform may
    /// already have flipped) off the UI thread. A failure is logged and shown; the caller re-reads the state.
    /// </summary>
    public Task ToggleStartAtLogin() => Task.Run(() =>
    {
        if (services.Autostart is not { } autostart) return;
        try
        {
            if (autostart.IsEnabled) autostart.Disable();
            else autostart.Enable(services.TrayExecutable);
            // Either direction is the user's choice: a reinstall must keep it.
            new Install.AutostartChoice(services.Paths.StateDir).Record();
        }
        catch (Exception e)
        {
            services.Log.Info($"autostart: {e.Message}");
            services.Notifier.Notify("Sakshi", $"Could not change Start at login: {e.Message}", Urgency.Normal);
        }
    });

    /// <summary>Before the first recording there is no meetings folder: make it, so the opener has one to show.</summary>
    public void OpenMeetings() => OpenWithDesktop(services.Config.MeetingsDir,
        () => Directory.CreateDirectory(services.Config.MeetingsDir));

    /// <summary>The folder with sakshi.log, for a bug report.</summary>
    public void OpenLogFolder() => OpenWithDesktop(Path.GetDirectoryName(services.Paths.LogFile)!,
        () => Directory.CreateDirectory(Path.GetDirectoryName(services.Paths.LogFile)!));

    /// <summary>On a fresh machine there is no config file: write a commented starter, so the editor has one to open.</summary>
    public void OpenConfig() => OpenWithDesktop(services.Paths.ConfigFile, () =>
    {
        if (ConfigStore.CreateStarter(services.Paths.ConfigFile, services.Os))
            services.Log.Info($"config: wrote a starter {services.Paths.ConfigFile}");
    });

    private void OpenWithDesktop(string target, Action ensure) => _ = Task.Run(() =>
    {
        try
        {
            ensure();
            var (file, args) = DesktopOpen.Command(services.Os, target);
            services.ProcessRunner.StartDetached(file, args);
        }
        catch (Exception e) { services.Log.Info($"could not open {target}: {e.Message}"); }
    });

    /// <summary>
    /// Quit: cancel the IPC server and the background work, wait (at most <paramref name="bound"/> each) for
    /// the controller and the server, release the lock. Transcription children keep running.
    /// </summary>
    public async Task StopAsync(TimeSpan bound)
    {
        if (Interlocked.Exchange(ref _stopped, 1) == 1) return;
        try
        {
            if (Volatile.Read(ref _live) is { } live)
                try { await live.DisposeAsync().ConfigureAwait(false); }
                catch (Exception e) { services.Log.Info($"quit: the live transcript did not stop cleanly: {e.Message}"); }
            try { await _quit.CancelAsync().ConfigureAwait(false); }
            catch (Exception e) { services.Log.Info($"quit: cancel failed: {e.Message}"); }
            try { await controller.DisposeAsync().AsTask().WaitAsync(bound).ConfigureAwait(false); }
            catch (Exception e) { services.Log.Info($"quit: the controller did not finish cleanly: {e.Message}"); }
            try { await Ipc.WaitAsync(bound).ConfigureAwait(false); }
            catch (Exception e) { services.Log.Info($"quit: the ipc server did not finish cleanly: {e.Message}"); }
        }
        finally
        {
            try { await instance.DisposeAsync().ConfigureAwait(false); }
            catch (Exception e) { services.Log.Info($"quit: releasing the lock failed: {e.Message}"); }
        }
    }
}
