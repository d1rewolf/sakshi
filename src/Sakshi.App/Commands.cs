using System.Globalization;
using System.Text.Json;
using Sakshi.Core.Config;
using Sakshi.Core.Obs;
using Sakshi.Core.Paths;
using Sakshi.Core.Ports;
using Sakshi.Core.State;
using Sakshi.Core.Store;
using Sakshi.App.Tray;
using Sakshi.Platform;
using Sakshi.Platform.Doctor;
using Sakshi.Platform.Linux;
using Sakshi.Platform.MacOS;
using Sakshi.Search;
using Sakshi.Speakers;
using Sakshi.Transcribe;

namespace Sakshi.App;

/// <summary>How the CLI starts the tray when none is running; tests substitute the runner and the timings.</summary>
internal sealed record TrayLaunch(IProcessRunner Runner, string ProcessPath, TimeSpan RetryEvery, TimeSpan RetryFor)
{
    public static TrayLaunch Default() => new(new ProcessRunner(),
        TrayPath(OperatingSystem.IsWindows(), Environment.ProcessPath, File.Exists),
        TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(10));

    /// <summary>
    /// On Windows the CLI is the console <c>sakshi.exe</c>, which would flash a console window as the tray;
    /// <c>sakshiw.exe</c> beside it is the same app on the GUI subsystem.
    /// </summary>
    internal static string TrayPath(bool windows, string? processPath, Func<string, bool> exists)
    {
        if (windows && processPath is { Length: > 0 } && Path.GetDirectoryName(processPath) is { } dir)
        {
            var gui = Path.Combine(dir, "sakshiw.exe");
            if (exists(gui)) return gui;
        }
        return processPath ?? "sakshi";
    }
}

/// <summary>The command line. start, stop, toggle, repick, import and a real housekeep or migrate go to the running tray.</summary>
internal static class Commands
{
    private const string Usage = """
        usage: sakshi <command> [args]

        commands:
          sakshi find [--all] WORD...      search every meeting (and the archive)
          sakshi index [--rebuild]         refresh the search index
          sakshi transcribe [options] FILE transcribe a video (see: sakshi transcribe -h)
          sakshi status                    one-line JSON for the bar widget
          sakshi config                    print the config file path
          sakshi config import             convert ~/.config/sakshi/config into the JSON config
          sakshi housekeep [-n]            archive day folders older than keepDays (through the tray);
                                           -n only shows which (runs here)
          sakshi migrate [-n]              file meeting folders into day folders (through the tray);
                                           -n only shows how (runs here)
          sakshi doctor                    check OBS, ffmpeg and the rest; says how to fix what is missing
          sakshi obs REQUEST [JSON]        send one obs-websocket request, print the response
          sakshi share-picker [ARGS]       Hyprland share picker wrapper (Linux; set as custom_picker_binary)
          sakshi --version                 print the version
          sakshi tray                      run the tray icon (also: sakshi with no arguments)
          sakshi start [--audio|--video]   start recording (through the tray); --audio records
                                           sound only, --video the window too; neither: the
                                           recordAudioOnly setting decides
          sakshi stop [NAME]               stop recording (through the tray)
          sakshi toggle [--audio|--video]  start or stop recording (through the tray)
          sakshi repick                    pick the recorded window again, without stopping
                                           (through the tray; Linux under Hyprland)
          sakshi import VIDEO [NAME]       import a video (through the tray)
        """;

    private static string Version() => AppVersion.Number;

    private sealed class StderrNotifier(TextWriter stderr) : INotifier
    {
        public void Notify(string headline, string body, Urgency urgency) => stderr.WriteLine($"{headline}: {body}");
    }

    /// <summary>The CLI's notifier: messages go to stderr, and a failure to write one is only logged.</summary>
    private static INotifier Notifier(TextWriter stderr, ILog log) => new SafeNotifier(new StderrNotifier(stderr), log);

