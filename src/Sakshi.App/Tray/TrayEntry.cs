using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Sakshi.App.Notifications;
using Sakshi.App.Prompts;
using Sakshi.Core.Paths;
using Sakshi.Core.Ports;

namespace Sakshi.App.Tray;

/// <summary>Starts the tray process. Never called from tests (it opens a real Avalonia session).</summary>
internal static class TrayEntry
{
    public static int Run(TextWriter stdout, TextWriter stderr, CancellationToken ct)
    {
        var paths = SakshiPaths.Current();
        var os = OperatingSystem.IsWindows() ? HostOs.Windows : OperatingSystem.IsMacOS() ? HostOs.MacOS : HostOs.Linux;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var (exit, prepared) = Prepare(paths, stdout, stderr,
            () => TrayInstance.TryAcquire(paths.StateDir),
            () => TrayServices.Create(paths, os, home, new AvaloniaPrompter(),
                OperatingSystem.IsWindows() ? new LabsNotifier(new FileLog(paths.LogFile)) : null));
        if (prepared is not { } host) return exit;
        var services = host.Services;

        TrayApp.Host = host;
        TrayApp.Interrupt = ct;   // TrayApp starts the host and registers the quit once the dispatcher exists
        try
        {
            var builder = AppBuilder.Configure<TrayApp>()
                .UsePlatformDetect()
                .With(new X11PlatformOptions { WmClass = "sakshi" })
                .With(new MacOSPlatformOptions { ShowInDock = false });   // a tray app has no dock icon
            // Toasts on Windows only: Linux keeps notify-send and macOS osascript (Labs needs a bundle id there).
            if (OperatingSystem.IsWindows()) builder = LabsNotifier.Register(builder);
            return builder.StartWithClassicDesktopLifetime([], lifetime => lifetime.ShutdownMode = ShutdownMode.OnExplicitShutdown);
        }
        catch (Exception e)
        {
            services.Log.Info($"tray: could not start the tray UI: {e}");
            stderr.WriteLine($"Error: could not start the tray UI: {e.Message}");
            return 1;
        }
        finally
        {
            // Avalonia leaves its dispatcher context on this thread; blocking on an await would wait on a loop that is gone.
            SynchronizationContext.SetSynchronizationContext(null);
            host.StopAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
        }
    }

    /// <summary>
    /// Everything before Avalonia: the tray lock, then the services. Started from a keybinding, nobody
    /// reads stderr, so every failure also goes to sakshi.log. Returns the host (not yet started), or
    /// null with the exit code: 0 when another tray already runs, 1 on a failure (the lock released).
    /// </summary>
    internal static (int Exit, TrayHost? Host) Prepare(SakshiPaths paths, TextWriter stdout,
        TextWriter stderr, Func<TrayInstance?> acquire, Func<TrayServices> create)
    {
        var log = new FileLog(paths.LogFile);
        int Fail(string message, Exception e)
        {
            log.Info($"tray: {message} ({e.GetType().Name}: {e.Message})");
            stderr.WriteLine(message);
            return 1;
        }

        TrayInstance? instance;
        try { instance = acquire(); }
        catch (InvalidOperationException e) { return (Fail(e.Message, e), null); }   // the private socket dir is not ours
        catch (Exception e) { return (Fail($"Error: could not take the tray lock: {e.Message}", e), null); }
        if (instance is null)
        {
            stdout.WriteLine("sakshi is already running");
            return (0, null);
        }

        try
        {
            var services = create();
            return (0, new TrayHost(services, new TrayController(services), instance));
        }
        catch (Exception e)
        {
            instance.DisposeAsync().AsTask().GetAwaiter().GetResult();
            return (Fail($"Error: {e.Message}", e), null);
        }
    }
}
