using Sakshi.Core.Config;
using Sakshi.Core.HandOff;
using Sakshi.Core.Obs;
using Sakshi.Core.Paths;
using Sakshi.Core.Ports;
using Sakshi.Core.Recording;
using Sakshi.Core.State;
using Sakshi.Core.Store;
using Sakshi.Core.Transcription;
using Sakshi.Platform;
using Sakshi.Platform.Autostart;
using Sakshi.Platform.Doctor;
using Sakshi.Transcribe;

namespace Sakshi.App.Tray;

/// <summary>
/// Everything the <see cref="TrayController"/> works with. <see cref="Create"/> builds the production set;
/// tests build one from fakes. There is exactly one <see cref="StateStore"/> in it, shared by every part.
/// </summary>
public sealed record TrayServices(
    SakshiPaths Paths,
    HostOs Os,
    string Home,
    SakshiConfig Config,
    StateStore Store,
    Recorder Recorder,
    Starter Starter,
    TranscriptionQueue Queue,
    IPrompter Prompter,
    INotifier Notifier,
    ILog Log,
    IClock Clock,
    IProcessRunner ProcessRunner)
{
    /// <summary>The controller's waits (the reminder poll, the queue's restart backoff).</summary>
    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;

    /// <summary>The OBS process name on this OS (obs, OBS, obs64); <see cref="Create"/> sets it from the adapters.</summary>
    public string ObsProcessName { get; init; } = "obs";

    /// <summary>Start at login; null (tests) leaves the menu item out.</summary>
    public IAutostart? Autostart { get; init; }

    /// <summary>The probes for the first-start check; null (tests) skips the check.</summary>
    public DoctorProbes? DoctorProbes { get; init; }

    /// <summary>The executable autostart launches (see <see cref="Install.TrayExecutable"/>).</summary>
    public string TrayExecutable { get; init; } = "";

    /// <summary>Whether this is an installed copy (see <see cref="Install.InstalledCopy"/>); tests default to no.</summary>
    public Func<bool> IsInstalledCopy { get; init; } = () => false;

    /// <summary>Mending a frozen capture while recording; null where the platform has none (and in tests that do not need it).</summary>
    public ICaptureRecovery? CaptureRecovery { get; init; }

    /// <summary>Opens a connection to OBS (the freeze check and re-pick use it); null in tests that do not need it.</summary>
    public Func<CancellationToken, Task<ObsClient>>? ConnectObs { get; init; }

    /// <summary>The AppImage this tray runs from (Linux); null otherwise and in tests.</summary>
    public string? AppImage { get; init; }

    /// <summary>Whether OBS is running: the stale-state check and the Recorder's connect ask this.</summary>
    public Func<bool> ObsRunning
    {
        get => _obsRunning ?? (() => ProcessRunner.IsRunning(ObsProcessName));
        init => _obsRunning = value;
    }

    private readonly Func<bool>? _obsRunning;

    /// <summary>
    /// The production services for this desktop. On first start (no config.json yet) the bash config
    /// is imported into config.json. Throws <see cref="InvalidDataException"/> for a broken config.json.
    /// <paramref name="uiNotifier"/> is the Windows toast notifier (it lives in this project, not in Platform).
    /// </summary>
    public static TrayServices Create(SakshiPaths paths, HostOs os, string home, IPrompter prompter,
        INotifier? uiNotifier = null)
    {
        var log = new FileLog(paths.LogFile);
        var runner = new ProcessRunner();
        var config = LoadConfig(paths, os, home, log);
        var clock = new SystemClock();

        var adapters = PlatformAdapters.Create(os, config, paths, runner, prompter, log, uiNotifier);
        var notifier = adapters.Notifier;

        var store = new StateStore(paths.StateFile, log, notifier);
        var meetings = new MeetingStore(config.MeetingsDir, config.EffectiveArchiveDir, log);
        var handOff = new HandOffRunner(new CommandResolver(config.HandOffPath, Environment.GetEnvironmentVariable), notifier);
        // The child is this same executable: the tray must run as the published apphost (sakshi), not as
        // `dotnet sakshi.dll`, where ProcessPath is the dotnet host and "dotnet transcribe" would fail.
        // On Windows the tray is sakshiw.exe, which only ever runs the tray: the child must be sakshi.exe.
        var transcriber = new ChildProcessTranscriber(CliExecutable.Resolve(), ["transcribe"]);
        var queue = new TranscriptionQueue(store, transcriber, handOff, notifier, log, TranscriptionQueue.IsProcessAlive);

        var launcher = adapters.Launcher;
        Func<CancellationToken, Task<ObsClient>> connect =
            ct => ObsClient.ConnectAsync(config.ObsWsHost, config.ObsWsPort, launcher.Password(), ct);

        Func<bool> obsRunning = () => runner.IsRunning(adapters.ObsProcessName);
        var starter = new Starter(config, store, meetings, launcher, adapters.Capture, notifier, clock, log, connect, obsRunning);
        // null = OBS is not running (the Recorder clears the capture); a connect failure throws (the state is kept).
        var recorder = new Recorder(config, store, meetings, prompter, notifier, adapters.Trash, clock, log, queue,
            async ct => obsRunning() ? new ObsRecording(await connect(ct).ConfigureAwait(false)) : null);

        return new TrayServices(paths, os, home, config, store, recorder, starter, queue, prompter, notifier, log, clock, runner)
        {
            ObsProcessName = adapters.ObsProcessName,
            Autostart = adapters.Autostart,
            TrayExecutable = Install.TrayExecutable.Resolve(),
            IsInstalledCopy = Install.InstalledCopy.Detect,
            AppImage = OperatingSystem.IsLinux() && Environment.GetEnvironmentVariable("APPIMAGE") is { Length: > 0 } image ? image : null,
            ObsRunning = obsRunning,
            CaptureRecovery = adapters.Recovery,
            ConnectObs = connect,
            DoctorProbes = DoctorProbes.Production(os, runner, FfmpegExecutable.Bundled()),
        };
    }

    private static SakshiConfig LoadConfig(SakshiPaths paths, HostOs os, string home, ILog log)
    {
        if (!File.Exists(paths.ConfigFile))
        {
            var bash = Environment.GetEnvironmentVariable("SAKSHI_CONFIG") is { Length: > 0 } c
                ? c : Path.Combine(home, ".config", "sakshi", "config");
            if (File.Exists(bash))
            {
                var imported = BashConfigImport.Import(File.ReadAllText(bash), os, home, File.Exists, log.Info);
                try
                {
                    ConfigStore.Save(paths.ConfigFile, imported);
                    log.Info($"config: imported {bash} into {paths.ConfigFile}");
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // BashConfigImport has already expanded ~ and $HOME: the imported settings are usable as they are.
                    log.Info($"config: could not write {paths.ConfigFile} ({e.Message}); using the imported settings for now");
                    return imported;
                }
            }
        }
        return ConfigStore.Load(paths.ConfigFile, os, home, log.Info);
    }
}

internal sealed class SystemClock : IClock
{
    public DateTimeOffset Now => DateTimeOffset.Now;
}
