using System.Text.Json;
using Sakshi.Core.Obs;
using Sakshi.Core.Ports;
using Sakshi.Platform.MacOS;
using Sakshi.TestSupport;

namespace Sakshi.Platform.Tests;

public sealed class MacMeetingAudioTests : IDisposable
{
    private readonly List<(string Type, JsonElement Data)> _requests = [];
    private readonly ListLog _log = new();
    private FakeObsServer? _server;
    private object[] _items = [];
    private int _createFailsWith;
    private bool _addFails;
    private string _existingKind = "sck_audio_capture";   // the kind of an input that already has our name
    private readonly HashSet<string> _muted = [];
    private readonly Dictionary<string, int> _types = [];
    private readonly HashSet<string> _failing = [];

    private sealed class ListLog : ILog
    {
        public List<string> Lines { get; } = [];
        public void Info(string message) => Lines.Add(message);
    }

    public void Dispose() => _server?.DisposeAsync().AsTask().GetAwaiter().GetResult();

    private (bool, object?) Handle(string type, JsonElement data)
    {
        lock (_requests) _requests.Add((type, data.Clone()));
        if (_failing.Contains(type)) return (false, (600, "refused"));
        if (type == "GetSceneItemList") return (true, new { sceneItems = _items });
        var input = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("inputName", out var n) ? n.GetString()! : "";
        if (type == "GetInputMute") return (true, new { inputMuted = _muted.Contains(input) });
        if (type == "GetInputSettings")
            return (true, _types.TryGetValue(input, out var t) ? new { inputSettings = (object)new { type = t } } : new { inputSettings = (object)new { } });
        if (type == "CreateInput" && _createFailsWith != 0) return (false, (_createFailsWith, "failed"));
        if (type == "CreateSceneItem" && _addFails) return (false, (600, "no such source"));
        if (type == "CreateSceneItem") _items = [.. _items, Item(_existingKind, "Sakshi Desktop Audio")];
        return (true, null);
    }

    private async Task Run()
    {
        _server = new FakeObsServer(Handle);
        await using var obs = await ObsClient.ConnectAsync("127.0.0.1", _server.Port, null, TestContext.Current.CancellationToken);
        await MacMeetingAudio.EnsureAsync(obs, "Scene", "Src", _log, TestContext.Current.CancellationToken);
    }

    private JsonElement Sent(string type) => _requests.Single(r => r.Type == type).Data;

    private static object Item(string kind, string name = "x", bool enabled = true, int id = 1) =>
        new { sourceName = name, inputKind = kind, sceneItemEnabled = enabled, sceneItemId = id };

    private bool MutedTheWindow => _requests.Any(r => r.Type == "SetInputMute"
        && r.Data.GetProperty("inputName").GetString() == "Src" && r.Data.GetProperty("inputMuted").GetBoolean());

    private string[] Writes => _requests.Select(r => r.Type)
        .Where(t => t is "CreateInput" or "CreateSceneItem" or "SetInputMute" or "SetSceneItemEnabled").ToArray();

    [Fact]
    public async Task Creates_desktop_audio_when_the_scene_has_none()
    {
        _items = [Item("screen_capture"), new { sourceName = "Nested", inputKind = (string?)null }];
        await Run();

        Assert.Equal(["GetSceneItemList", "CreateInput", "SetInputMute"], _requests.Select(r => r.Type));
        Assert.Equal("Scene", Sent("GetSceneItemList").GetProperty("sceneName").GetString());
        var create = Sent("CreateInput");
        Assert.Equal("Scene", create.GetProperty("sceneName").GetString());
        Assert.Equal("Sakshi Desktop Audio", create.GetProperty("inputName").GetString());
        Assert.Equal("sck_audio_capture", create.GetProperty("inputKind").GetString());
        Assert.Equal(0, create.GetProperty("inputSettings").GetProperty("type").GetInt32());
        Assert.Equal("Src", Sent("SetInputMute").GetProperty("inputName").GetString());
        Assert.True(Sent("SetInputMute").GetProperty("inputMuted").GetBoolean());
    }

