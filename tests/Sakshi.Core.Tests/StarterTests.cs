using System.Text.Json;
using Sakshi.Core.Config;
using Sakshi.Core.Obs;
using Sakshi.Core.Ports;
using Sakshi.Core.Recording;
using Sakshi.Core.State;
using Sakshi.Core.Store;
using Sakshi.TestSupport;

namespace Sakshi.Core.Tests;

public sealed class StarterTests : IAsyncDisposable
{
    private sealed class ListLog : ILog
    {
        public List<string> Lines { get; } = [];
        public void Info(string message) => Lines.Add(message);
    }

    private sealed class FakeCapture(Func<bool> onSelect, bool systemPicker = false) : IWindowCapture
    {
        public bool OpensSystemPicker => systemPicker;
        public int Calls { get; private set; }
        public bool? LastSourceJustCreated { get; private set; }
        public string InputKind => "test-capture-kind";
        public object NewSourceSettings { get; } = new { fake = 1 };
        public Task<bool> SelectWindowAsync(ObsClient obs, string scene, string source, bool sourceJustCreated, CancellationToken ct)
        {
            Calls++;
            LastSourceJustCreated = sourceJustCreated;
            return Task.FromResult(onSelect());
        }
    }

    private readonly string _root = Path.Combine(Path.GetTempPath(), "sakshi-starter-" + Guid.NewGuid().ToString("N"));
    private readonly FakeNotifier _notifier = new();
    private readonly ListLog _log = new();
    private readonly List<(string Type, JsonElement Data)> _calls = [];
    private readonly List<FakeObsServer> _servers = [];
    private readonly FakeClock _clock = new(new DateTimeOffset(2026, 11, 19, 10, 0, 0, TimeSpan.Zero));
    private readonly StateStore _store;
    private readonly SakshiConfig _config;

    // Scriptable server behaviour.
    private bool _recordingActive;
    private bool _sceneMissing;
    private bool _sourceMissing;
    private bool _startRecordFails;
    private bool _transformFails;
    private int _sizeAfterPolls;           // GetSceneItemTransform returns 0x0 this many times first
    private int _itemId = 7;
    private int _itemIdAfterCreate = 7;
    private int _itemIdCalls;
    private int _transformCalls;
    private Action? _onStartRecord;
    private bool _created;
    private bool _createInputExists;
    private bool _recordStatusThrows;
    private CancellationTokenSource? _cancelOnStartRecord;
    private readonly List<TimeSpan> _delays = [];

    public StarterTests()
    {
        Directory.CreateDirectory(_root);
        _config = new SakshiConfig { MeetingsDir = Path.Combine(_root, "meetings"), ObsScene = "Scene", ObsSource = "Src" };
        _store = new StateStore(Path.Combine(_root, "state.json"), _log, _notifier);
    }

    public ValueTask DisposeAsync()
    {
        foreach (var s in _servers) s.DisposeAsync().AsTask().GetAwaiter().GetResult();
        try { Directory.Delete(_root, true); } catch (IOException) { }
        return ValueTask.CompletedTask;
    }

    private (bool, object?) Handle(string type, JsonElement data)
    {
        lock (_calls) _calls.Add((type, data.ValueKind == JsonValueKind.Undefined ? default : data.Clone()));
        switch (type)
        {
            case "GetRecordStatus": if (_recordStatusThrows) throw new FormatException("kaboom"); return (true, new { outputActive = _recordingActive });
            case "SetCurrentProgramScene": return _sceneMissing ? (false, "no such scene") : (true, null);
            case "GetProfileList": return (true, new { currentProfileName = "Sakshi", profiles = new[] { "Sakshi" } });
            case "GetSceneCollectionList": return (true, new { currentSceneCollectionName = "Sakshi", sceneCollections = new[] { "Sakshi" } });
            case "GetSceneItemId" when data.GetProperty("sourceName").GetString()!.StartsWith("Sakshi "):
                return (false, "no such source");   // Sakshi's own sound sources: not there yet
            case "GetSceneItemId":
            {
                _itemIdCalls++;
                if (_sourceMissing && !_created) return (false, "no such source");
                return (true, new { sceneItemId = _created ? _itemIdAfterCreate : (_itemIdCalls > 1 ? _itemId + 100 : _itemId) });
            }
            case "CreateInput":
                if (_createInputExists) return (false, (601, "exists"));
                _created = true; return (true, new { sceneItemId = _itemIdAfterCreate });
            case "CreateSceneItem": _created = true; return (true, new { sceneItemId = _itemIdAfterCreate });
            case "GetVideoSettings": return (true, new { baseWidth = 1920, baseHeight = 1080 });
            case "GetSceneItemTransform":
                _transformCalls++;
                return _transformCalls <= _sizeAfterPolls
                    ? (true, new { sceneItemTransform = new { sourceWidth = 0.0, sourceHeight = 0.0 } })
                    : (true, new { sceneItemTransform = new { sourceWidth = 1280.0, sourceHeight = 720.0 } });
            case "SetSceneItemTransform": return _transformFails ? (false, "nope") : (true, null);
            case "StartRecord": _onStartRecord?.Invoke(); _cancelOnStartRecord?.Cancel(); return _startRecordFails ? (false, "no") : (true, null);
            default: return (true, null);
        }
    }

