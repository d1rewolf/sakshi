using System.Runtime.Versioning;
using Sakshi.App.Install;
using Sakshi.Core.Paths;
using Sakshi.Core.Ports;
using Sakshi.Platform;
using Sakshi.Platform.MacOS;
using Sakshi.Platform.Windows;
using Velopack;

namespace Sakshi.App;

public enum ProgramRoute { Tray, Cli }

public static class Program
{
    /// <summary>No arguments and <c>tray</c> run the tray; everything else is the command line.</summary>
    internal static ProgramRoute Route(string[] args) =>
        args.Length == 0 || args[0] == "tray" ? ProgramRoute.Tray : ProgramRoute.Cli;

    // Synchronous and STA: Avalonia must start on the process's main thread on macOS and on an STA thread
    // on Windows, which an async Main (and the awaits after it) does not guarantee.
    [STAThread]
    public static int Main(string[] args) => Start(args, Route(args));

    /// <summary>The GUI-subsystem entry (<c>sakshiw.exe</c>): always the tray, whatever the arguments.</summary>
    public static int RunTray(string[] args) => Start(args, ProgramRoute.Tray);

    private static int Start(string[] args, ProgramRoute route)
    {
        if (NeedsVelopack(route, args, Environment.GetEnvironmentVariable)) StartVelopack(args);
        if (OperatingSystem.IsMacOS())
            Environment.SetEnvironmentVariable("PATH", MacPath.WithHomebrew(Environment.GetEnvironmentVariable("PATH"), Directory.Exists));
        // Quotes, ellipses and dashes appear in find output; see ConsoleEncoding.
        using var console = ConsoleEncoding.Apply();

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        if (route == ProgramRoute.Tray) return Tray.TrayEntry.Run(Console.Out, Console.Error, cts.Token);
        return Commands.RunAsync(args, Console.Out, Console.Error, home: null, isTty: !Console.IsOutputRedirected, cts.Token).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Velopack has work only in the tray, in the hooks it launches us for (<c>--veloapp-*</c>; <c>--squirrel-*</c>
    /// it ignores but still exits on) and in a launch it marks as a first run or restart. A plain CLI call skips it:
    /// every Velopack start appends to Velopack's own log file (the temp dir, ~/Library/Logs or
    /// %LocalAppData%\velopack), and its SetLogger only adds a logger next to that file, it cannot replace it.
    /// </summary>
    internal static bool NeedsVelopack(ProgramRoute route, string[] args, Func<string, string?> env) =>
        route == ProgramRoute.Tray
        || (args.Length > 0 && (args[0].StartsWith("--veloapp-", StringComparison.OrdinalIgnoreCase)
                                || args[0].StartsWith("--squirrel-", StringComparison.OrdinalIgnoreCase)))
        || !string.IsNullOrWhiteSpace(env("VELOPACK_FIRSTRUN"))
        || !string.IsNullOrWhiteSpace(env("VELOPACK_RESTART"));

    /// <summary>
    /// Must run first in every entry point that <see cref="NeedsVelopack"/>: when Velopack launches the app for an install or uninstall hook it
    /// runs the hook and exits here; otherwise (also when not installed) it returns at once. Updates are applied
    /// only by running a newer installer, so a CLI call never relaunches itself to apply one.
    /// </summary>
    public static void StartVelopack(string[] args)
    {
        // The running exe's directory is Velopack's "current" directory when installed. Without the trailing
        // separator, so the entry added to PATH is exactly the one uninstall removes.
        var cliDir = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        var paths = SakshiPaths.Current();
        var choice = new AutostartChoice(paths.StateDir);
        var hooks = new InstallHooks(PlatformAdapters.CreateAutostart(
                OperatingSystem.IsWindows() ? HostOs.Windows : OperatingSystem.IsMacOS() ? HostOs.MacOS : HostOs.Linux), TrayExecutable.Resolve,
            addToPath: OperatingSystem.IsWindows() ? AddToUserPath : null,
            removeFromPath: OperatingSystem.IsWindows() ? RemoveFromUserPath : null, new FileLog(paths.LogFile), () => choice.Made, choice.Record, choice.Forget);
        var app = VelopackApp.Build()
            .SetAutoApplyOnStartup(false)
            .OnFirstRun(_ => hooks.OnFirstRun());
        // The install and uninstall hooks only exist on Windows; elsewhere the first run does the work.
        if (OperatingSystem.IsWindows())
            app = app.OnAfterInstallFastCallback(_ => hooks.OnInstalled(cliDir))
                .OnBeforeUninstallFastCallback(_ => hooks.OnUninstalling(cliDir));
        app.Run();
    }

    [SupportedOSPlatform("windows")]
    private static void AddToUserPath(string dir) => UserPath.Apply(p => UserPath.Add(p, dir));

    [SupportedOSPlatform("windows")]
    private static void RemoveFromUserPath(string dir) => UserPath.Apply(p => UserPath.Remove(p, dir));

    /// <summary>In-process entry for tests. <paramref name="home"/> replaces the user's home directory.</summary>
    public static int Run(string[] args, TextWriter stdout, TextWriter stderr, string? home = null,
        Sakshi.Platform.Doctor.DoctorProbes? doctorProbes = null) =>
        Commands.RunAsync(args, stdout, stderr, home, isTty: false, CancellationToken.None, doctorProbes: doctorProbes).GetAwaiter().GetResult();
}
