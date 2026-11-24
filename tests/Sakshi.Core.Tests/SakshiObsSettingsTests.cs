using System.Text.Json;
using Sakshi.Core.Config;
using Sakshi.Core.Obs;
using Sakshi.Core.Paths;
using Sakshi.Core.Ports;
using Sakshi.Core.Recording;
using Sakshi.Core.State;
using Sakshi.TestSupport;

namespace Sakshi.Core.Tests;

public sealed class SakshiObsSettingsTests : IAsyncDisposable
{
    private sealed class ListLog : ILog
    {
        public List<string> Lines { get; } = [];
        public void Info(string message) => Lines.Add(message);
    }

    private readonly string _root = Path.Combine(Path.GetTempPath(), "sakshi-obssettings-" + Guid.NewGuid().ToString("N"));
    private readonly ListLog _log = new();
    private readonly FakePrompter _prompter = new();
    private readonly StateStore _store;
    private readonly List<FakeObsServer> _servers = [];
    private readonly List<(string Type, JsonElement Data)> _calls = [];

    // OBS as the fake server holds it.
    private string _profile = "Untitled", _collection = "Untitled";
    private readonly List<string> _profiles = ["Untitled"], _collections = ["Untitled"];
    private bool _streaming;
    private int _recordingFor;   // GetRecordStatus says "active" this many more times
    private bool _collectionsStuck;   // OBS ignores collection switches

    public SakshiObsSettingsTests()
    {
        Directory.CreateDirectory(_root);
        _store = new StateStore(Path.Combine(_root, "state.json"), _log, new FakeNotifier());
    }

    public ValueTask DisposeAsync()
    {
        foreach (var s in _servers) s.DisposeAsync().AsTask().GetAwaiter().GetResult();
        try { Directory.Delete(_root, true); } catch (IOException) { }
        return ValueTask.CompletedTask;
    }

    private SakshiPaths Paths => new(Path.Combine(_root, "c"), Path.Combine(_root, "s"), Path.Combine(_root, "d"), Path.Combine(_root, "obs"));

    private (bool, object?) Handle(string type, JsonElement data)
    {
        lock (_calls) _calls.Add((type, data.ValueKind == JsonValueKind.Undefined ? default : data.Clone()));
        string Field(string name) => data.GetProperty(name).GetString()!;
        switch (type)
        {
            case "GetProfileList": return (true, new { currentProfileName = _profile, profiles = _profiles });
            case "GetSceneCollectionList": return (true, new { currentSceneCollectionName = _collection, sceneCollections = _collections });
            case "SetCurrentProfile": _profile = Field("profileName"); return (true, null);
            case "CreateProfile": _profiles.Add(Field("profileName")); _profile = Field("profileName"); return (true, null);
            case "SetCurrentSceneCollection": if (!_collectionsStuck) _collection = Field("sceneCollectionName"); return (true, null);
            case "CreateSceneCollection":
                _collections.Add(Field("sceneCollectionName"));
                if (!_collectionsStuck) _collection = Field("sceneCollectionName");
                return (true, null);
            case "GetStreamStatus": return (true, new { outputActive = _streaming });
            case "GetRecordStatus": return (true, new { outputActive = _recordingFor-- > 0 });
            case "GetVirtualCamStatus": return (true, new { outputActive = false });
            default: return (true, null);
        }
    }

    private async Task<T> WithObs<T>(Func<SakshiObsSettings, ObsClient, Task<T>> act, SakshiConfig? config = null)
    {
        var server = new FakeObsServer(Handle);
        _servers.Add(server);
        await using var obs = await ObsClient.ConnectAsync("127.0.0.1", server.Port, null, TestContext.Current.CancellationToken);
        var settings = new SakshiObsSettings(config ?? new SakshiConfig(), Paths, HostOs.Windows, _store, _prompter, _log,
            (_, _) => Task.CompletedTask);
        return await act(settings, obs);
    }

    private string[] Types() { lock (_calls) return _calls.Select(c => c.Type).ToArray(); }

    [Fact]
    public async Task OBS_that_Sakshi_started_is_switched_without_asking_and_switched_back_after()
    {
        var ok = await WithObs((s, obs) => s.EnterAsync(obs, ask: false, TestContext.Current.CancellationToken));

        Assert.True(ok);
        Assert.Empty(_prompter.Shown);
        Assert.Equal(("Sakshi", "Sakshi"), (_profile, _collection));
        Assert.Equal(new ObsReturn("Untitled", "Untitled"), _store.Load().ObsReturn);
        Assert.NotNull(SakshiObsProfile.DirectoryOf(Paths.ObsConfigDir, "Sakshi"));   // its settings written first

        await WithObs(async (s, obs) => { await s.LeaveAsync(obs, TestContext.Current.CancellationToken); return 0; });
        Assert.Equal(("Untitled", "Untitled"), (_profile, _collection));
        Assert.Null(_store.Load().ObsReturn);
    }

    [Fact]
    public async Task OBS_the_user_opened_is_switched_only_when_they_agree()
    {
        _prompter.Answer("Don't record");
        var ok = await WithObs((s, obs) => s.EnterAsync(obs, ask: true, TestContext.Current.CancellationToken));

        Assert.False(ok);
        Assert.Equal("OBS is open with your own settings", _prompter.Shown.Single());
        Assert.Equal(("Untitled", "Untitled"), (_profile, _collection));
        Assert.DoesNotContain("SetCurrentProfile", Types());
        Assert.Null(_store.Load().ObsReturn);
    }