    private sealed class FakeLauncher : IObsLauncher
    {
        public int Ensured { get; private set; }
        public Task EnsureRunningAsync(CancellationToken ct) { Ensured++; return Task.CompletedTask; }
        public string? Password() => null;
    }

    private readonly FakeLauncher _launcher = new();
    private int _connects;

    private Starter Starter(FakeCapture capture, bool obsRunning = true, SakshiConfig? cfg = null)
    {
        var c = cfg ?? _config;
        var server = new FakeObsServer(Handle);
        _servers.Add(server);
        var meetings = new MeetingStore(c.MeetingsDir, Path.Combine(_root, "archive"), _log);
        return new Starter(c, _store, meetings, _launcher, capture, _notifier, _clock, _log,
            ct => { _connects++; return ObsClient.ConnectAsync("127.0.0.1", server.Port, null, ct); },
            () => obsRunning,
            (d, _) => { _delays.Add(d); return Task.CompletedTask; });
    }

    private string[] Types() { lock (_calls) return _calls.Select(c => c.Type).ToArray(); }

    private sealed class FakeWait : IPickerWait
    {
        public List<string> Seen { get; } = [];
        public void Opening() => Seen.Add("opening");
        public void Slow() => Seen.Add("slow");
        public void Done() => Seen.Add("done");
    }

    [Fact]
    public async Task A_system_picker_is_announced_while_OBS_starts_and_the_sign_always_goes()
    {
        var wait = new FakeWait();
        var server = new FakeObsServer(Handle);
        _servers.Add(server);
        var capture = new FakeCapture(() => false, systemPicker: true);   // the user picks nothing: the start fails
        var starter = new Starter(_config, _store, new MeetingStore(_config.MeetingsDir, Path.Combine(_root, "archive"), _log),
            _launcher, capture, _notifier, _clock, _log, ct => ObsClient.ConnectAsync("127.0.0.1", server.Port, null, ct),
            () => false, (_, _) => Task.CompletedTask, pickerWait: wait);

        Assert.Equal(0, await starter.StartAsync(TestContext.Current.CancellationToken));
        Assert.Equal("opening", wait.Seen.First());   // before OBS was started
        Assert.Equal("done", wait.Seen.Last());
    }

    /// <summary>A Starter that records on Sakshi's own OBS profile (OBS is already on it here).</summary>
    private Starter OwnProfileStarter(FakeCapture capture)
    {
        var server = new FakeObsServer(Handle);
        _servers.Add(server);
        var paths = new Sakshi.Core.Paths.SakshiPaths(Path.Combine(_root, "c"), Path.Combine(_root, "s"), Path.Combine(_root, "d"), Path.Combine(_root, "obs"));
        var settings = new SakshiObsSettings(_config, paths, Sakshi.Core.Paths.HostOs.Windows, _store, new FakePrompter(), _log,
            (_, _) => Task.CompletedTask);
        return new Starter(_config, _store, new MeetingStore(_config.MeetingsDir, Path.Combine(_root, "archive"), _log), _launcher,
            capture, _notifier, _clock, _log, ct => ObsClient.ConnectAsync("127.0.0.1", server.Port, null, ct), () => true,
            (_, _) => Task.CompletedTask, settings, () => null);
    }

