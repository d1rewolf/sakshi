using Sakshi.Core.Config;
using Sakshi.Core.Paths;
using Sakshi.Core.Ports;
using Sakshi.Platform.Linux;
using Sakshi.Platform.MacOS;
using Sakshi.Platform.Windows;
using Sakshi.TestSupport;

namespace Sakshi.Platform.Tests;

public sealed class PlatformAdaptersTests
{
    private sealed class NullLog : ILog
    {
        public void Info(string message) { }
    }

    private static (PlatformAdapters Adapters, FakeProcessRunner Runner, string Root) Build(HostOs os, INotifier? ui = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "sakshi-adapters-" + Guid.NewGuid().ToString("N"));
        var paths = new SakshiPaths(Path.Combine(root, "c"), Path.Combine(root, "s"), Path.Combine(root, "d"), Path.Combine(root, "o"));
        var runner = new FakeProcessRunner();
        var adapters = PlatformAdapters.Create(os, new SakshiConfig(), paths, runner, new FakePrompter(), new NullLog(), ui);
        return (adapters, runner, root);
    }

    [Fact]
    public void Linux_uses_obs_pipewire_notify_send_and_gio()
    {
        var (a, _, _) = Build(HostOs.Linux);
        Assert.IsType<ObsLauncher>(a.Launcher);
        Assert.IsType<PipeWireWindowCapture>(a.Capture);
        Assert.IsType<SafeNotifier>(a.Notifier);
        Assert.IsType<GioTrash>(a.Trash);
        Assert.Equal("obs", a.ObsProcessName);
        Assert.IsType<Sakshi.Platform.Autostart.XdgAutostart>(a.Autostart);
    }

    [Fact]
    public void Only_linux_under_hyprland_can_mend_a_frozen_capture()
    {
        var hyprland = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HYPRLAND_INSTANCE_SIGNATURE"));
        var linux = Build(HostOs.Linux).Adapters.Recovery;
        if (hyprland) Assert.IsType<HyprlandCaptureRecovery>(linux);
        else Assert.Null(linux);
        Assert.Null(Build(HostOs.MacOS).Adapters.Recovery);
        Assert.Null(Build(HostOs.Windows).Adapters.Recovery);
    }

    [Fact]
    public void MacOS_uses_screen_capture_osascript_and_the_trash()
    {
        var (a, _, _) = Build(HostOs.MacOS);
        Assert.IsType<ObsLauncher>(a.Launcher);
        Assert.Equal("screen_capture", Assert.IsType<ObsWindowChooser>(a.Capture).InputKind);
        Assert.IsType<SafeNotifier>(a.Notifier);
        Assert.Equal("MacTrash", a.Trash.GetType().Name);   // by name: the type is macOS-only for the analyzer
        Assert.Equal("OBS", a.ObsProcessName);
        Assert.IsType<Sakshi.Platform.Autostart.MacLaunchAgentAutostart>(a.Autostart);
    }

    [Fact]
    public void Windows_uses_window_capture_the_ui_notifier_and_the_recycle_bin()
    {
        var ui = new FakeNotifier();
        var (a, _, _) = Build(HostOs.Windows, ui);
        Assert.IsType<ObsLauncher>(a.Launcher);
        Assert.Equal("window_capture", Assert.IsType<ObsWindowChooser>(a.Capture).InputKind);
        a.Notifier.Notify("h", "b", Urgency.Normal);
        Assert.Single(ui.Sent);
        Assert.Equal("RecycleBin", a.Trash.GetType().Name);
        Assert.Equal("obs64", a.ObsProcessName);
        Assert.IsType<Sakshi.Platform.Autostart.WindowsRunKeyAutostart>(a.Autostart);
    }

    [Theory]
    [InlineData(HostOs.MacOS, true)]
    [InlineData(HostOs.Windows, false)]
    public async Task Only_the_macOS_chooser_sets_up_desktop_audio_first(HostOs os, bool expected)
    {
        var (a, _, _) = Build(os);
        await using var server = new FakeObsServer((type, _) => type switch
        {
            "GetSceneItemList" => (true, new { sceneItems = Array.Empty<object>() }),
            "GetInputPropertiesListPropertyItems" => (true, new { propertyItems = new[] { new { itemName = "[Safari] News", itemEnabled = true, itemValue = (object)(os == HostOs.MacOS ? 4 : "News:Class:safari.exe") } } }),
            _ => (true, null),
        });
        await using var obs = await Sakshi.Core.Obs.ObsClient.ConnectAsync("127.0.0.1", server.Port, null, TestContext.Current.CancellationToken);

        await a.Capture.SelectWindowAsync(obs, "Scene", "Src", false, TestContext.Current.CancellationToken);

        Assert.Equal(expected, server.Requests.FirstOrDefault() == "GetSceneItemList");
        Assert.Equal(expected, server.Requests.Contains("CreateInput"));
    }

    [Fact]
    public void Windows_without_a_ui_notifier_still_works()
    {
        var (a, _, _) = Build(HostOs.Windows);
        a.Notifier.Notify("h", "b", Urgency.Normal);
    }

    [Theory]
    [InlineData(HostOs.Linux)]
    [InlineData(HostOs.MacOS)]
    [InlineData(HostOs.Windows)]
    public void Building_the_adapters_touches_no_file_and_starts_no_process(HostOs os)
    {
        var (_, runner, root) = Build(os);
        Assert.False(Directory.Exists(root));
        Assert.Empty(runner.Calls);
        Assert.Empty(runner.Detached);
    }
}
