using System.Text.Json;
using Sakshi.Core.Config;
using Sakshi.Core.Obs;
using Sakshi.Core.Ports;
using Sakshi.TestSupport;

namespace Sakshi.Platform.Tests;

public sealed class ObsWindowChooserTests : IDisposable
{
    private readonly List<FakeObsServer> _servers = [];
    private readonly List<JsonElement> _setSettings = [];
    private readonly FakePrompter _prompter = new();
    private object[] _items = [];

    private sealed class NoLog : ILog { public void Info(string m) { } }

    public void Dispose()
    {
        foreach (var s in _servers) s.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private (bool, object?) Handle(string type, JsonElement data)
    {
        if (type == "SetInputSettings") lock (_setSettings) _setSettings.Add(data.Clone());
        return type == "GetInputPropertiesListPropertyItems" ? (true, new { propertyItems = _items }) : (true, null);
    }

    private async Task<bool> Select(ChooserOs os, object[] items)
    {
        _items = items;
        var server = new FakeObsServer(Handle);
        _servers.Add(server);
        await using var obs = await ObsClient.ConnectAsync("127.0.0.1", server.Port, null, TestContext.Current.CancellationToken);
        return await new ObsWindowChooser(os, new SakshiConfig(), _prompter, new NoLog())
            .SelectWindowAsync(obs, "Scene", "Src", false, TestContext.Current.CancellationToken);
    }

    private static object Item(string name, bool enabled, object value) => new { itemName = name, itemEnabled = enabled, itemValue = value };

    private JsonElement LastSettings() => _setSettings[^1].GetProperty("inputSettings");

    [Fact]
    public async Task Mac_lists_windows_meeting_first_and_sets_the_chosen_id()
    {
        _prompter.Answer("Zoom Meeting");
        var ok = await Select(ChooserOs.MacOS, [Item(" ", true, 0), Item("[Safari] News", true, 4), Item("[zoom.us] Zoom Meeting", true, 9)]);

        Assert.True(ok);
        Assert.Equal(["Zoom Meeting", "News"], _prompter.Rows[0].Select(r => r.Label));
        Assert.Equal(["zoom.us", "Safari"], _prompter.Rows[0].Select(r => r.Description));
        Assert.Equal("Pick the window to record", _prompter.Shown[0]);
        Assert.Equal(TimeSpan.FromMinutes(3), _prompter.Timeouts[0]);
        Assert.Equal(JsonValueKind.Number, LastSettings().GetProperty("window").ValueKind);
        Assert.Equal(9, LastSettings().GetProperty("window").GetInt32());
        Assert.Equal(1, LastSettings().GetProperty("type").GetInt32());
        Assert.True(_setSettings[^1].GetProperty("overlay").GetBoolean());
    }

    [Fact]
    public async Task Windows_sets_the_string_value_and_method_2()
    {
        const string value = "Zoom Meeting:ZPContentViewWndClass:Zoom.exe";
        _prompter.Answer("Zoom Meeting");
        var ok = await Select(ChooserOs.Windows, [Item("[Select a window]", false, ""), Item("[Zoom.exe]: Zoom Meeting", true, value)]);

        Assert.True(ok);
        Assert.Equal(value, LastSettings().GetProperty("window").GetString());
        Assert.Equal(2, LastSettings().GetProperty("method").GetInt32());
    }

    [Fact]
    public async Task Windows_identical_values_are_one_window()
    {
        // OBS's value is title:class:exe, so two same-titled windows of one app can't be told apart.
        const string meet = "Meet:Chrome_WidgetWin_1:chrome.exe";
        _prompter.Answer("Meet");
        await Select(ChooserOs.Windows, [Item("[chrome.exe]: Meet", true, meet), Item("[chrome.exe]: Meet", true, meet)]);

        Assert.Equal(["Meet"], _prompter.Rows[0].Select(r => r.Label));
        Assert.Equal(["chrome.exe"], _prompter.Rows[0].Select(r => r.Description));
        Assert.Equal(meet, LastSettings().GetProperty("window").GetString());
    }

    [Fact]
    public async Task Windows_distinct_values_with_the_same_name_get_numbered_and_map_back()
    {
        // Same title, different window class: OBS can tell them apart.
        _prompter.Answer("Meet (2)");
        await Select(ChooserOs.Windows, [
            Item("[chrome.exe]: Meet", true, "Meet:Chrome_WidgetWin_1:chrome.exe"),
            Item("[chrome.exe]: Meet", true, "Meet:Chrome_RenderWidgetHostHWND:chrome.exe")]);

        Assert.Equal(["Meet", "Meet (2)"], _prompter.Rows[0].Select(r => r.Label));
        Assert.Equal("Meet:Chrome_RenderWidgetHostHWND:chrome.exe", LastSettings().GetProperty("window").GetString());
    }

    [Fact]
    public async Task Mac_same_titles_get_numbered_and_map_back()
    {
        _prompter.Answer("Meet (2)");
        await Select(ChooserOs.MacOS, [Item("[Google Chrome] Meet", true, 11), Item("[Google Chrome] Meet", true, 12)]);

        Assert.Equal(["Meet", "Meet (2)"], _prompter.Rows[0].Select(r => r.Label));
        Assert.Equal(12, LastSettings().GetProperty("window").GetInt32());
    }

    [Fact]
    public async Task A_number_never_collides_with_a_real_title()
    {
        _prompter.Answer("Meet (3)");
        await Select(ChooserOs.MacOS, [Item("Meet", true, 1), Item("Meet", true, 2), Item("Meet (2)", true, 3)]);

        var labels = _prompter.Rows[0].Select(r => r.Label).ToArray();
        Assert.Equal(["Meet", "Meet (3)", "Meet (2)"], labels);
        Assert.Equal(2, LastSettings().GetProperty("window").GetInt32());
    }

    [Fact]
    public async Task The_same_title_in_two_apps_is_told_apart_by_the_app_line()
    {
        _prompter.Answer("Standup (2)");
        await Select(ChooserOs.Windows, [
            Item("[Teams.exe]: Standup", true, "Standup:TeamsWebView:Teams.exe"),
            Item("[chrome.exe]: Standup", true, "Standup:Chrome_WidgetWin_1:chrome.exe"),
            Item("[Untitled.exe]: ", true, "x:y:Untitled.exe")]);

        Assert.Equal(["Standup", "Standup (2)", "Untitled.exe"], _prompter.Rows[0].Select(r => r.Label));
        Assert.Equal(["Teams.exe", "chrome.exe", "Untitled.exe"], _prompter.Rows[0].Select(r => r.Description));
        Assert.Equal("Standup:Chrome_WidgetWin_1:chrome.exe", LastSettings().GetProperty("window").GetString());
    }

    [Fact]
    public async Task Sakshis_own_windows_are_not_offered()
    {
        _prompter.Answer("Standup");
        await Select(ChooserOs.Windows, [
            Item("[sakshiw.exe]: Live transcript", true, "Live transcript:Avalonia-x:sakshiw.exe"),
            Item("[Teams.exe]: Standup", true, "Standup:TeamsWebView:Teams.exe")]);

        Assert.Equal(["Standup"], _prompter.Rows[0].Select(r => r.Label));
    }

    [Fact]
    public async Task Mac_leaves_out_menu_bar_icons_and_the_systems_own_surfaces()
    {
        _prompter.Answer("smoke.txt");
        await Select(ChooserOs.MacOS, [
            Item("[Sakshi] Item-0", true, 1),
            Item("[Control Center] Battery", true, 2),
            Item("[Dock] Dock", true, 3),
            Item("[Notification Center] Notification Center", true, 4),
            Item("[OBS Studio] Item-0", true, 5),
            Item("[Spotlight] Item-0", true, 6),
            Item("[SystemUIServer] Siri", true, 7),
            Item("[TextEdit] smoke.txt", true, 8),
            Item("[Sakshi] Live transcript", true, 9)]);

        Assert.Equal(["smoke.txt"], _prompter.Rows[0].Select(r => r.Label));
    }

    [Fact]
    public async Task Windows_keeps_a_window_whose_title_looks_like_a_mac_menu_bar_item()
    {
        _prompter.Answer("Item-0");
        await Select(ChooserOs.Windows, [Item("[notepad.exe]: Item-0", true, "Item-0:Notepad:notepad.exe")]);

        Assert.Equal(["Item-0"], _prompter.Rows[0].Select(r => r.Label));
    }

    [Fact]
    public async Task Dismissed_returns_false_and_sets_no_window()
    {
        var ok = await Select(ChooserOs.MacOS, [Item("[Safari] News", true, 4)]);

        Assert.False(ok);
        Assert.DoesNotContain(_setSettings, s => s.GetProperty("inputSettings").TryGetProperty("window", out _));
    }

    [Fact]
    public async Task Mac_with_only_the_placeholder_explains_screen_recording()
    {
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => Select(ChooserOs.MacOS, [Item(" ", true, 0)]));
        Assert.Equal("OBS can't see any windows. Give OBS screen recording permission (System Settings → Privacy & Security → Screen Recording on macOS 13 and 14, Screen & System Audio Recording on macOS 15), restart OBS, and try again.", e.Message);
    }