    [Fact]
    public async Task On_its_own_profile_the_recording_is_the_windows_size_with_a_microphone_and_the_computers_sound()
    {
        Assert.Equal(0, await OwnProfileStarter(new FakeCapture(() => true)).StartAsync(TestContext.Current.CancellationToken));

        // The window is 1280×720 and the canvas was 1920×1080: the canvas becomes the window's size.
        var video = _calls.Single(c => c.Type == "SetVideoSettings" && c.Data.TryGetProperty("baseWidth", out _)).Data;
        Assert.Equal((1280, 720, 1280, 720), (video.GetProperty("baseWidth").GetInt32(), video.GetProperty("baseHeight").GetInt32(),
            video.GetProperty("outputWidth").GetInt32(), video.GetProperty("outputHeight").GetInt32()));
        var fit = _calls.Last(c => c.Type == "SetSceneItemTransform").Data.GetProperty("sceneItemTransform");
        Assert.Equal(1280, fit.GetProperty("boundsWidth").GetInt32());

        var inputs = _calls.Where(c => c.Type == "CreateInput").Select(c => (c.Data.GetProperty("inputName").GetString(), c.Data.GetProperty("inputKind").GetString())).ToList();
        Assert.Contains(("Sakshi Microphone", "wasapi_input_capture"), inputs);
        Assert.Contains(("Sakshi Desktop Audio", "wasapi_output_capture"), inputs);
        Assert.Contains("StartRecord", Types());
    }

    [Fact]
    public async Task Sound_only_records_its_own_scene_with_no_window_and_no_picker()
    {
        var capture = new FakeCapture(() => true, systemPicker: true);
        Assert.Equal(0, await OwnProfileStarter(capture).StartAsync(TestContext.Current.CancellationToken, soundOnly: true));

        Assert.Equal(0, capture.Calls);   // no window is picked
        Assert.Contains(_calls, c => c.Type == "SetCurrentProgramScene" && c.Data.GetProperty("sceneName").GetString() == "Scene (sound only)");
        Assert.DoesNotContain(_calls, c => c.Type == "CreateInput" && c.Data.GetProperty("inputName").GetString() == "Src");
        Assert.Contains(_calls, c => c.Type == "CreateInput" && c.Data.GetProperty("inputName").GetString() == "Sakshi Microphone");
        var video = _calls.Single(c => c.Type == "SetVideoSettings" && c.Data.TryGetProperty("baseWidth", out _)).Data;
        Assert.Equal(640, video.GetProperty("baseWidth").GetInt32());
        Assert.Contains("StartRecord", Types());
        Assert.True(_store.Load().Capture is { Phase: CapturePhase.Recording, AudioOnly: true });
    }

    [Fact]
    public async Task On_its_own_profile_a_missing_scene_is_made()
    {
        _sceneMissing = true;
        var starter = OwnProfileStarter(new FakeCapture(() => true));
        _calls.Clear();
        // The fake keeps saying the scene is missing; what matters is that Sakshi makes it rather than giving up.
        await starter.StartAsync(TestContext.Current.CancellationToken);
        Assert.Contains(_calls, c => c.Type == "CreateScene" && c.Data.GetProperty("sceneName").GetString() == "Scene");
    }