    [Fact]
    public async Task Keeps_audio_that_is_in_the_scene()
    {
        _items = [Item("screen_capture"), Item("sck_audio_capture")];
        await Run();

        Assert.Equal(["SetInputMute"], Writes);
        Assert.True(MutedTheWindow);
    }

    [Fact]
    public async Task A_muted_audio_item_does_not_count()
    {
        _items = [Item("screen_capture"), Item("sck_audio_capture", "Mac Audio")];
        _muted.Add("Mac Audio");
        await Run();

        Assert.Equal(["CreateInput", "SetInputMute"], Writes);
        Assert.True(MutedTheWindow);
    }

    [Fact]
    public async Task A_hidden_audio_item_does_not_count()
    {
        _items = [Item("sck_audio_capture", "Mac Audio", enabled: false)];
        await Run();

        Assert.Equal(["CreateInput", "SetInputMute"], Writes);
        Assert.True(MutedTheWindow);
    }

    [Fact]
    public async Task An_app_only_audio_item_does_not_count()
    {
        _items = [Item("sck_audio_capture", "Zoom Audio")];
        _types["Zoom Audio"] = 1;
        await Run();

        Assert.Equal(["CreateInput", "SetInputMute"], Writes);
        Assert.Equal("Sakshi Desktop Audio", Sent("CreateInput").GetProperty("inputName").GetString());
        Assert.True(MutedTheWindow);
    }

    [Fact]
    public async Task Explicit_type_0_counts()
    {
        _items = [Item("sck_audio_capture", "Mac Audio")];
        _types["Mac Audio"] = 0;
        await Run();

        Assert.Equal(["SetInputMute"], Writes);
    }

    [Fact]
    public async Task Our_muted_desktop_audio_is_unmuted_then_the_window_muted()
    {
        _items = [Item("sck_audio_capture", "Sakshi Desktop Audio")];
        _muted.Add("Sakshi Desktop Audio");
        await Run();

        var mutes = _requests.Where(r => r.Type == "SetInputMute").Select(r =>
            (r.Data.GetProperty("inputName").GetString(), r.Data.GetProperty("inputMuted").GetBoolean())).ToArray();
        Assert.Equal([("Sakshi Desktop Audio", false), ("Src", true)], mutes);
        Assert.DoesNotContain("CreateInput", Writes);
    }

    [Fact]
    public async Task Our_hidden_desktop_audio_is_shown_then_the_window_muted()
    {
        _items = [Item("sck_audio_capture", "Sakshi Desktop Audio", enabled: false, id: 42)];
        await Run();

        Assert.Equal(["SetSceneItemEnabled", "SetInputMute"], Writes);
        var enable = Sent("SetSceneItemEnabled");
        Assert.Equal("Scene", enable.GetProperty("sceneName").GetString());
        Assert.Equal(42, enable.GetProperty("sceneItemId").GetInt32());
        Assert.True(enable.GetProperty("sceneItemEnabled").GetBoolean());
        Assert.True(MutedTheWindow);
    }

    [Fact]
    public async Task Our_desktop_audio_set_to_one_app_is_logged_and_leaves_the_window_audio_on()
    {
        _items = [Item("sck_audio_capture", "Sakshi Desktop Audio")];
        _types["Sakshi Desktop Audio"] = 1;
        await Run();

        Assert.Empty(Writes);
        Assert.Contains("window's own audio only", Assert.Single(_log.Lines));
    }

    [Theory]
    [InlineData("GetSceneItemList")]
    [InlineData("GetInputMute")]
    [InlineData("GetInputSettings")]
    public async Task A_failed_check_is_logged_and_leaves_the_window_audio_on(string failing)
    {
        _items = [Item("sck_audio_capture", "Mac Audio")];
        _failing.Add(failing);
        await Run();

        Assert.Empty(Writes);
        Assert.Contains("window's own audio only", Assert.Single(_log.Lines));
    }

