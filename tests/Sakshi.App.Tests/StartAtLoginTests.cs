using Avalonia.Controls;
using Sakshi.App.Install;
using Sakshi.App.Tests.Prompts;
using Sakshi.App.Tray;
using Sakshi.Core.Ports;
using Sakshi.Platform.Autostart;

namespace Sakshi.App.Tests;

public sealed class StartAtLoginTests : IDisposable
{
    private readonly ShortDir _short = new();

    public void Dispose() => _short.Dispose();

    private sealed class FakeAutostart : IAutostart
    {
        public bool IsEnabled { get; set; }
        public string? Executable { get; private set; }
        public Exception? Failure { get; set; }
        public List<string> Calls { get; } = [];
        public void Enable(string executable)
        {
            Calls.Add("enable");
            if (Failure is { } f) throw f;
            IsEnabled = true; Executable = executable;
        }
        public void Disable()
        {
            Calls.Add("disable");
            if (Failure is { } f) throw f;
            IsEnabled = false;
        }
    }

    private (TrayFixture F, TrayHost Host, TrayInstance Instance) Host(FakeAutostart autostart)
    {
        var f = new TrayFixture();
        var controller = f.Controller();
        var instance = TrayInstance.TryAcquire(Path.GetDirectoryName(f.StateFile)!, _ => null, _short.Root)!;
        var services = controller.Services;
        services = new TrayServices(services.Paths, services.Os, services.Home, services.Config, services.Store,
            services.Recorder, services.Starter, services.Queue, services.Prompter, services.Notifier, services.Log, services.Clock,
            services.ProcessRunner)
        { Autostart = autostart, TrayExecutable = "/apps/Sakshi.AppImage" };
        return (f, new TrayHost(services, controller, instance), instance);
    }

    [Fact]
    public Task The_menu_item_is_a_checkbox_checked_from_the_current_setting() => HeadlessHost.Run(async () =>
    {
        var on = Host(new FakeAutostart { IsEnabled = true });
        var off = Host(new FakeAutostart { IsEnabled = false });
        await using var _ = on.F;
        await using var __ = off.F;
        await using var ___ = on.Instance;
        await using var ____ = off.Instance;

        var checkedItem = TrayApp.StartAtLoginItem(on.Host)!;
        var uncheckedItem = TrayApp.StartAtLoginItem(off.Host)!;

        Assert.Equal("Start at login", checkedItem.Header);
        Assert.Equal(MenuItemToggleType.CheckBox, checkedItem.ToggleType);
        Assert.True(checkedItem.IsChecked);
        Assert.False(uncheckedItem.IsChecked);
    });