    [Fact]
    public async Task AlreadyRecordingAndObsRunning_NotifiesAndDoesNothing()
    {
        _store.Update(s => s with { Capture = new Capture(CapturePhase.Recording, _clock.Now) });
        var capture = new FakeCapture(() => true);
        var code = await Starter(capture).StartAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, code);
        Assert.Equal(0, _connects);
        Assert.Equal(0, _launcher.Ensured);
        Assert.Contains(_notifier.Sent, n => n is (Urgency.Low, "Already recording", "Click again to stop."));
    }

    [Theory]
    [InlineData(false)]   // the normal branch: OBS idle, the start would reach the window picker
    [InlineData(true)]    // the outputActive branch: OBS already recording, the start would track it
    public async Task UnnamedRecordingWaiting_RefusesToStart_AndKeepsIt(bool obsAlreadyRecording)
    {
        _recordingActive = obsAlreadyRecording;
        var video = Path.Combine(_root, "rec.mkv");
        File.WriteAllBytes(video, new byte[16]);
        var naming = new Capture(CapturePhase.Naming, _clock.Now.AddHours(-1), video, _clock.Now);
        _store.Update(s => s with { Capture = naming });

        var code = await Starter(new FakeCapture(() => true)).StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, code);
        Assert.Equal(naming, _store.Load().Capture);
        Assert.Empty(Types());
        Assert.Equal(0, _launcher.Ensured);
        Assert.Contains(_notifier.Sent, n => n is (Urgency.Critical, "Sakshi", "An unnamed recording is waiting — choose Name recording in the Sakshi menu to name and store it."));
    }

    [Fact]
    public async Task NamingWhoseVideoIsGone_StartsAfresh()
    {
        _store.Update(s => s with { Capture = new Capture(CapturePhase.Naming, _clock.Now, Path.Combine(_root, "gone.mkv"), _clock.Now) });
        var code = await Starter(new FakeCapture(() => true)).StartAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, code);
        Assert.Contains("StartRecord", Types());
    }

    [Fact]
    public async Task RecordingStateButObsNotRunning_StartsAgain()
    {
        _store.Update(s => s with { Capture = new Capture(CapturePhase.Recording, _clock.Now) });
        var code = await Starter(new FakeCapture(() => true), obsRunning: false).StartAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, code);
        Assert.Contains("StartRecord", Types());
    }

    [Fact]
    public async Task ObsAlreadyRecording_TracksIt()
    {
        _recordingActive = true;
        var code = await Starter(new FakeCapture(() => true)).StartAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, code);
        var cap = _store.Load().Capture;
        Assert.Equal(CapturePhase.Recording, cap.Phase);
        Assert.Equal(_clock.Now, cap.Started);
        Assert.Equal(["GetRecordStatus"], Types());
        Assert.Contains(_notifier.Sent, n => n is (Urgency.Low, "Already recording", "OBS was already recording; tracking it."));
    }

    [Fact]
    public async Task HappyPath_RequestSequenceStateAndNotification()
    {
        CapturePhase? phaseDuringStartRecord = null;
        _onStartRecord = () => phaseDuringStartRecord = _store.Load().Capture.Phase;
        var capture = new FakeCapture(() => true);
        var code = await Starter(capture).StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, code);
        Assert.Equal(1, _launcher.Ensured);
        Assert.Equal(1, capture.Calls);
        Assert.Equal(["GetRecordStatus", "SetCurrentProgramScene", "GetSceneItemId", "GetSceneItemId", "GetVideoSettings",
            "GetSceneItemTransform", "SetSceneItemTransform", "StartRecord"], Types());
        Assert.Equal(CapturePhase.Starting, phaseDuringStartRecord);

        var cap = _store.Load().Capture;
        Assert.Equal(CapturePhase.Recording, cap.Phase);
        Assert.Equal("1280x720", cap.SourceSize);
        Assert.Equal(_clock.Now, cap.Started);
        Assert.Contains(_notifier.Sent, n => n is (Urgency.Low, "Recording started", "1280×720 → 1920×1080. Click the bar icon to stop."));

        // The transform and the second lookup use the item id looked up after the window selection.
        var transform = _calls.Single(c => c.Type == "SetSceneItemTransform").Data;
        Assert.Equal(107, transform.GetProperty("sceneItemId").GetInt32());
        Assert.Equal("Scene", transform.GetProperty("sceneName").GetString());
        var t = transform.GetProperty("sceneItemTransform");
        Assert.Equal(
            """{"positionX":0,"positionY":0,"rotation":0,"alignment":5,"scaleX":1,"scaleY":1,"cropLeft":0,"cropTop":0,"cropRight":0,"cropBottom":0,"boundsType":"OBS_BOUNDS_SCALE_INNER","boundsAlignment":0,"boundsWidth":1920,"boundsHeight":1080}""",
            t.GetRawText());
        Assert.Equal(107, _calls.Single(c => c.Type == "GetSceneItemTransform").Data.GetProperty("sceneItemId").GetInt32());
        Assert.DoesNotContain(_notifier.Sent, n => n.Urgency == Urgency.Critical);
    }

    [Fact]
    public async Task ReselectDisabled_SkipsSelectionAndSecondLookup()
    {
        var capture = new FakeCapture(() => true);
        var code = await Starter(capture, cfg: _config with { ObsReselectWindow = false }).StartAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, code);
        Assert.Equal(1, capture.Calls); // the capture decides whether to pick
        Assert.Equal(2, Types().Count(t => t == "GetSceneItemId"));
        Assert.Equal(107, _calls.Single(c => c.Type == "SetSceneItemTransform").Data.GetProperty("sceneItemId").GetInt32());
    }

    [Fact]
    public async Task MissingScene_DiesAndStaysIdle()
    {
        _sceneMissing = true;
        var code = await Starter(new FakeCapture(() => true)).StartAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, code);
        Assert.Equal(CapturePhase.Idle, _store.Load().Capture.Phase);
        Assert.Contains(_notifier.Sent, n => n is (Urgency.Critical, "Sakshi", "scene 'Scene' not found in OBS"));
        Assert.Contains("scene 'Scene' not found in OBS", _log.Lines);
    }

    [Fact]
    public async Task MissingSource_IsCreatedWithTheCaptureKind()
    {
        _sourceMissing = true;
        _itemIdAfterCreate = 9;
        var capture = new FakeCapture(() => true);
        var code = await Starter(capture).StartAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, code);
        var create = _calls.Single(c => c.Type == "CreateInput").Data;
        Assert.Equal("Scene", create.GetProperty("sceneName").GetString());
        Assert.Equal("Src", create.GetProperty("inputName").GetString());
        Assert.Equal("test-capture-kind", create.GetProperty("inputKind").GetString());
        Assert.Equal("""{"fake":1}""", create.GetProperty("inputSettings").GetRawText());
        Assert.Contains(_log.Lines, l => l.Contains("creating it"));
        Assert.Contains("StartRecord", Types());
        Assert.Equal(1, capture.Calls);
        Assert.True(capture.LastSourceJustCreated);
        Assert.Equal(9, _calls.Single(c => c.Type == "SetSceneItemTransform").Data.GetProperty("sceneItemId").GetInt32());
    }

    [Fact]
    public async Task MissingSourceExistsElsewhere_AddsItToTheScene()
    {
        _sourceMissing = true;
        _createInputExists = true;
        var capture = new FakeCapture(() => true);
        var code = await Starter(capture).StartAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, code);
        var add = _calls.Single(c => c.Type == "CreateSceneItem").Data;
        Assert.Equal("Scene", add.GetProperty("sceneName").GetString());
        Assert.Equal("Src", add.GetProperty("sourceName").GetString());
        Assert.Equal(1, capture.Calls);
        Assert.Contains("StartRecord", Types());
    }

    [Fact]
    public async Task StaleRecordingState_IsClearedBeforeStarting()
    {
        _store.Update(s => s with { Capture = new Capture(CapturePhase.Recording, _clock.Now.AddHours(-1)) });
        _recordStatusThrows = true;
        var code = await Starter(new FakeCapture(() => true), obsRunning: false).StartAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, code);
        Assert.Equal(CapturePhase.Idle, _store.Load().Capture.Phase);
    }

    [Fact]
    public async Task UnexpectedException_LogsDetailAndShowsGenericText()
    {
        var code = await Starter(new FakeCapture(() => throw new FormatException("kaboom"))).StartAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, code);
        Assert.Contains(_notifier.Sent, n => n.Urgency == Urgency.Critical && n.Headline == "Sakshi"
            && n.Body.StartsWith("Could not start recording: ") && n.Body.EndsWith(" (see sakshi.log)"));
        Assert.Contains(_log.Lines, l => l.Contains("System.FormatException") && l.Contains(" at "));
    }

    [Fact]
    public async Task CancelledDuringStartRecord_StillRecordsAndTracksIt()
    {
        using var cts = new CancellationTokenSource();
        _cancelOnStartRecord = cts;
        var code = await Starter(new FakeCapture(() => true)).StartAsync(cts.Token);
        Assert.Equal(0, code);
        Assert.Equal(CapturePhase.Recording, _store.Load().Capture.Phase);
    }

    [Fact]
    public async Task HousekeepFailure_IsLoggedAndStartContinues()
    {
        if (OperatingSystem.IsWindows()) { Assert.Skip("needs Unix permission bits"); return; }
        Directory.CreateDirectory(_config.MeetingsDir);
        const UnixFileMode rwx = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        File.SetUnixFileMode(_config.MeetingsDir, UnixFileMode.None);
        try
        {
            var enforced = false;
            try { _ = Directory.GetFileSystemEntries(_config.MeetingsDir); }
            catch (UnauthorizedAccessException) { enforced = true; }
            if (!enforced) { Assert.Skip("permissions are not enforced (root?)"); return; }

            var code = await Starter(new FakeCapture(() => true)).StartAsync(TestContext.Current.CancellationToken);
            Assert.Equal(0, code);
            Assert.Contains(_log.Lines, l => l.StartsWith("housekeep: failed, carrying on"));
            Assert.Contains("StartRecord", Types());
        }
        finally
        {
            File.SetUnixFileMode(_config.MeetingsDir, rwx);
        }
    }

    [Fact]
    public async Task Closing_the_picker_without_a_choice_ends_the_start_at_once_and_quietly()
    {
        var code = await Starter(new FakeCapture(() => false)).StartAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, code);   // the user's decision, not a failure
        Assert.Equal(CapturePhase.Idle, _store.Load().Capture.Phase);
        Assert.DoesNotContain("StartRecord", Types());
        Assert.DoesNotContain("GetSceneItemTransform", Types());   // no waiting for a window that is not coming
        Assert.Equal([(Urgency.Low, "Not recording", "No window was picked.")], _notifier.Sent);
    }

    [Fact]
    public async Task The_capture_is_asked_even_for_a_new_source()
    {
        _sourceMissing = true;
        _itemIdAfterCreate = 9;
        var capture = new FakeCapture(() => true);
        await Starter(capture).StartAsync(TestContext.Current.CancellationToken);
        var types = Types();
        Assert.True(Array.IndexOf(types, "CreateInput") >= 0);
        Assert.Equal(1, capture.Calls);
        Assert.True(capture.LastSourceJustCreated);
    }

    [Fact]
    public async Task SizeNeverArrives_GoesIdleAndDies()
    {
        _sizeAfterPolls = int.MaxValue;
        var code = await Starter(new FakeCapture(() => true)).StartAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, code);
        Assert.Equal(CapturePhase.Idle, _store.Load().Capture.Phase);
        Assert.Equal(40, Types().Count(t => t == "GetSceneItemTransform"));
        Assert.Equal(39, _delays.Count(d => d == TimeSpan.FromMilliseconds(250)));
        Assert.DoesNotContain("StartRecord", Types());
        Assert.Contains(_notifier.Sent, n => n is (Urgency.Critical, "Sakshi", "No window selected — recording not started."));
    }

    [Fact]
    public async Task SizeArrivesLate_Waits()
    {
        _sizeAfterPolls = 3;
        var code = await Starter(new FakeCapture(() => true)).StartAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, code);
        Assert.Equal(4, Types().Count(t => t == "GetSceneItemTransform"));
    }

    [Fact]
    public async Task TransformFailure_IsLoggedAndRecordingStarts()
    {
        _transformFails = true;
        var code = await Starter(new FakeCapture(() => true)).StartAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, code);
        Assert.Contains("warning: fit-to-canvas transform failed", _log.Lines);
        Assert.Equal(CapturePhase.Recording, _store.Load().Capture.Phase);
    }

    [Fact]
    public async Task StartRecordRefused_GoesIdleAndDies()
    {
        _startRecordFails = true;
        var code = await Starter(new FakeCapture(() => true)).StartAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, code);
        Assert.Equal(CapturePhase.Idle, _store.Load().Capture.Phase);
        Assert.Contains(_notifier.Sent, n => n is (Urgency.Critical, "Sakshi", "OBS refused to start recording"));
    }

    [Fact]
    public async Task CaptureThrows_DiesWithItsMessageAndGoesIdle()
    {
        var capture = new FakeCapture(() => throw new InvalidOperationException("cannot find the scene collection file for 'X'"));
        var code = await Starter(capture).StartAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, code);
        Assert.Equal(CapturePhase.Idle, _store.Load().Capture.Phase);
        Assert.Contains(_notifier.Sent, n => n is (Urgency.Critical, "Sakshi", "cannot find the scene collection file for 'X'"));
    }

    [Fact]
    public async Task Cancelled_BeforeStartRecord_GoesIdle()
    {
        using var cts = new CancellationTokenSource();
        var capture = new FakeCapture(() => { cts.Cancel(); return true; });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Starter(capture).StartAsync(cts.Token));
        Assert.Equal(CapturePhase.Idle, _store.Load().Capture.Phase);
        Assert.DoesNotContain("StartRecord", Types());
    }

    [Fact]
    public async Task Launcher_Failure_Dies()
    {
        var failing = new Starter(_config, _store, new MeetingStore(_config.MeetingsDir, Path.Combine(_root, "a"), _log),
            new ThrowingLauncher(), new FakeCapture(() => true), _notifier, _clock, _log,
            _ => throw new InvalidOperationException("must not connect"), () => true);
        Assert.Equal(1, await failing.StartAsync(TestContext.Current.CancellationToken));
        Assert.Contains(_notifier.Sent, n => n is (Urgency.Critical, "Sakshi", "OBS started but its WebSocket port 4455 never opened"));
    }

    private sealed class ThrowingLauncher : IObsLauncher
    {
        public Task EnsureRunningAsync(CancellationToken ct) =>
            throw new InvalidOperationException("OBS started but its WebSocket port 4455 never opened");
        public string? Password() => null;
    }
}