    public static async Task<int> RunAsync(string[] args, TextWriter stdout, TextWriter stderr,
        string? home, bool isTty, CancellationToken ct, TrayLaunch? launch = null, DoctorProbes? doctorProbes = null)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            stdout.WriteLine(Usage);
            return 0;
        }

        if (args[0] is "--version")
        {
            stdout.WriteLine(Version());
            return 0;
        }

        var os = OperatingSystem.IsWindows() ? HostOs.Windows : OperatingSystem.IsMacOS() ? HostOs.MacOS : HostOs.Linux;
        home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var paths = home == Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            ? SakshiPaths.Current()
            : SakshiPaths.Resolve(os, home, Environment.GetEnvironmentVariable);
        var command = args[0];
        var rest = args[1..];

        try
        {
            switch (command)
            {
                case "start" or "stop" or "toggle" or "repick" or "import":
                    return await ForwardAsync(args, paths, stdout, stderr, launch, ct);
                case "housekeep" or "migrate":
                    if (rest.Length == 1 && rest[0] == "-n")
                        return Load(paths, os, home, stderr) is { } c ? DryRun(command, c, paths, stderr, stdout) : 1;
                    if (rest.Length == 0) return await ForwardAsync(args, paths, stdout, stderr, launch, ct);
                    stderr.WriteLine($"usage: sakshi {command} [-n]");
                    return 2;
                case "find" or "index":
                {
                    if (rest.Any(a => a == "--root" || a.StartsWith("--root=", StringComparison.Ordinal)))
                    {
                        stderr.WriteLine("sakshi: --root is not supported; searching the meetings and archive folders from the config");
                        return 2;
                    }
                    if (Load(paths, os, home, stderr) is not { } cfg) return 1;
                    return SearchCommand.Run(args, [cfg.MeetingsDir, cfg.EffectiveArchiveDir], paths.IndexFile, stdout, stderr, isTty);
                }
                case "share-picker":
                {
                    if (!OperatingSystem.IsLinux())
                    {
                        stderr.WriteLine("sakshi: share-picker works only on Linux with Hyprland");
                        return 2;
                    }
                    // A broken config must never block screen sharing: say so and use the defaults.
                    var pickerCfg = Load(paths, os, home, stderr) ?? SakshiConfig.Defaults(os, home);
                    // The portal stops the picker with SIGTERM: take the child down with us.
                    using var term = new CancellationTokenSource();
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, term.Token);
                    using var sigterm = OperatingSystem.IsWindows() ? null
                        : System.Runtime.InteropServices.PosixSignalRegistration.Create(
                            System.Runtime.InteropServices.PosixSignal.SIGTERM, c => { c.Cancel = true; term.Cancel(); });
                    return await SharePicker.RunAsync(rest, pickerCfg, (launch ?? TrayLaunch.Default()).Runner,
                        Environment.GetEnvironmentVariable, home, linked.Token, stderr);
                }
                case "transcribe":
                    return await TranscribeAsync(rest, paths, os, home, stdout, stderr, ct);
                case "live-transcribe":
                    return await LiveTranscribeAsync(rest, paths, os, home, stdout, stderr, ct);
                case "status":
                {
                    var log = new FileLog(paths.LogFile);
                    var state = new StateStore(paths.StateFile, log, Notifier(stderr, log)).Load();
                    var trayRunning = TrayInstance.IsRunning(paths.StateDir);
                    stdout.WriteLine(StatusReport.Compute(state, DateTimeOffset.Now, lockHeld: false, trayRunning).ToJson());
                    return 0;
                }
                case "config":
                    if (rest.Length == 0) { stdout.WriteLine(paths.ConfigFile); return 0; }
                    if (rest is ["import"]) return ImportConfig(paths, os, home, stdout, stderr);
                    stderr.WriteLine("usage: sakshi config [import]");
                    return 2;
                case "doctor":
                    return DoctorAsync(rest, os, paths, home, doctorProbes, stdout, stderr);
                case "obs":
                    return await ObsAsync(rest, home, os, paths, stdout, stderr, ct);
                default:
                    stderr.WriteLine($"sakshi: unknown command '{command}'");
                    stderr.WriteLine("Run 'sakshi --help' to list the commands.");
                    return 2;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return 130;
        }
        catch (Exception e)
        {
            stderr.WriteLine($"Error: {e.Message}");
            return 1;
        }
    }

    // A reminder prompt can hold the tray's busy gate for up to 10 minutes and a stop waits for the
    // meeting name for up to 15, so the reply to start, stop, toggle, import, housekeep and migrate
    // may legitimately take that long: wait 30 minutes, not a few seconds.
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Sends the command to the running tray. With none running, starts one detached and retries
    /// every <see cref="TrayLaunch.RetryEvery"/> for <see cref="TrayLaunch.RetryFor"/>; a second CLI
    /// racing this one simply connects to whichever tray wins the lock.
    /// </summary>
    private static async Task<int> ForwardAsync(string[] args, SakshiPaths paths, TextWriter stdout, TextWriter stderr,
        TrayLaunch? launch, CancellationToken ct)
    {
        var send = args.ToArray();
        if (send[0] == "import" && send.Length > 1) send[1] = Path.GetFullPath(send[1]);
        var socket = TrayInstance.SocketPathFor(paths.StateDir, Environment.GetEnvironmentVariable).Path;

        ForegroundRights.Share();
        var result = await TrayIpc.SendAsync(socket, send, ReplyTimeout, ct);
        if (result is null)
        {
            launch ??= TrayLaunch.Default();
            try { launch.Runner.StartDetached(launch.ProcessPath, ["tray"]); }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                new FileLog(paths.LogFile).Info($"could not start the tray ({launch.ProcessPath}): {e}");
                return await FailAsync($"Could not start the sakshi tray: {e.Message}", paths, launch.Runner, stderr);
            }
            var deadline = DateTime.UtcNow + launch.RetryFor;
            while (result is null && DateTime.UtcNow < deadline)
            {
                await Task.Delay(launch.RetryEvery, ct);
                result = await TrayIpc.SendAsync(socket, send, ReplyTimeout, ct);
            }
            if (result is null)
            {
                new FileLog(paths.LogFile).Info($"the tray ({launch.ProcessPath}) did not answer on {socket} within {launch.RetryFor.TotalSeconds:0.#} s");
                return await FailAsync("Could not reach the sakshi tray (see sakshi.log).", paths, launch.Runner, stderr);
            }
        }
        stdout.Write(result.StdOut);
        stderr.Write(result.StdErr);
        return result.ExitCode;
    }

    /// <summary>
    /// A forwarding failure, as bash's die: in the log, on stderr and, on Linux, as a critical desktop
    /// notification on Linux and macOS, since a keybinding has no terminal. Windows has no UI here: it only logs.
    /// Waits (5 s at most) for the notification to go out.
    /// </summary>
    private static async Task<int> FailAsync(string message, SakshiPaths paths, IProcessRunner runner, TextWriter stderr)
    {
        var log = new FileLog(paths.LogFile);
        log.Info($"sakshi: {message}");
        stderr.WriteLine(message);
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            INotifier sender;
            Func<Task> drain;
            if (OperatingSystem.IsMacOS())
            {
                var osa = new OsascriptNotifier(runner, log);
                (sender, drain) = (osa, osa.Drain);
            }
            else
            {
                var send = new NotifySendNotifier(runner, log);
                (sender, drain) = (send, send.Drain);
            }
            new SafeNotifier(sender, log).Notify("Sakshi", message, Urgency.Critical);
            try { await drain().WaitAsync(TimeSpan.FromSeconds(6)); }
            catch (TimeoutException) { log.Info("notification: still not sent after 6 s; not waiting"); }
        }
        return 1;
    }

    private static SakshiConfig? Load(SakshiPaths paths, HostOs os, string home, TextWriter stderr) =>
        Load(paths, os, home, stderr.WriteLine);

    /// <summary>Loads the config; warnings and the error (null result) go to <paramref name="say"/>.</summary>
    private static SakshiConfig? Load(SakshiPaths paths, HostOs os, string home, Action<string> say)
    {
        try { return ConfigStore.Load(paths.ConfigFile, os, home, say); }
        catch (InvalidDataException e)
        {
            say($"Error: {e.Message}");
            return null;
        }
    }

    private static int DryRun(string command, SakshiConfig cfg, SakshiPaths paths, TextWriter stderr, TextWriter stdout)
    {
        var log = new FileLog(paths.LogFile);
        var store = new MeetingStore(cfg.MeetingsDir, cfg.EffectiveArchiveDir, log);
        try
        {
            IReadOnlyList<string> lines;
            if (command == "housekeep")
            {
                var state = new StateStore(paths.StateFile, log, Notifier(stderr, log)).Load();
                lines = store.Housekeep(cfg.KeepDays, DateOnly.FromDateTime(DateTime.Now), state.Jobs.Select(j => j.Dir).ToList(), dryRun: true);
            }
            else
                lines = store.Migrate(dryRun: true);
            foreach (var l in lines) stdout.WriteLine(l);
            return 0;
        }
        catch (DirectoryNotFoundException e)
        {
            stderr.WriteLine($"Error: {e.Message}");
            return 1;
        }
    }

    private static int ImportConfig(SakshiPaths paths, HostOs os, string home, TextWriter stdout, TextWriter stderr)
    {
        var bash = Environment.GetEnvironmentVariable("SAKSHI_CONFIG") is { Length: > 0 } c
            ? c : Path.Combine(home, ".config", "sakshi", "config");
        if (!File.Exists(bash))
        {
            stderr.WriteLine($"Error: no bash config at {bash}");
            return 1;
        }
        var cfg = BashConfigImport.Import(File.ReadAllText(bash), os, home, File.Exists, w => stderr.WriteLine(w));
        if (File.Exists(paths.ConfigFile))
            stdout.WriteLine($"Kept the existing config as {BackUp(paths.ConfigFile)}");
        ConfigStore.Save(paths.ConfigFile, cfg);
        stdout.WriteLine($"Imported {bash} into {paths.ConfigFile}");
        return 0;
    }

    /// <summary>
    /// Copies the config to config.json.bak, or, when that exists, to config.json.bak-yyyyMMddHHmmss
    /// (-2, -3 … within one second): an earlier backup is never overwritten.
    /// </summary>
    private static string BackUp(string config)
    {
        var bak = config + ".bak";
        var stamp = DateTime.Now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        for (var n = 0; ; n++)
        {
            try
            {
                File.Copy(config, bak, overwrite: false);
                return bak;
            }
            catch (IOException) when (File.Exists(bak))
            {
                bak = $"{config}.bak-{stamp}" + (n == 0 ? "" : $"-{n + 1}");
            }
        }
    }

    private static async Task<int> TranscribeAsync(string[] args, SakshiPaths paths, HostOs os, string home,
        TextWriter stdout, TextWriter stderr, CancellationToken ct)
    {
        if (args.Any(a => a is "-h" or "--help")) // help needs no config
            return await TranscribeCommand.RunAsync(args, new WhisperNet(), stdout, stderr, paths.ModelsDir, ct);

        // With --log (the tray's detached child) no one reads stderr: open the log first so a
        // config error, a usage error or a crash is in it as well.
        StreamWriter? log = null;
        if (TranscribeOptions.FindLogFile(args) is { } logFile
            && (log = await TranscribeCommand.OpenLogAsync(logFile, stderr)) is null)
            return 1;
        await using (log)
        {
            void Say(string line)
            {
                stderr.WriteLine(line);
                log?.WriteLine(line);
            }

            if (Load(paths, os, home, Say) is not { } cfg) return 1;
            try
            {
                return await TranscribeCommand.RunAsync(args, new WhisperNet(), stdout, stderr, paths.ModelsDir, ct,
                    configModel: cfg.Model.Length > 0 ? cfg.Model : null, log: log,
                    speakers: new SpeakerStep(paths.ModelsDir, paths.VoicesFile, Math.Max(1, Environment.ProcessorCount / 2)),
                    configSpeakers: cfg.Speakers);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e)
            {
                Say($"Error: {e}");
                return 1;
            }
        }
    }

    /// <summary>
    /// The live transcript's child process (the tray starts it; not for typing): transcribes VIDEO while OBS
    /// writes it and prints JSON lines (see <see cref="LiveTranscriber"/>) until stdin closes. Exit 4: no speech
    /// model and the download failed.
    /// </summary>
    private static async Task<int> LiveTranscribeAsync(string[] args, SakshiPaths paths, HostOs os, string home,
        TextWriter stdout, TextWriter stderr, CancellationToken ct)
    {
        if (args is not [var video])
        {
            stderr.WriteLine("usage: sakshi live-transcribe VIDEO");
            return 2;
        }
        if (Load(paths, os, home, stderr) is not { } cfg) return 1;
        void Line(string json)
        {
            stdout.WriteLine(json);
            stdout.Flush();
        }
        if (!FfmpegExecutable.Available)
        {
            stderr.WriteLine("live-transcribe: ffmpeg not found");
            Line("{\"error\":\"no-ffmpeg\"}");
            return 5;
        }
        var model = cfg.Model.Length > 0 ? cfg.Model : Path.Combine(paths.ModelsDir, TranscribeCommand.DefaultModelFile);
        if (cfg.Model.Length > 0 && !File.Exists(model))
        {
            stderr.WriteLine($"live-transcribe: the model in the config does not exist: {model}");
            Line("{\"error\":\"model-missing\"}");
            return 4;
        }
        if (!File.Exists(model))
        {
            Line("{\"status\":\"downloading-model\"}");
            if (await TranscribeCommand.DownloadModelAsync(model, ct) is { } why)
            {
                stderr.WriteLine($"live-transcribe: could not download the speech model: {why}");
                Line("{\"error\":\"no-model\"}");
                return 4;
            }
        }
        // The tray closes stdin when the recording ends.
        var stop = Task.Run(() => { while (Console.In.Read() >= 0) { } }, CancellationToken.None);
        // Half the cores, so the meeting app keeps its share.
        using var whisper = new LiveWhisperNet(model, Math.Max(1, Environment.ProcessorCount / 2));
        var live = new LiveTranscriber((from, max, c) => Ffmpeg.ReadAudioAsync(video, from, max, c), whisper,
            stdout, Task.Delay, () => DateTimeOffset.Now);
        return await live.RunAsync(stop, ct);
    }

    private static int DoctorAsync(string[] args, HostOs os, SakshiPaths paths, string home, DoctorProbes? probes,
        TextWriter stdout, TextWriter stderr)
    {
        if (args.Length > 0)
        {
            stderr.WriteLine("usage: sakshi doctor");
            return 2;
        }
        // A broken config must not stop the check: the defaults tell the same story for everything but the model.
        var config = Load(paths, os, home, stderr) ?? SakshiConfig.Defaults(os, home);
        var checks = Doctor.Run(os, config, paths, probes ?? DoctorProbes.Production(os, new ProcessRunner(), FfmpegExecutable.Bundled()));
        foreach (var c in checks)
        {
            stdout.WriteLine($"{(c.Ok ? "✓" : "✗")} {c.Name}: {c.Detail}");
            if (!c.Ok && c.Fix is not null) stdout.WriteLine($"  fix: {c.Fix}");
        }
        return checks.All(c => c.Ok) ? 0 : 1;
    }

    private static async Task<int> ObsAsync(string[] args, string home, HostOs os, SakshiPaths paths,
        TextWriter stdout, TextWriter stderr, CancellationToken ct)
    {
        if (args.Length is 0 or > 2)
        {
            stderr.WriteLine("usage: sakshi obs REQUEST [JSON]");
            return 2;
        }
        if (Load(paths, os, home, stderr) is not { } cfg) return 1;
        object? data = null;
        if (args.Length == 2)
        {
            try { data = JsonDocument.Parse(args[1]).RootElement.Clone(); }
            catch (JsonException e)
            {
                stderr.WriteLine($"Error: the request data is not valid JSON: {e.Message}");
                return 2;
            }
        }
        var password = cfg.ObsWsPassword.Length > 0 ? cfg.ObsWsPassword : ObsPassword.Read(paths.ObsConfigDir);
        try
        {
            await using var obs = await ObsClient.ConnectAsync(cfg.ObsWsHost, cfg.ObsWsPort, password, ct);
            var response = await obs.RequestAsync(args[0], data, ct);
            stdout.WriteLine(JsonSerializer.Serialize(response, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception e) when (e is InvalidOperationException or TimeoutException or ObsRequestException)
        {
            stderr.WriteLine($"Error: {e.Message}");
            return 1;
        }
    }
}