    [Fact]
    public async Task Agreeing_switches()
    {
        _prompter.Answer("Switch to Sakshi's settings");
        Assert.True(await WithObs((s, obs) => s.EnterAsync(obs, ask: true, TestContext.Current.CancellationToken)));
        Assert.Equal(("Sakshi", "Sakshi"), (_profile, _collection));
    }

    [Fact]
    public async Task A_profile_obs_does_not_know_yet_is_made_by_obs_and_written_while_obs_is_elsewhere()
    {
        // OBS was already running when Sakshi's profile was first needed: OBS makes it, Sakshi goes back, writes, returns.
        Assert.True(await WithObs((s, obs) => s.EnterAsync(obs, ask: false, TestContext.Current.CancellationToken)));
        var types = Types();
        var created = Array.IndexOf(types, "CreateProfile");
        Assert.True(created >= 0);
        Assert.Contains(_calls.Skip(created), c => c.Type == "SetCurrentProfile" && c.Data.GetProperty("profileName").GetString() == "Untitled");
        Assert.Equal("Sakshi", _calls.Last(c => c.Type == "SetCurrentProfile").Data.GetProperty("profileName").GetString());
    }

    [Fact]
    public async Task Already_on_Sakshis_settings_OBS_steps_away_so_the_new_settings_are_read()
    {
        (_profile, _collection) = ("Sakshi", "Sakshi");
        _profiles.Add("Sakshi");
        _collections.Add("Sakshi");
        Assert.True(await WithObs((s, obs) => s.EnterAsync(obs, ask: true, TestContext.Current.CancellationToken),
            new SakshiConfig { RecordFormat = "mkv" }));

        Assert.Empty(_prompter.Shown);   // nothing of the user's is changed: no question
        Assert.Equal(["Untitled", "Sakshi"], _calls.Where(c => c.Type == "SetCurrentProfile")
            .Select(c => c.Data.GetProperty("profileName").GetString()));
        var ini = File.ReadAllText(Path.Combine(SakshiObsProfile.DirectoryOf(Paths.ObsConfigDir, "Sakshi")!, "basic.ini"));
        Assert.Equal("mkv", IniText.Get(ini, "AdvOut", "RecFormat2"));
        Assert.Equal(("Sakshi", "Sakshi"), (_profile, _collection));
    }

    [Fact]
    public async Task OBS_that_is_streaming_is_left_alone()
    {
        _streaming = true;
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WithObs((s, obs) => s.EnterAsync(obs, ask: false, TestContext.Current.CancellationToken)));
        Assert.Contains("streaming", e.Message);
        Assert.Equal(("Untitled", "Untitled"), (_profile, _collection));
        Assert.Null(_store.Load().ObsReturn);
    }

    [Fact]
    public async Task Leaving_waits_for_OBS_to_finish_the_recording_before_switching_back()
    {
        await WithObs((s, obs) => s.EnterAsync(obs, ask: false, TestContext.Current.CancellationToken));
        _recordingFor = 3;
        lock (_calls) _calls.Clear();
        await WithObs(async (s, obs) => { await s.LeaveAsync(obs, TestContext.Current.CancellationToken); return 0; });

        var types = Types();
        Assert.Equal(4, types.Count(t => t == "GetRecordStatus"));
        Assert.True(Array.LastIndexOf(types, "GetRecordStatus") < Array.IndexOf(types, "SetCurrentSceneCollection"));
    }

    [Fact]
    public async Task A_switch_that_fails_halfway_puts_the_users_settings_back()
    {
        _collectionsStuck = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WithObs((s, obs) => s.EnterAsync(obs, ask: false, TestContext.Current.CancellationToken)));
        Assert.Equal(("Untitled", "Untitled"), (_profile, _collection));
        Assert.Null(_store.Load().ObsReturn);
    }

    [Fact]
    public async Task Leaving_does_not_move_OBS_off_settings_the_user_chose_since()
    {
        _store.Update(s => s with { ObsReturn = new ObsReturn("Old", "Old") });
        _profiles.Add("Old");
        _collections.Add("Old");
        await WithObs(async (s, obs) => { await s.LeaveAsync(obs, TestContext.Current.CancellationToken); return 0; });
        Assert.Equal(("Untitled", "Untitled"), (_profile, _collection));
        Assert.Null(_store.Load().ObsReturn);
    }

    [Fact]
    public async Task Leaving_gives_up_while_OBS_is_still_recording()
    {
        await WithObs((s, obs) => s.EnterAsync(obs, ask: false, TestContext.Current.CancellationToken));
        _recordingFor = int.MaxValue;
        await WithObs(async (s, obs) => { await s.LeaveAsync(obs, TestContext.Current.CancellationToken); return 0; });
        Assert.Equal(("Sakshi", "Sakshi"), (_profile, _collection));
        Assert.NotNull(_store.Load().ObsReturn);   // the next stop tries again
    }

    [Fact]
    public async Task Leaving_when_Sakshi_switched_nothing_does_nothing()
    {
        await WithObs(async (s, obs) => { await s.LeaveAsync(obs, TestContext.Current.CancellationToken); return 0; });
        Assert.Empty(Types());
    }
}
