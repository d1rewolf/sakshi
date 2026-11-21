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
        _prompter.Answer("[zoom.us] Zoom Meeting");
        var ok = await Select(ChooserOs.MacOS, [Item(" ", true, 0), Item("[Safari] News", true, 4), Item("[zoom.us] Zoom Meeting", true, 9)]);

        Assert.True(ok);
        Assert.Equal(["[zoom.us] Zoom Meeting", "[Safari] News"], _prompter.Rows[0].Select(r => r.Label));
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
        _prompter.Answer("[Zoom.exe]: Zoom Meeting");
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
        _prompter.Answer("[chrome.exe]: Meet");
        await Select(ChooserOs.Windows, [Item("[chrome.exe]: Meet", true, meet), Item("[chrome.exe]: Meet", true, meet)]);

        Assert.Equal(["[chrome.exe]: Meet"], _prompter.Rows[0].Select(r => r.Label));
        Assert.Equal(meet, LastSettings().GetProperty("window").GetString());
    }

    [Fact]
    public async Task Windows_distinct_values_with_the_same_name_get_numbered_and_map_back()
    {
        // Same title, different window class: OBS can tell them apart.
        _prompter.Answer("[chrome.exe]: Meet (2)");
        await Select(ChooserOs.Windows, [
            Item("[chrome.exe]: Meet", true, "Meet:Chrome_WidgetWin_1:chrome.exe"),
            Item("[chrome.exe]: Meet", true, "Meet:Chrome_RenderWidgetHostHWND:chrome.exe")]);

        Assert.Equal(["[chrome.exe]: Meet", "[chrome.exe]: Meet (2)"], _prompter.Rows[0].Select(r => r.Label));
        Assert.Equal("Meet:Chrome_RenderWidgetHostHWND:chrome.exe", LastSettings().GetProperty("window").GetString());
    }

    [Fact]
    public async Task Mac_same_titles_get_numbered_and_map_back()
    {
        _prompter.Answer("[Google Chrome] Meet (2)");
        await Select(ChooserOs.MacOS, [Item("[Google Chrome] Meet", true, 11), Item("[Google Chrome] Meet", true, 12)]);

        Assert.Equal(["[Google Chrome] Meet", "[Google Chrome] Meet (2)"], _prompter.Rows[0].Select(r => r.Label));
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
