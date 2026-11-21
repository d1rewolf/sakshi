using System.Text.Json;
using System.Text.Json.Nodes;
using Sakshi.Core.Config;
using Sakshi.Core.Obs;
using Sakshi.Core.Paths;
using Sakshi.Core.Ports;
using Sakshi.Platform.Linux;
using Sakshi.TestSupport;

namespace Sakshi.Platform.Tests;

public sealed class PipeWireWindowCaptureTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sakshi-pw-" + Guid.NewGuid().ToString("N"));
    private readonly FakeNotifier _notifier = new();
    private readonly List<FakeObsServer> _servers = [];
    private readonly List<(string Type, string? Name)> _calls = [];
    private readonly List<TimeSpan> _delays = [];
    private readonly List<string> _logLines = [];
    private sealed class ListLog(List<string> lines) : ILog { public void Info(string m) => lines.Add(m); }
    private string _current = "Main";
    private bool _createFails;
    private bool _neverSwitches;
    private bool _switchBackFails;
    private int _zeroSizePolls = int.MaxValue;
    private int _sizePolls;
    private static string? Hyprland(string name) => name == "HYPRLAND_INSTANCE_SIGNATURE" ? "test-signature" : null;
    private string SceneFile => Path.Combine(_root, "obs", "basic", "scenes", "Main.json");

    public PipeWireWindowCaptureTests()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SceneFile)!);
        File.WriteAllText(Path.Combine(_root, "obs", "user.ini"), "[General]\nFoo=1\n\n[Basic]\nSceneCollectionFile=Main.json\nSceneCollection=Main\n");
        File.WriteAllText(SceneFile, """
            {"name":"Main","sources":[
              {"id":"pipewire-screen-capture-source","name":"Src","settings":{"RestoreToken":"abc","ShowCursor":true}},
              {"id":"other-source","name":"Other","settings":{"RestoreToken":"keep-me"}}
            ]}
            """);
    }

    public void Dispose()
    {
        foreach (var s in _servers) s.DisposeAsync().AsTask().GetAwaiter().GetResult();
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private SakshiPaths Paths => new(Path.Combine(_root, "c"), Path.Combine(_root, "s"), Path.Combine(_root, "d"), Path.Combine(_root, "obs"));

    private (bool, object?) Handle(string type, JsonElement data)
    {
        string? name = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("sceneCollectionName", out var n) ? n.GetString() : null;
        lock (_calls) _calls.Add((type, name));
        switch (type)
        {
            case "GetSceneCollectionList": return (true, new { currentSceneCollectionName = _current });
            case "CreateSceneCollection":
                if (_createFails) return (false, "exists");
                if (!_neverSwitches) _current = name!;
                return (true, null);
            case "SetCurrentSceneCollection":
                if (_switchBackFails && name == "Main") return (false, "refused");
                if (!_neverSwitches) _current = name!;
                return (true, null);
            case "GetSceneItemId": return (true, new { sceneItemId = 7 });
            case "GetSceneItemTransform":
                lock (_calls) return (true, new { sceneItemTransform = new { sourceWidth = _sizePolls++ < _zeroSizePolls ? 0 : 1280, sourceHeight = 720 } });
            default: return (true, null);
        }
    }

    private async Task<ObsClient> Connect()
    {
        var server = new FakeObsServer(Handle);
        _servers.Add(server);
        return await ObsClient.ConnectAsync("127.0.0.1", server.Port, null, TestContext.Current.CancellationToken);
    }

    private PipeWireWindowCapture Capture(Func<bool> picker, SakshiConfig? cfg = null, Func<string, string?>? env = null) =>
        new(cfg ?? new SakshiConfig(), Paths, new FakeProcessRunner(), _notifier, new ListLog(_logLines),
            (d, _) => { _delays.Add(d); return Task.CompletedTask; }, picker, env ?? Hyprland);

    [Fact]
    public void InputKind_IsPipeWire() => Assert.Equal("pipewire-screen-capture-source", Capture(() => false).InputKind);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_new_source_is_reloaded_through_a_hop_so_the_picker_opens(bool reselect)
    {
        // Created over obs-websocket, the source never handles the portal's reply; reloading it on OBS's
        // UI thread does. It has no token yet, so nothing is stripped, and the user is told to pick.
        var polls = 0;
        bool Picker() { polls++; return polls is >= 2 and < 4; }
        await using var obs = await Connect();
        var ok = await Capture(Picker, new SakshiConfig { ObsReselectWindow = reselect })
            .SelectWindowAsync(obs, "Scene", "Src", true, TestContext.Current.CancellationToken);

        Assert.True(ok);
        var hops = _calls.Where(c => c.Type is "CreateSceneCollection" or "SetCurrentSceneCollection").ToArray();
        Assert.Equal([("CreateSceneCollection", "sakshi-scratch"), ("SetCurrentSceneCollection", "Main")], hops);
        Assert.Equal("abc", (string)JsonNode.Parse(File.ReadAllText(SceneFile))!["sources"]![0]!["settings"]!["RestoreToken"]!);
        Assert.Contains(_notifier.Sent, n => n is (Urgency.Low, "Pick the window to record", "Select it in the share picker."));
        Assert.Equal(4, polls);
    }

    [Fact]
    public async Task A_new_source_does_not_need_the_collection_file()
    {
        File.Delete(SceneFile);
        await using var obs = await Connect();
        Assert.True(await Capture(() => false).SelectWindowAsync(obs, "Scene", "Src", true, TestContext.Current.CancellationToken));
        Assert.Equal("Main", _current);
    }

    [Fact]
    public async Task Reconnect_hops_keeping_the_token_and_asks_nothing()
    {
        var pickerChecks = 0;
        await using var obs = await Connect();
        await Capture(() => { pickerChecks++; return false; }).ReconnectAsync(obs, TestContext.Current.CancellationToken);

        var hops = _calls.Where(c => c.Type is "CreateSceneCollection" or "SetCurrentSceneCollection").ToArray();
        Assert.Equal([("CreateSceneCollection", "sakshi-scratch"), ("SetCurrentSceneCollection", "Main")], hops);
        Assert.Equal("abc", (string)JsonNode.Parse(File.ReadAllText(SceneFile))!["sources"]![0]!["settings"]!["RestoreToken"]!);
        Assert.Empty(_notifier.Sent);
        Assert.Equal(0, pickerChecks);
        Assert.DoesNotContain(TimeSpan.FromMilliseconds(500), _delays);
    }

    [Fact]
    public async Task No_hop_from_the_scratch_collection()
    {
        // A failed switch-back left OBS there; another hop would "return" to the empty scratch collection.
        _current = "sakshi-scratch";
        await using var obs = await Connect();
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Capture(() => false).ReconnectAsync(obs, TestContext.Current.CancellationToken));
        Assert.StartsWith("OBS is on Sakshi's empty 'sakshi-scratch' scene collection", e.Message);
        Assert.DoesNotContain(_calls, c => c.Type is "CreateSceneCollection" or "SetCurrentSceneCollection");
    }

    [Fact]
    public async Task Repick_strips_the_token_even_when_reselect_is_off()
    {
        var polls = 0;
        bool Picker() { polls++; return polls is >= 2 and < 3; }
        await using var obs = await Connect();
        await Capture(Picker, new SakshiConfig { ObsReselectWindow = false })
            .RepickAsync(obs, "Scene", "Src", TestContext.Current.CancellationToken);

        var pw = JsonNode.Parse(File.ReadAllText(SceneFile))!["sources"]![0]!["settings"]!.AsObject();
        Assert.False(pw.ContainsKey("RestoreToken"));
        Assert.Equal("Main", _current);
        Assert.Contains(_notifier.Sent, n => n is (Urgency.Low, "Pick the window to record", "Select it in the share picker."));
    }

    [Fact]
    public void NewSourceSettings_AreEmpty() => Assert.Equal("{}", System.Text.Json.JsonSerializer.Serialize(Capture(() => false).NewSourceSettings));

    [Fact]
    public async Task ReselectDisabled_DoesNothing()
    {
        await using var obs = await Connect();
        var ok = await Capture(() => false, new SakshiConfig { ObsReselectWindow = false })
            .SelectWindowAsync(obs, "Scene", "Src", false, TestContext.Current.CancellationToken);
        Assert.True(ok);
        Assert.Empty(_calls);
        Assert.Empty(_notifier.Sent);
    }

    [Fact]
    public async Task HopsToScratchStripsTokenAndSwitchesBack()
    {
        var polls = 0;
        // Picker appears on the 3rd check and closes on the 6th.
        bool Picker() { polls++; return polls is >= 3 and < 6; }
        await using var obs = await Connect();
        var ok = await Capture(Picker).SelectWindowAsync(obs, "Scene", "Src", false, TestContext.Current.CancellationToken);

        Assert.True(ok);
        var hops = _calls.Where(c => c.Type is "CreateSceneCollection" or "SetCurrentSceneCollection").ToArray();
        Assert.Equal([("CreateSceneCollection", "sakshi-scratch"), ("SetCurrentSceneCollection", "Main")], hops);
        Assert.Equal("Main", _current);

        var root = JsonNode.Parse(File.ReadAllText(SceneFile))!;
        var pw = root["sources"]![0]!["settings"]!.AsObject();
        Assert.False(pw.ContainsKey("RestoreToken"));
        Assert.True((bool)pw["ShowCursor"]!);
        Assert.Equal("keep-me", (string)root["sources"]![1]!["settings"]!["RestoreToken"]!);

        Assert.Contains(_notifier.Sent, n => n is (Urgency.Low, "Pick the window to record", "Select it in the share picker."));
        Assert.Contains(TimeSpan.FromMilliseconds(500), _delays);
        Assert.Equal(6, polls); // waited for it to appear, then for it to close
    }

    [Fact]
    public async Task CreateFails_FallsBackToSwitching()
    {
        _createFails = true;
        await using var obs = await Connect();
        await Capture(() => false).SelectWindowAsync(obs, "Scene", "Src", false, TestContext.Current.CancellationToken);
        Assert.Contains(("SetCurrentSceneCollection", "sakshi-scratch"), _calls);
        Assert.Equal("Main", _current);
    }

    [Fact]
    public async Task PickerNeverAppears_CarriesOnAfterFortyPolls()
    {
        var polls = 0;
        await using var obs = await Connect();
        var ok = await Capture(() => { polls++; return false; }).SelectWindowAsync(obs, "Scene", "Src", false, TestContext.Current.CancellationToken);
        Assert.True(ok);
        Assert.Equal(40, polls);
        Assert.Contains("picker never appeared, continuing with whatever OBS captures", _logLines);
    }

    [Fact]
    public async Task PickerNeverCloses_GivesUpAfterThreeMinutes()
    {
        var polls = 0;
        await using var obs = await Connect();
        var ok = await Capture(() => { polls++; return true; }).SelectWindowAsync(obs, "Scene", "Src", false, TestContext.Current.CancellationToken);
        Assert.True(ok);
        Assert.Equal(1 + 1 + 600, polls); // seen on the first check, then once per 300 ms delay, plus the final check
        Assert.Equal(600, _delays.Count(d => d == TimeSpan.FromMilliseconds(300)));
        Assert.Contains("picker still open after 3 min; carrying on", _logLines);
    }

    [Fact]
    public async Task Without_hyprland_waits_for_the_source_to_get_a_size()
    {
        _zeroSizePolls = 100;
        var pickerChecks = 0;
        await using var obs = await Connect();
        var ok = await Capture(() => { pickerChecks++; return false; }, env: _ => null)
            .SelectWindowAsync(obs, "Scene", "Src", false, TestContext.Current.CancellationToken);

        Assert.True(ok);
        Assert.Equal(0, pickerChecks);
        Assert.Equal(101, _calls.Count(c => c.Type == "GetSceneItemTransform"));
        Assert.Contains(_notifier.Sent, n => n is (Urgency.Low, "Pick the window to record", "Select it in the screen-sharing dialog."));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Without_hyprland_a_new_source_waits_for_a_size_whatever_reselect_says(bool reselect)
    {
        // A fresh PipeWire source opens GNOME's dialog once reloaded; the user still has to pick.
        _zeroSizePolls = 100;
        await using var obs = await Connect();
        var ok = await Capture(() => false, new SakshiConfig { ObsReselectWindow = reselect }, env: _ => null)
            .SelectWindowAsync(obs, "Scene", "Src", true, TestContext.Current.CancellationToken);

        Assert.True(ok);
        Assert.Equal(101, _calls.Count(c => c.Type == "GetSceneItemTransform"));
        Assert.Contains(("CreateSceneCollection", "sakshi-scratch"), _calls);
        Assert.Equal("Main", _current);
        Assert.Contains(_notifier.Sent, n => n is (Urgency.Low, "Pick the window to record", "Select it in the screen-sharing dialog."));
    }

    [Fact]
    public async Task Without_hyprland_gives_up_after_three_minutes()
    {
        await using var obs = await Connect();
        var ok = await Capture(() => false, env: _ => "").SelectWindowAsync(obs, "Scene", "Src", false, TestContext.Current.CancellationToken);

        Assert.True(ok);
        Assert.Equal(600, _calls.Count(c => c.Type == "GetSceneItemTransform"));
        Assert.True(_delays.Where(d => d == TimeSpan.FromMilliseconds(300)).Sum(d => d.TotalSeconds) >= 180);
    }

    [Fact]
    public async Task CancelledDuringHop_SwitchesBackAndStillCancels()
    {
        using var cts = new CancellationTokenSource();
        var capture = new PipeWireWindowCapture(new SakshiConfig(), Paths, new FakeProcessRunner(), _notifier, new ListLog(_logLines),
            (d, _) => { if (d == TimeSpan.FromMilliseconds(500)) cts.Cancel(); cts.Token.ThrowIfCancellationRequested(); return Task.CompletedTask; },
            () => false, Hyprland);
        await using var obs = await Connect();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => capture.SelectWindowAsync(obs, "Scene", "Src", false, cts.Token));
        Assert.Equal("Main", _current);
        Assert.Contains(("SetCurrentSceneCollection", "Main"), _calls);
    }

    [Fact]
    public async Task CancelledAndSwitchBackFails_OriginalPropagatesAndFailureIsLogged()
    {
        _switchBackFails = true;
        using var cts = new CancellationTokenSource();
        var capture = new PipeWireWindowCapture(new SakshiConfig(), Paths, new FakeProcessRunner(), _notifier, new ListLog(_logLines),
            (d, _) => { if (d == TimeSpan.FromMilliseconds(500)) cts.Cancel(); cts.Token.ThrowIfCancellationRequested(); return Task.CompletedTask; },
            () => false, Hyprland);
        await using var obs = await Connect();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => capture.SelectWindowAsync(obs, "Scene", "Src", false, cts.Token));
        Assert.Contains(_logLines, l => l.StartsWith("could not switch back to scene collection 'Main': "));
    }

    [Fact]
    public async Task SwitchBackFailsAfterSuccess_Throws()
    {
        _switchBackFails = true;
        await using var obs = await Connect();
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Capture(() => false).SelectWindowAsync(obs, "Scene", "Src", false, TestContext.Current.CancellationToken));
        Assert.Equal("could not switch back to scene collection 'Main'", e.Message);
    }

    [Fact]
    public async Task StripFailure_IsLoggedAndLeavesNoTempFile()
    {
        File.WriteAllText(SceneFile, "{ not json");
        await using var obs = await Connect();
        await Capture(() => false).SelectWindowAsync(obs, "Scene", "Src", false, TestContext.Current.CancellationToken);
        Assert.Contains(_logLines, l => l.StartsWith("could not strip RestoreToken"));
        Assert.Equal("{ not json", File.ReadAllText(SceneFile));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(SceneFile)!, "*.sakshi-tmp"));
        Assert.Equal("Main", _current);
    }

    [Fact]
    public async Task MissingSceneFile_Throws()
    {
        File.Delete(SceneFile);
        await using var obs = await Connect();
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Capture(() => false).SelectWindowAsync(obs, "Scene", "Src", false, TestContext.Current.CancellationToken));
        Assert.Equal("cannot find the scene collection file for 'Main'", e.Message);
        Assert.DoesNotContain(_calls, c => c.Type == "CreateSceneCollection");
    }

    [Fact]
    public async Task CollectionNeverSwitches_Throws()
    {
        _neverSwitches = true;
        await using var obs = await Connect();
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Capture(() => false).SelectWindowAsync(obs, "Scene", "Src", false, TestContext.Current.CancellationToken));
        Assert.Equal("OBS did not switch to the scratch scene collection", e.Message);
    }

    [Fact]
    public void ProcCmdline_MatchesArgv0FileName()
    {
        var proc = Path.Combine(_root, "proc");
        void Add(string pid, string cmdline)
        {
            Directory.CreateDirectory(Path.Combine(proc, pid));
            File.WriteAllText(Path.Combine(proc, pid, "cmdline"), cmdline);
        }
        Add("100", "/usr/bin/bash\0-c\0hyprland-preview-share-picker\0");
        Add("101", "/usr/bin/hyprland-preview-share-picker-extra\0");
        Directory.CreateDirectory(Path.Combine(proc, "self"));
        File.WriteAllText(Path.Combine(proc, "self", "cmdline"), "hyprland-preview-share-picker\0");
        Add("102", "");
        Assert.False(ProcCmdline.AnyArgv0Named(proc, "hyprland-preview-share-picker"));

        Add("103", "/usr/bin/hyprland-preview-share-picker\0--flag\0");
        Assert.True(ProcCmdline.AnyArgv0Named(proc, "hyprland-preview-share-picker"));
        Assert.False(ProcCmdline.AnyArgv0Named(Path.Combine(_root, "nope"), "x"));
    }

    [Fact]
    public async Task DefaultPickerCheck_OffLinuxUsesRunner()
    {
        Assert.SkipWhen(OperatingSystem.IsLinux(), "Linux scans /proc instead of asking the runner");
        var runner = new FakeProcessRunner();
        runner.Running.Add("hyprland-preview-share-picker");
        var polls = 0;
        var c = new PipeWireWindowCapture(new SakshiConfig(), Paths, runner, _notifier, new ListLog(_logLines),
            (_, _) => { if (++polls > 2) runner.Running.Clear(); return Task.CompletedTask; },
            env: Hyprland);
        await using var obs = await Connect();
        Assert.True(await c.SelectWindowAsync(obs, "Scene", "Src", false, TestContext.Current.CancellationToken));
        Assert.DoesNotContain("picker never appeared, continuing with whatever OBS captures", _logLines);
    }
}
