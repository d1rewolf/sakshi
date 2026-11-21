using Sakshi.App.Install;
using Sakshi.App.Tests.Prompts;
using Sakshi.App.Tray;
using Sakshi.Platform.Autostart;

namespace Sakshi.App.Tests;

/// <summary>
/// The tray's own default: the AppImage has no installer and a silent Windows install may skip the first-run hook,
/// so the first tray start of an installed copy turns start at login on unless a choice is already recorded.
/// </summary>
public sealed class DefaultAutostartTests : IAsyncDisposable
{
    private readonly TrayFixture _f = new();
    private readonly ShortDir _short = new();
    private TrayInstance? _instance;

    public async ValueTask DisposeAsync()
    {
        if (_instance is not null) await _instance.DisposeAsync();
        await _f.DisposeAsync();
        _short.Dispose();
    }

    private sealed class FakeAutostart : IAutostart
    {
        public bool IsEnabled { get; private set; }
        public string? Executable { get; private set; }
        public Exception? Failure { get; init; }
        public List<string> Calls { get; } = [];
        public void Enable(string executable)
        {
            Calls.Add("enable");
            if (Failure is { } f) throw f;
            IsEnabled = true; Executable = executable;
        }
        public void Disable() { Calls.Add("disable"); IsEnabled = false; }
    }

    private TrayHost Host(FakeAutostart autostart, bool installed)
    {
        var controller = _f.Controller();
        var services = controller.Services with
        {
            Autostart = autostart, TrayExecutable = "/apps/Sakshi.AppImage", IsInstalledCopy = () => installed,
        };
        _instance = TrayInstance.TryAcquire(Path.GetDirectoryName(_f.StateFile)!, _ => null, _short.Root)!;
        return new TrayHost(services, controller, _instance);
    }

    private AutostartChoice Choice => new(Path.GetDirectoryName(_f.StateFile)!);

    [Fact]
    public async Task An_installed_copy_without_a_recorded_choice_turns_it_on_and_records_it()
    {
        var autostart = new FakeAutostart();
        await Host(autostart, installed: true).StartInstallUpkeep();

        Assert.Equal(["enable"], autostart.Calls);
        Assert.Equal("/apps/Sakshi.AppImage", autostart.Executable);
        Assert.True(Choice.Made);
    }

    [Fact]
    public async Task A_recorded_choice_is_left_alone()
    {
        Choice.Record();   // the user turned it off, or the installer's first run already turned it on
        var autostart = new FakeAutostart();
        await Host(autostart, installed: true).StartInstallUpkeep();

        Assert.Empty(autostart.Calls);
    }

    [Fact]
    public async Task A_copy_that_is_not_installed_is_left_alone()
    {
        var autostart = new FakeAutostart();
        await Host(autostart, installed: false).StartInstallUpkeep();

        Assert.Empty(autostart.Calls);
        Assert.False(Choice.Made);
    }

    [Fact]
    public async Task A_failing_enable_is_logged_and_records_nothing()
    {
        var autostart = new FakeAutostart { Failure = new IOException("read-only home") };
        await Host(autostart, installed: true).StartInstallUpkeep();

        Assert.Contains(_f.Log.Lines, l => l.Contains("read-only home"));
        Assert.False(Choice.Made);
    }

    [Fact]
    public Task The_menu_item_shows_the_default_once_the_upkeep_has_turned_it_on() => HeadlessHost.Run(async () =>
    {
        var host = Host(new FakeAutostart(), installed: true);
        var item = TrayApp.StartAtLoginItem(host)!;
        Assert.False(item.IsChecked);   // built before the background task has run

        await TrayApp.RefreshStartAtLoginAfter(host.StartInstallUpkeep(), host, item);

        Assert.True(item.IsChecked);
    });

    [Theory]
    [InlineData(true, null, true)]
    [InlineData(true, "/apps/S.AppImage", true)]
    public void Linux_is_installed_only_when_run_from_an_AppImage(bool linux, string? appImage, bool velopack) =>
        Assert.Equal(appImage is not null,
            InstalledCopy.Detect(linux, n => n == "APPIMAGE" ? appImage : null, () => velopack));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Elsewhere_Velopack_decides(bool velopack) =>
        Assert.Equal(velopack, InstalledCopy.Detect(false, _ => "/apps/S.AppImage", () => velopack));

    [Fact]
    public void Without_a_started_Velopack_this_copy_is_not_installed() =>
        Assert.False(InstalledCopy.Detect(false, _ => null, InstalledCopy.VelopackInstalled));
}