    [Theory]
    [InlineData(true, false, "SetInputMute")]
    [InlineData(false, true, "SetSceneItemEnabled")]
    public async Task A_failed_unmute_or_show_is_logged_and_leaves_the_window_audio_on(bool muted, bool hidden, string failing)
    {
        _items = [Item("sck_audio_capture", "Sakshi Desktop Audio", enabled: !hidden)];
        if (muted) _muted.Add("Sakshi Desktop Audio");
        _failing.Add(failing);
        await Run();

        Assert.False(MutedTheWindow);
        Assert.Contains("window's own audio only", Assert.Single(_log.Lines));
    }

    [Fact]
    public async Task A_failed_window_mute_is_logged_as_doubled_audio()
    {
        _items = [Item("sck_audio_capture", "Mac Audio")];
        _failing.Add("SetInputMute");
        await Run();

        Assert.Contains("twice", Assert.Single(_log.Lines));
    }

    [Fact]
    public async Task Name_taken_in_another_scene_adds_it_to_this_scene_checks_it_then_mutes()
    {
        _createFailsWith = 601;
        await Run();

        Assert.Equal(["CreateInput", "CreateSceneItem", "SetInputMute"], Writes);
        Assert.Equal("Scene", Sent("CreateSceneItem").GetProperty("sceneName").GetString());
        Assert.Equal("Sakshi Desktop Audio", Sent("CreateSceneItem").GetProperty("sourceName").GetString());
        // The existing input was checked before the window was muted.
        Assert.Contains(_requests, r => r.Type == "GetInputMute" && r.Data.GetProperty("inputName").GetString() == "Sakshi Desktop Audio");
        Assert.Contains(_requests, r => r.Type == "GetInputSettings" && r.Data.GetProperty("inputName").GetString() == "Sakshi Desktop Audio");
        Assert.True(MutedTheWindow);
        Assert.Empty(_log.Lines);
    }

    [Fact]
    public async Task Name_taken_by_a_muted_input_unmutes_it_then_mutes_the_window()
    {
        _createFailsWith = 601;
        _muted.Add("Sakshi Desktop Audio");
        await Run();

        var mutes = _requests.Where(r => r.Type == "SetInputMute").Select(r =>
            (r.Data.GetProperty("inputName").GetString(), r.Data.GetProperty("inputMuted").GetBoolean())).ToArray();
        Assert.Equal([("Sakshi Desktop Audio", false), ("Src", true)], mutes);
    }

    [Fact]
    public async Task Name_taken_by_an_app_only_input_leaves_the_window_audio_on()
    {
        _createFailsWith = 601;
        _types["Sakshi Desktop Audio"] = 1;
        await Run();

        Assert.False(MutedTheWindow);
        Assert.Contains("window's own audio only", Assert.Single(_log.Lines));
    }

    [Fact]
    public async Task Name_taken_by_an_input_of_another_kind_leaves_the_window_audio_on()
    {
        _createFailsWith = 601;
        _existingKind = "coreaudio_input_capture";
        await Run();

        Assert.False(MutedTheWindow);
        Assert.Contains("window's own audio only", Assert.Single(_log.Lines));
    }

    [Fact]
    public async Task Other_create_failure_is_logged_and_leaves_the_window_audio_on()
    {
        _createFailsWith = 600;
        await Run();

        Assert.Equal(["GetSceneItemList", "CreateInput"], _requests.Select(r => r.Type));
        Assert.Single(_log.Lines);
        Assert.Contains("Scene", _log.Lines[0]);
        Assert.Contains("window's own audio only", _log.Lines[0]);
    }

    [Fact]
    public async Task Failed_add_to_scene_is_logged_and_leaves_the_window_audio_on()
    {
        _createFailsWith = 601;
        _addFails = true;
        await Run();

        Assert.Equal(["GetSceneItemList", "CreateInput", "CreateSceneItem"], _requests.Select(r => r.Type));
        Assert.Single(_log.Lines);
    }
}