    [Fact]
    public async Task Without_autostart_there_is_no_item()
    {
        var f = new TrayFixture();
        await using var _ = f;
        var controller = f.Controller();
        var instance = TrayInstance.TryAcquire(Path.GetDirectoryName(f.StateFile)!, _ => null, _short.Root)!;
        await using var __ = instance;
        await HeadlessHost.Run(() =>
        {
            Assert.Null(TrayApp.StartAtLoginItem(new TrayHost(controller.Services, controller, instance)));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Toggling_calls_disable_when_on_and_enable_with_the_tray_exe_when_off()
    {
        var autostart = new FakeAutostart { IsEnabled = true };
        var (f, host, instance) = Host(autostart);
        await using var _ = f;
        await using var __ = instance;

        await host.ToggleStartAtLogin();
        Assert.False(host.StartAtLoginEnabled);
        await host.ToggleStartAtLogin();
        Assert.True(host.StartAtLoginEnabled);

        Assert.Equal(["disable", "enable"], autostart.Calls);
        Assert.Equal("/apps/Sakshi.AppImage", autostart.Executable);
    }

    [Fact]
    public async Task A_failure_is_logged_and_notified_and_the_setting_is_unchanged()
    {
        var autostart = new FakeAutostart { IsEnabled = true, Failure = new IOException("denied") };
        var (f, host, instance) = Host(autostart);
        await using var _ = f;
        await using var __ = instance;

        await host.ToggleStartAtLogin();

        Assert.True(host.StartAtLoginEnabled);
        Assert.Contains(f.Notifier.Sent, n => n.Body == "Could not change Start at login: denied");
        Assert.Contains(f.Log.Lines, l => l.Contains("denied"));
    }

    [Fact]
    public async Task Starting_the_tray_never_changes_the_setting()
    {
        var autostart = new FakeAutostart { IsEnabled = false };   // the user turned it off
        var (f, host, instance) = Host(autostart);
        await using var _ = f;
        await using var __ = instance;
        try
        {
            host.Start();
            await HeadlessHost.Run(() =>
            {
                TrayApp.StartAtLoginItem(host);
                return Task.CompletedTask;
            });
            Assert.Empty(autostart.Calls);
            Assert.False(autostart.IsEnabled);
        }
        finally { await host.StopAsync(TimeSpan.FromSeconds(10)); }
    }

    [Fact]
    public void Resolve_without_a_started_Velopack_falls_back_to_this_process() =>
        Assert.False(string.IsNullOrEmpty(TrayExecutable.Resolve()));

    [Theory]
    [InlineData(true, false, null, @"C:\x\sakshiw.exe", @"C:\x\sakshiw.exe")]
    [InlineData(false, true, "/apps/S.AppImage", "/tmp/.mount_x/sakshi", "/apps/S.AppImage")]
    [InlineData(false, true, null, "/usr/local/bin/sakshi", "/usr/local/bin/sakshi")]
    [InlineData(false, true, "", "/usr/local/bin/sakshi", "/usr/local/bin/sakshi")]
    [InlineData(false, false, "/apps/S.AppImage", "/Applications/Sakshi.app/Contents/MacOS/sakshi", "/Applications/Sakshi.app/Contents/MacOS/sakshi")]
    public void Tray_executable_follows_the_platform(bool windows, bool linux, string? appImage, string process, string expected) =>
        Assert.Equal(expected, TrayExecutable.Resolve(windows, n => n == "APPIMAGE" ? appImage : null, null, process, linux));

    [Fact]
    public void Windows_installed_uses_the_root_stub() =>
        Assert.Equal(Path.Combine(@"C:\L\sakshi", "Sakshi.exe"),
            TrayExecutable.Resolve(true, _ => null, @"C:\L\sakshi", @"C:\L\sakshi\current\sakshiw.exe"));

    [Fact]
    public async Task Clicking_the_item_disables_and_unchecks_it()
    {
        var autostart = new FakeAutostart { IsEnabled = true };
        var (f, host, instance) = Host(autostart);
        await using var _ = f;
        await using var __ = instance;
        await HeadlessHost.Run(async () =>
        {
            var item = TrayApp.StartAtLoginItem(host)!;
            Assert.True(item.IsChecked);
            await TrayApp.OnStartAtLoginClicked(host, item);
            Assert.Equal(["disable"], autostart.Calls);
            Assert.False(item.IsChecked);
        });
        Assert.True(new AutostartChoice(host.Services.Paths.StateDir).Made);   // either direction records the choice
    }

    [Fact]
    public async Task A_failing_click_leaves_the_item_checked_and_notifies()
    {
        var autostart = new FakeAutostart { IsEnabled = true, Failure = new IOException("denied") };
        var (f, host, instance) = Host(autostart);
        await using var _ = f;
        await using var __ = instance;
        await HeadlessHost.Run(async () =>
        {
            var item = TrayApp.StartAtLoginItem(host)!;
            await TrayApp.OnStartAtLoginClicked(host, item);
            Assert.True(item.IsChecked);
        });
        Assert.Contains(f.Notifier.Sent, n => n.Body == "Could not change Start at login: denied");
        Assert.False(new AutostartChoice(host.Services.Paths.StateDir).Made);
    }
}
