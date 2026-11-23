using System.ComponentModel;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Sakshi.App.Prompts;

namespace Sakshi.App.Tray;

/// <summary>
/// The tray-only application: no main window, a TrayIcon with a menu, a 1 s timer that refreshes it.
/// Built in code (no XAML) on top of the prompt application's Fluent dark theme.
/// </summary>
public sealed class TrayApp : PromptApplication
{
    internal static TrayHost? Host { get; set; }

    /// <summary>Cancelled by Ctrl-C; registered once Avalonia (and its dispatcher) is up.</summary>
    internal static CancellationToken Interrupt { get; set; }
    private static TrayApp? _current;

    private DispatcherTimer? _timer;
    private TrayIcon? _icon;
    private NativeMenuItem? _statusLine, _toggle, _repick, _liveItem;
    private IClassicDesktopStyleApplicationLifetime? _lifetime;
    private int _ticks;
    private int _quitting;
    private readonly Dictionary<TrayIconState, WindowIcon> _icons = [];
    private TrayIconState? _shownIcon;
    private string? _shownTip, _shownStatus, _shownLabel;
    private IDisposable? _ctrlC, _sigterm;
    private SetupWindow? _setup;
    private LiveTranscriptWindow? _liveWindow;
    private WindowsHotkey? _hotkey;