    [Fact]
    public async Task Windows_with_no_windows_explains_minimised()
    {
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => Select(ChooserOs.Windows, [Item("[Select a window]", false, "")]));
        Assert.Equal("OBS found no windows to record. Minimised windows aren't listed: restore the meeting window and try again.", e.Message);
    }

    [Fact]
    public async Task Prepare_runs_first_and_kinds_and_settings_match_the_os()
    {
        var ran = false;
        _items = [Item("[Safari] News", true, 4)];
        var server = new FakeObsServer(Handle);
        _servers.Add(server);
        await using var obs = await ObsClient.ConnectAsync("127.0.0.1", server.Port, null, TestContext.Current.CancellationToken);
        var mac = new ObsWindowChooser(ChooserOs.MacOS, new SakshiConfig(), _prompter, new NoLog(), (_, _, _, _) => { ran = _setSettings.Count == 0; return Task.CompletedTask; });
        await mac.SelectWindowAsync(obs, "Scene", "Src", true, TestContext.Current.CancellationToken);

        Assert.True(ran);
        Assert.Equal("screen_capture", mac.InputKind);
        Assert.Equal("window_capture", new ObsWindowChooser(ChooserOs.Windows, new SakshiConfig(), _prompter, new NoLog()).InputKind);
    }
}
