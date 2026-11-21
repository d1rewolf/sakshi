using Sakshi.Core.Config;
using Sakshi.Core.Paths;
using Sakshi.Core.Ports;
using Sakshi.Platform.Autostart;
using Sakshi.Platform.Linux;
using Sakshi.Platform.MacOS;
using Sakshi.Platform.Windows;

namespace Sakshi.Platform;

// MacTrash and RecycleBin are OS-annotated, but only their MoveToTrash needs the OS. Create runs for any
// HostOs (the tests do it on Linux), and the trash is used only on the OS it was chosen for.
#pragma warning disable CA1416

/// <summary>The parts of the app that differ per OS, chosen in one place.</summary>
public sealed record PlatformAdapters(
    IObsLauncher Launcher, IWindowCapture Capture, INotifier Notifier, ITrash Trash, string ObsProcessName,
    IAutostart Autostart)
{
    /// <summary>Mending a frozen capture while recording; only Linux under Hyprland has one (see <see cref="ICaptureRecovery"/>).</summary>
    public ICaptureRecovery? Recovery { get; init; }

    /// <summary>
    /// Builds the adapters for <paramref name="os"/>. Nothing here touches a file, the registry or a
    /// process: every adapter does its work later (the Windows registry read is inside LaunchAsync).
    /// <paramref name="uiNotifier"/> is the Windows toast notifier, which lives in the App project.
    /// </summary>
    public static PlatformAdapters Create(HostOs os, SakshiConfig config, SakshiPaths paths, IProcessRunner runner,
        IPrompter prompter, ILog log, INotifier? uiNotifier = null)
    {
        switch (os)
        {
            case HostOs.MacOS:
            {
                var notifier = new SafeNotifier(new OsascriptNotifier(runner, log), log);
                var install = new MacObsInstall(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Directory.Exists);
                var capture = new ObsWindowChooser(ChooserOs.MacOS, config, prompter, log,
                    (obs, scene, source, ct) => MacMeetingAudio.EnsureAsync(obs, scene, source, log, ct));
                return new PlatformAdapters(new ObsLauncher(config, paths, install, runner, log), capture,
                    notifier, new MacTrash(), install.ProcessName,
                    CreateAutostart(os));
            }
            case HostOs.Windows:
            {
                var notifier = new SafeNotifier(uiNotifier ?? new LogOnlyNotifier(log), log);
                var install = new WindowsObsInstall(WindowsObsInstall.ReadRegistryInstallDir, File.Exists,
                    Environment.GetEnvironmentVariable);
                return new PlatformAdapters(new ObsLauncher(config, paths, install, runner, log),
                    new ObsWindowChooser(ChooserOs.Windows, config, prompter, log),
                    notifier, new RecycleBin(), install.ProcessName,
                    CreateAutostart(os));
            }
            default:
            {
                var notifier = new SafeNotifier(new NotifySendNotifier(runner, log), log);
                var install = new LinuxObsInstall(runner);
                var capture = new PipeWireWindowCapture(config, paths, runner, notifier, log, Task.Delay);
                return new PlatformAdapters(new ObsLauncher(config, paths, install, runner, log), capture,
                    notifier, new GioTrash(runner), install.ProcessName,
                    CreateAutostart(os))
                {
                    Recovery = capture.UnderHyprland ? new HyprlandCaptureRecovery(capture, runner) : null,
                };
            }
        }
    }

    /// <summary>The start-at-login mechanism of <paramref name="os"/>; builds an object only, nothing is read or written.</summary>
    public static IAutostart CreateAutostart(HostOs os) => os switch
    {
        HostOs.MacOS => new MacLaunchAgentAutostart(UserHome()),
        HostOs.Windows => new WindowsRunKeyAutostart(OperatingSystem.IsWindows() ? new RegistryRunKey() : new NoRunKey()),
        _ => new XdgAutostart(XdgConfigHome()),
    };

    private static string UserHome() => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static string XdgConfigHome() =>
        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } x && Path.IsPathRooted(x)
            ? x : Path.Combine(UserHome(), ".config");

    /// <summary>Windows adapters built on another OS (the tests): autostart is simply never on.</summary>
    private sealed class NoRunKey : IRunKey
    {
        public string? Get(string name) => null;
        public void Set(string name, string value) { }
        public void Delete(string name) { }
    }

    /// <summary>Windows with no toast notifier (the CLI): messages are logged only.</summary>
    private sealed class LogOnlyNotifier(ILog log) : INotifier
    {
        public void Notify(string headline, string body, Urgency urgency) => log.Info($"notification: {headline}: {body}");
    }
}
