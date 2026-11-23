using System.Text.Json.Nodes;
using Sakshi.Core.Config;
using Sakshi.Core.Paths;
using Sakshi.Core.Ports;
using Sakshi.Platform;
using Sakshi.Platform.Linux;
using Sakshi.TestSupport;

namespace Sakshi.Platform.Tests;

public sealed class ObsLauncherTests : IDisposable
{
    private sealed class ListLog : ILog
    {
        public List<string> Lines { get; } = [];
        public void Info(string message) => Lines.Add(message);
    }

    private sealed class FixedInstall(ObsLaunch launch) : IObsInstall
    {
        public string ProcessName => "fixed";
        public Task<ObsLaunch> LaunchAsync(IReadOnlyList<string> extraArgs, CancellationToken ct) => Task.FromResult(launch);
    }

    private readonly string _root = Path.Combine(Path.GetTempPath(), "sakshi-obs-" + Guid.NewGuid().ToString("N"));
    private readonly FakeProcessRunner _runner = new();
    private readonly ListLog _log = new();

    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    private string ObsConfigFile => Path.Combine(_root, "obs", "plugin_config", "obs-websocket", "config.json");
    private SakshiPaths Paths => new(Path.Combine(_root, "c"), Path.Combine(_root, "s"), Path.Combine(_root, "d"), Path.Combine(_root, "obs"));

    private ObsLauncher Launcher(Func<string, int, CancellationToken, Task<bool>>? open = null, SakshiConfig? cfg = null, Func<TimeSpan, CancellationToken, Task>? delay = null, IObsInstall? install = null) =>
        new(cfg ?? new SakshiConfig(), Paths, install ?? new LinuxObsInstall(_runner), _runner, _log, open ?? ((_, _, _) => Task.FromResult(true)), delay ?? ((_, _) => Task.CompletedTask));