    public override void OnFrameworkInitializationCompleted()
    {
        _current = this;
        _lifetime = ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
        if (_lifetime is not null) _lifetime.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        if (Host is { } host)
        {
            // Only now is the dispatcher up on this (the main) thread: start everything that can post to it.
            host.Start();
            var upkeep = host.StartInstallUpkeep();
            _ctrlC = Interrupt.Register(RequestQuit);
            if (!OperatingSystem.IsWindows())
                _sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx => { ctx.Cancel = true; RequestQuit(); });
            Build(host, upkeep);
            if (OperatingSystem.IsWindows()) HoldShortcut(host);
            _ = ShowSetupIfWantedAsync(host);
        }
        base.OnFrameworkInitializationCompleted();
    }

    private void Build(TrayHost host, Task upkeep)
    {
        _statusLine = new NativeMenuItem(TrayView.Name) { IsEnabled = false };
        _toggle = new NativeMenuItem("Start recording");
        _toggle.Click += (_, _) => host.Toggle();
        _repick = RepickItem(host);
        _liveItem = new NativeMenuItem("Live transcript") { IsEnabled = false };
        _liveItem.Click += (_, _) => _ = ShowLiveTranscriptAsync(host);
        var meetings = new NativeMenuItem("Open meetings folder");
        meetings.Click += (_, _) => host.OpenMeetings();
        var config = new NativeMenuItem("Open config");
        config.Click += (_, _) => host.OpenConfig();
        var setup = new NativeMenuItem("Setup…");
        setup.Click += (_, _) => _ = ShowSetupAsync(host);
        var startAtLogin = StartAtLoginItem(host);
        if (startAtLogin is not null) _ = RefreshStartAtLoginAfter(upkeep, host, startAtLogin);
        var quit = new NativeMenuItem("Quit");
        quit.Click += (_, _) => Quit();

        var menu = new NativeMenu();
        menu.Add(_statusLine);
        menu.Add(_toggle);
        menu.Add(_liveItem);
        if (_repick is not null) menu.Add(_repick);
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(meetings);
        menu.Add(config);
        menu.Add(setup);
        if (startAtLogin is not null) menu.Add(startAtLogin);
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(quit);

        _icon = new TrayIcon { Menu = menu, IsVisible = true };
        if (ClickToggles) _icon.Clicked += (_, _) => host.Toggle();
        TrayIcon.SetIcons(this, new TrayIcons { _icon });

        Refresh(host);
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) =>
        {
            try { _ticks++; Refresh(host); }
            catch (Exception e) { host.Log($"tray: refresh failed: {e.Message}"); }
        };
        _timer.Start();
    }

    /// <summary>
    /// A click on the icon starts or stops a recording only on Linux, where bars such as Omarchy's send a left click
    /// to the app. On Windows a left click is easy to make by accident, and on macOS a click opens the menu.
    /// </summary>
    internal static bool ClickToggles => OperatingSystem.IsLinux();

    /// <summary>
    /// Takes the shortcut from the config (Windows only: elsewhere the desktop binds keys to <c>sakshi toggle</c>).
    /// A shortcut that cannot be used is told in a notification; the tray runs on without it.
    /// </summary>
    private void HoldShortcut(TrayHost host)
    {
        var setting = host.Services.Config.Shortcut;
        if (Hotkey.IsOff(setting)) return;
        if (Hotkey.Parse(setting) is not { } key)
        {
            host.Log($"shortcut: cannot read \"{setting}\"");
            host.Services.Notifier.Notify("Sakshi", ShortcutUnreadable(setting), Core.Ports.Urgency.Normal);
            return;
        }
        try
        {
            _hotkey = WindowsHotkey.Register(key, host.Toggle);
            host.Log($"shortcut: {key.Text} starts or stops a recording");
        }
        catch (Win32Exception e)
        {
            host.Log($"shortcut: could not register {key.Text}: {e.Message}");
            host.Services.Notifier.Notify("Sakshi", ShortcutTaken(key), Core.Ports.Urgency.Normal);
        }
    }

    internal static string ShortcutUnreadable(string setting) =>
        $"The shortcut \"{setting}\" in config.json is not one Sakshi understands, so there is no shortcut for now. " +
        "Use a form like Ctrl+Alt+R (Ctrl, Alt or Win, plus a letter, a digit or F1–F24), then restart Sakshi.";

    internal static string ShortcutTaken(Hotkey key) =>
        $"{key.Text} is already used by another program, so it won't start or stop recording. " +
        "Choose another shortcut in config.json (\"shortcut\"), then restart Sakshi.";

    /// <summary>Opens the setup window when the start check asks for it (something missing, or an installed copy's first start).</summary>
    private async Task ShowSetupIfWantedAsync(TrayHost host)
    {
        try
        {
            if (await host.SetupAtStart.ConfigureAwait(false) is { } checks)
                await Dispatcher.UIThread.InvokeAsync(() => OpenSetup(host, checks));
        }
        catch (Exception e) { host.Log($"setup: could not open the window: {e.Message}"); }
    }

    /// <summary>The "Setup…" item: runs the checks off the UI thread, then opens (or brings forward) the window.</summary>
    private async Task ShowSetupAsync(TrayHost host)
    {
        try
        {
            if (_setup is { } open)
            {
                open.Activate();
                return;
            }
            var checks = await Task.Run(host.RunDoctor).ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() => OpenSetup(host, checks));
        }
        catch (Exception e) { host.Log($"setup: could not open the window: {e.Message}"); }
    }

    /// <summary>
    /// The "Live transcript" item: starts (or finds) the recording's live transcript off the UI thread, then opens its
    /// window, or brings an open one forward. What stops it is told in a notification.
    /// </summary>
    private async Task ShowLiveTranscriptAsync(TrayHost host)
    {
        try
        {
            var live = await Task.Run(host.LiveTranscriptAsync).ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_liveWindow is { } open)
                {
                    if (open.Transcript == live)
                    {
                        open.Activate();
                        return;
                    }
                    open.Close(); // an earlier recording's: this recording gets its own window
                }
                var window = new LiveTranscriptWindow(live, host.Log);
                window.Closed += (_, _) => { if (_liveWindow == window) _liveWindow = null; };
                _liveWindow = window;
                window.Show();
                window.Activate();
            });
        }
        catch (Exception e)
        {
            host.Log($"live transcript: could not open: {e}");
            host.Services.Notifier.Notify("Live transcript", e is InvalidOperationException
                ? e.Message
                : $"The live transcript could not start: {e.Message}. The full transcript will still be made after the meeting.",
                Core.Ports.Urgency.Normal);
        }
    }

    /// <summary>On the UI thread. One window at a time: a second request brings the open one forward.</summary>
    private void OpenSetup(TrayHost host, IReadOnlyList<Sakshi.Platform.Doctor.DoctorCheck> checks)
    {
        if (_setup is { } open)
        {
            open.Activate();
            return;
        }
        var window = new SetupWindow(checks, () => Task.Run(host.RunDoctor), host.OpenUrl, host.Log);
        window.Closed += (_, _) => _setup = null;
        _setup = window;
        window.Show();
        window.Activate();
        _ = host.MarkSetupSeen();
    }

    /// <summary>The "Re-pick window" item, enabled while recording; null where a capture cannot be mended.</summary>
    internal static NativeMenuItem? RepickItem(TrayHost host)
    {
        if (host.Services.CaptureRecovery is null) return null;
        var item = new NativeMenuItem("Re-pick window") { IsEnabled = false };
        item.Click += (_, _) => host.Repick();
        return item;
    }

    /// <summary>The checkable "Start at login" item, checked from the current setting; null when there is no autostart.</summary>
    internal static NativeMenuItem? StartAtLoginItem(TrayHost host)
    {
        if (host.Services.Autostart is null) return null;
        var item = new NativeMenuItem("Start at login")
        {
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = host.StartAtLoginEnabled,
        };
        item.Click += (_, _) => _ = OnStartAtLoginClicked(host, item);
        return item;
    }

    /// <summary>
    /// The item is built before the upkeep may turn the default on; without this it would show unchecked, and a
    /// click would then turn it off and record that as the user's choice.
    /// </summary>
    internal static async Task RefreshStartAtLoginAfter(Task upkeep, TrayHost host, NativeMenuItem item)
    {
        try { await upkeep.ConfigureAwait(false); }
        catch (Exception e) { host.Log($"tray: install upkeep failed: {e.Message}"); }   // it logs its own failures; never fatal
        await Dispatcher.UIThread.InvokeAsync(() => item.IsChecked = host.StartAtLoginEnabled);
    }

    /// <summary>Toggles off the UI thread, then shows the real setting (the await resumes on the UI thread).</summary>
    internal static async Task OnStartAtLoginClicked(TrayHost host, NativeMenuItem item)
    {
        await host.ToggleStartAtLogin();
        item.IsChecked = host.StartAtLoginEnabled;
    }

    private void Refresh(TrayHost host)
    {
        var view = TrayView.For(host.CurrentStatus(), blinkOn: _ticks % 2 == 0);
        if (_shownIcon != view.Icon)
        {
            if (!_icons.TryGetValue(view.Icon, out var icon))
            {
                using var bmp = Icons.Render(view.Icon);
                _icons[view.Icon] = icon = new WindowIcon(bmp);
            }
            _icon!.Icon = icon;
            _shownIcon = view.Icon;
        }
        if (_shownTip != view.Tooltip) _icon!.ToolTipText = _shownTip = view.Tooltip;
        if (_shownStatus != view.StatusLine) _statusLine!.Header = _shownStatus = view.StatusLine;
        if (_shownLabel != view.ToggleLabel) _toggle!.Header = _shownLabel = view.ToggleLabel;
        if (_repick is not null && _repick.IsEnabled != view.Recording) _repick.IsEnabled = view.Recording;
        if (_liveItem!.IsEnabled != view.Recording) _liveItem.IsEnabled = view.Recording;
    }

    /// <summary>Ask the running tray to quit (Ctrl-C, or the menu). Safe from any thread, idempotent.</summary>
    internal static void RequestQuit() => Dispatcher.UIThread.Post(() => _current?.Quit());

    private void Quit()
    {
        if (Interlocked.Exchange(ref _quitting, 1) == 1) return;
        // A repeated signal now takes the default action and kills the process.
        _ctrlC?.Dispose();
        _sigterm?.Dispose();
        _timer?.Stop();
        _hotkey?.Dispose();
        if (_icon is not null) _icon.IsVisible = false;
        var host = Host;
        _ = Task.Run(async () =>
        {
            try { if (host is not null) await host.StopAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); }
            catch (Exception e) { host?.Log($"quit: {e.Message}"); }
            finally { Dispatcher.UIThread.Post(() => _lifetime?.Shutdown()); }
        });
    }
}