    private void WriteConfig(string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ObsConfigFile)!);
        File.WriteAllText(ObsConfigFile, json);
    }

    [Fact]
    public async Task Launcher_starts_the_install_command_in_its_working_directory()
    {
        var install = new FixedInstall(new ObsLaunch("obs64.exe", ["--x"], "/some/dir"));
        await Launcher(install: install).EnsureRunningAsync(TestContext.Current.CancellationToken);
        var d = Assert.Single(_runner.Detached);
        Assert.Equal(("obs64.exe", "/some/dir"), (d.Item1, d.Item3));
        Assert.Equal(["--x"], d.Item2);
    }

    [Fact]
    public async Task Enables_server_when_obs_not_running_and_keeps_unknown_keys()
    {
        WriteConfig("""{"server_enabled":false,"server_port":1,"alerts_enabled":true,"extra":{"a":[1,2]}}""");
        _runner.OnPath["obs"] = "/usr/bin/obs";
        await Launcher(cfg: new SakshiConfig { ObsWsPort = 4466 }).EnsureRunningAsync(TestContext.Current.CancellationToken);
        var n = JsonNode.Parse(File.ReadAllText(ObsConfigFile))!;
        Assert.True((bool)n["server_enabled"]!);
        Assert.Equal(4466, (int)n["server_port"]!);
        Assert.True((bool)n["alerts_enabled"]!);
        Assert.Equal(2, n["extra"]!["a"]!.AsArray()[1]!.GetValue<int>());
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(ObsConfigFile)!, "*.tmp"));
    }

    [Fact]
    public async Task Creates_config_when_missing()
    {
        _runner.OnPath["obs"] = "/usr/bin/obs";
        await Launcher().EnsureRunningAsync(TestContext.Current.CancellationToken);
        var n = JsonNode.Parse(File.ReadAllText(ObsConfigFile))!;
        Assert.True((bool)n["server_enabled"]!);
        Assert.Equal(4455, (int)n["server_port"]!);
        Assert.True((bool)n["auth_required"]!);
        Assert.Equal(20, ((string)n["server_password"]!).Length);
        Assert.Equal((string)n["server_password"]!, Launcher().Password());   // what Sakshi connects with
    }

    [Fact]
    public async Task Turns_the_password_on_when_obs_asks_for_none()
    {
        WriteConfig("""{"server_enabled":true,"auth_required":false,"server_password":""}""");
        _runner.OnPath["obs"] = "/usr/bin/obs";
        await Launcher().EnsureRunningAsync(TestContext.Current.CancellationToken);
        var n = JsonNode.Parse(File.ReadAllText(ObsConfigFile))!;
        Assert.True((bool)n["auth_required"]!);
        Assert.Equal(20, ((string)n["server_password"]!).Length);
    }

    [Fact]
    public async Task Turning_the_password_on_keeps_one_already_there()
    {
        WriteConfig("""{"server_enabled":true,"auth_required":false,"server_password":"kept"}""");
        _runner.OnPath["obs"] = "/usr/bin/obs";
        await Launcher().EnsureRunningAsync(TestContext.Current.CancellationToken);
        var n = JsonNode.Parse(File.ReadAllText(ObsConfigFile))!;
        Assert.True((bool)n["auth_required"]!);
        Assert.Equal("kept", (string)n["server_password"]!);
    }

    [Fact]
    public async Task A_new_password_is_the_one_in_sakshis_config_when_set()
    {
        WriteConfig("""{"server_enabled":true,"auth_required":false}""");
        _runner.OnPath["obs"] = "/usr/bin/obs";
        await Launcher(cfg: new SakshiConfig { ObsWsPassword = "mine" }).EnsureRunningAsync(TestContext.Current.CancellationToken);
        Assert.Equal("mine", (string)JsonNode.Parse(File.ReadAllText(ObsConfigFile))!["server_password"]!);
    }

    [Fact]
    public async Task Opting_out_leaves_obs_without_a_password()
    {
        const string text = """{"server_enabled":true,"auth_required":false}""";
        WriteConfig(text);
        _runner.OnPath["obs"] = "/usr/bin/obs";
        await Launcher(cfg: new SakshiConfig { ObsWsRequirePassword = false }).EnsureRunningAsync(TestContext.Current.CancellationToken);
        Assert.Equal(text, File.ReadAllText(ObsConfigFile));
    }

    [Fact]
    public async Task A_running_obs_without_a_password_is_left_alone_until_next_start()
    {
        const string text = """{"server_enabled":true,"auth_required":false}""";
        WriteConfig(text);
        _runner.Running.Add("obs");
        await Launcher().EnsureRunningAsync(TestContext.Current.CancellationToken);
        Assert.Equal(text, File.ReadAllText(ObsConfigFile));
        Assert.Contains(_log.Lines, l => l.Contains("asks for no password"));
    }

    [Fact]
    public async Task Refuses_when_running_and_server_off()
    {
        WriteConfig("""{"server_enabled":false}""");
        _runner.Running.Add("obs");
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => Launcher().EnsureRunningAsync(TestContext.Current.CancellationToken));
        Assert.Equal("OBS is running but its WebSocket server is off. In OBS: Tools → WebSocket Server Settings → Enable, then try again.", e.Message);
        Assert.Contains("\"server_enabled\":false", File.ReadAllText(ObsConfigFile).Replace(" ", ""));
    }

    [Fact]
    public async Task Does_not_launch_when_running()
    {
        WriteConfig("""{"server_enabled":true}""");
        _runner.Running.Add("obs");
        await Launcher().EnsureRunningAsync(TestContext.Current.CancellationToken);
        Assert.Empty(_runner.Detached);
    }

    [Fact]
    public async Task Launches_via_uwsm_app()
    {
        _runner.OnPath["uwsm-app"] = "/usr/bin/uwsm-app";
        _runner.OnPath["obs"] = "/usr/bin/obs";
        await Launcher().EnsureRunningAsync(TestContext.Current.CancellationToken);
        var d = Assert.Single(_runner.Detached);
        Assert.Equal("uwsm-app", d.Item1);
        Assert.Equal(["--", "obs", "--minimize-to-tray", "--disable-shutdown-check"], d.Item2);
    }

    [Fact]
    public async Task Launches_obs_directly()
    {
        _runner.OnPath["obs"] = "/usr/bin/obs";
        await Launcher().EnsureRunningAsync(TestContext.Current.CancellationToken);
        var d = Assert.Single(_runner.Detached);
        Assert.Equal("obs", d.Item1);
        Assert.Equal(["--minimize-to-tray", "--disable-shutdown-check"], d.Item2);
    }

    [Fact]
    public async Task Launches_flatpak_when_only_that_exists()
    {
        _runner.OnPath["flatpak"] = "/usr/bin/flatpak";
        await Launcher().EnsureRunningAsync(TestContext.Current.CancellationToken);
        Assert.Contains(_runner.Calls, c => c.File == "flatpak" && c.Args.SequenceEqual(["info", "com.obsproject.Studio"]));
        var d = Assert.Single(_runner.Detached);
        Assert.Equal("flatpak", d.Item1);
        Assert.Equal(["run", "com.obsproject.Studio", "--minimize-to-tray", "--disable-shutdown-check"], d.Item2);
    }

    [Fact]
    public async Task Throws_when_no_obs_anywhere()
    {
        _runner.OnPath["flatpak"] = "/usr/bin/flatpak";
        _runner.OnRun = (_, _) => new ProcessResult(1, "", "not installed");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Launcher().EnsureRunningAsync(TestContext.Current.CancellationToken));
        Assert.Empty(_runner.Detached);
    }

    [Fact]
    public async Task Times_out_when_port_never_opens()
    {
        _runner.OnPath["obs"] = "/usr/bin/obs";
        var ticks = 0;
        var attempts = 0;
        var l = Launcher((_, _, _) => { attempts++; return Task.FromResult(false); }, delay: (_, _) => { ticks++; return Task.CompletedTask; });
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => l.EnsureRunningAsync(TestContext.Current.CancellationToken));
        Assert.Equal(ObsLauncher.NotAnswering(4455), e.Message);
        Assert.Contains("answer a question in an OBS window", e.Message);
        Assert.Equal(60, attempts);
        Assert.Equal(60, ticks);
    }

    [Fact]
    public async Task Succeeds_once_port_opens()
    {
        var n = 0;
        _runner.Running.Add("obs");
        await Launcher((_, _, _) => Task.FromResult(++n >= 3)).EnsureRunningAsync(TestContext.Current.CancellationToken);
        Assert.Equal(3, n);
    }

    [Fact]
    public void Password_prefers_config_then_obs_file()
    {
        WriteConfig("""{"server_password":"fromfile"}""");
        Assert.Equal("fromfile", Launcher().Password());
        Assert.Equal("cfg", Launcher(cfg: new SakshiConfig { ObsWsPassword = "cfg" }).Password());
    }

    [Fact]
    public async Task Malformed_config_throws_naming_file()
    {
        WriteConfig("{not json");
        _runner.OnPath["obs"] = "/usr/bin/obs";
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => Launcher().EnsureRunningAsync(TestContext.Current.CancellationToken));
        Assert.Contains(ObsConfigFile, e.Message);
        Assert.Equal("{not json", File.ReadAllText(ObsConfigFile));
    }

    [Fact]
    public async Task Non_object_root_is_not_overwritten()
    {
        WriteConfig("[1,2]");
        _runner.OnPath["obs"] = "/usr/bin/obs";
        await Assert.ThrowsAsync<InvalidOperationException>(() => Launcher().EnsureRunningAsync(TestContext.Current.CancellationToken));
        Assert.Equal("[1,2]", File.ReadAllText(ObsConfigFile));
    }

    [Fact]
    public async Task Auth_and_password_preserved()
    {
        WriteConfig("""{"server_enabled":false,"auth_required":true,"server_password":"pw"}""");
        _runner.OnPath["obs"] = "/usr/bin/obs";
        await Launcher().EnsureRunningAsync(TestContext.Current.CancellationToken);
        var n = JsonNode.Parse(File.ReadAllText(ObsConfigFile))!;
        Assert.True((bool)n["auth_required"]!);
        Assert.Equal("pw", (string)n["server_password"]!);
    }

    [Fact]
    public async Task Running_with_missing_file_is_noop()
    {
        _runner.Running.Add("obs");
        await Launcher().EnsureRunningAsync(TestContext.Current.CancellationToken);
        Assert.False(File.Exists(ObsConfigFile));
    }

    [Theory]
    [InlineData("", new string[0])]
    [InlineData("   ", new string[0])]
    [InlineData("  --a   --b  ", new[] { "--a", "--b" })]
    public async Task Launch_args_split_on_whitespace(string args, string[] expected)
    {
        _runner.OnPath["obs"] = "/usr/bin/obs";
        await Launcher(cfg: new SakshiConfig { ObsLaunchArgs = args }).EnsureRunningAsync(TestContext.Current.CancellationToken);
        Assert.Equal(expected, Assert.Single(_runner.Detached).Item2);
    }
}
