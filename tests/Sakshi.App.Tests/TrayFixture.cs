using System.Globalization;
using Sakshi.App.Tray;
using Sakshi.Core.Config;
using Sakshi.Core.HandOff;
using Sakshi.Core.Obs;
using Sakshi.Core.Paths;
using Sakshi.Core.Ports;
using Sakshi.Core.Recording;
using Sakshi.Core.State;
using Sakshi.Core.Store;
using Sakshi.Core.Transcription;
using Sakshi.TestSupport;

namespace Sakshi.App.Tests;

public sealed class ListLog : ILog
{
    private readonly List<string> _lines = [];
    public IReadOnlyList<string> Lines { get { lock (_lines) return [.. _lines]; } }
    public void Info(string message) { lock (_lines) _lines.Add(message); }
}

/// <summary>An OBS launcher that never launches anything: it can block (a picker still open) or fail.</summary>
public sealed class FakeLauncher : IObsLauncher
{
    private int _calls;
    public int Calls => Volatile.Read(ref _calls);
    public TaskCompletionSource? Block { get; set; }
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task EnsureRunningAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref _calls);
        Entered.TrySetResult();
        if (Block is { } b) await b.Task.WaitAsync(ct);
        throw new InvalidOperationException("OBS is not available in tests");
    }

    public string? Password() => null;
}

/// <summary>The window picker: returns at once, or stays open until <see cref="Block"/> completes.</summary>
public sealed class FakeCapture : IWindowCapture
{
    public TaskCompletionSource? Block { get; set; }
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public string InputKind => "test-kind";
    public object NewSourceSettings => new { };
    public async Task<bool> SelectWindowAsync(ObsClient obs, string scene, string source, bool sourceJustCreated, CancellationToken ct)
    {
        Entered.TrySetResult();
        if (Block is { } b) await b.Task.WaitAsync(ct);
        return true;
    }
}

/// <summary>Records each job it runs and finishes it at once with exit 0.</summary>
public sealed class ScriptedTranscriber : ITranscriber
{
    public TaskCompletionSource<TranscriptionJob> Ran { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<TranscriptionJob> _runs = [];
    public IReadOnlyList<TranscriptionJob> Runs { get { lock (_runs) return [.. _runs]; } }

    /// <summary>Runs inside each job, before it finishes.</summary>
    public Action<TranscriptionJob>? OnRun { get; set; }

    public Task<int> RunAsync(TranscriptionJob job, Action<int> onStarted, CancellationToken ct)
    {
        lock (_runs) _runs.Add(job);
        OnRun?.Invoke(job);
        Ran.TrySetResult(job);
        return Task.FromResult(0);
    }
}

/// <summary>A TrayController wired to fakes in a throwaway directory. No env vars, no windows, no OBS, no processes.</summary>
public sealed class TrayFixture : IAsyncDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "sakshi-tray-" + Guid.NewGuid().ToString("N"));
    public string Rec => Path.Combine(Root, "rec");
    public string Meetings => Path.Combine(Root, "meetings");
    public string Archive => Path.Combine(Meetings, "archives");
    public string StateFile => Path.Combine(Root, "state", "state.json");
    public FakePrompter Prompter { get; } = new();
    public FakeNotifier Notifier { get; } = new();
    public FakeClock Clock { get; } = new(new DateTimeOffset(new DateTime(2026, 10, 14, 10, 0, 0, DateTimeKind.Local)));
    public FakeObs Obs { get; } = new();
    public FakeProcessRunner Runner { get; } = new();
    public FakeLauncher Launcher { get; } = new();
    public FakeCapture Picker { get; } = new();

    /// <summary>The services' capture recovery (the freeze check and re-pick); null by default, as on macOS and Windows.</summary>
    public ICaptureRecovery? Recovery { get; set; }

    /// <summary>When set (see <see cref="UseObsServer"/>), the Starter talks to this in-process fake OBS.</summary>
    public FakeObsServer? ObsServer { get; private set; }
    public ScriptedTranscriber Transcriber { get; } = new();
    public ListLog Log { get; } = new();
    public StateStore Store { get; }
    public SakshiConfig Config { get; set; }

    /// <summary>Awaited by the Recorder's connectObs before it hands out <see cref="Obs"/> (a slow OBS).</summary>
    public TaskCompletionSource? ConnectBlock { get; set; }
    public TaskCompletionSource ConnectEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>The services' OBS check when set; otherwise "obs" in <see cref="Runner"/>'s running set.</summary>
    public Func<bool>? ObsCheck { get; set; }
    private bool IsObsRunning() => ObsCheck?.Invoke() ?? Runner.IsRunning("obs");

    /// <summary>When set, the Recorder's delays throw it (an unexpected failure inside a stop).</summary>
    public Exception? RecorderDelayFailure { get; set; }

    /// <summary>Every delay the controller asked for.</summary>
    public List<TimeSpan> Delays { get; } = [];

    /// <summary>The controller's delay. Default: the 15 s reminder wait never ends (until cancelled); others return at once.</summary>
    public Func<TimeSpan, CancellationToken, Task>? OnDelay { get; set; }

    private readonly List<TranscriptionQueue> _queues = [];
    private readonly List<TrayController> _controllers = [];

    public TrayFixture()
    {
        Directory.CreateDirectory(Rec);
        Directory.CreateDirectory(Meetings);
        Store = new StateStore(StateFile, Log, Notifier);
        Config = new SakshiConfig
        {
            MeetingsDir = Meetings, ArchiveDir = Archive, KeepPrompt = false, FramesPrompt = false,
            RemindInterval = 1800, RemindPromptTimeout = 600,
        };
    }

    public TrayController Controller()
    {
        var paths = new SakshiPaths(Path.Combine(Root, "config"), Path.GetDirectoryName(StateFile)!, Path.Combine(Root, "data"), Path.Combine(Root, "obs"));
        var meetings = new MeetingStore(Config.MeetingsDir, Config.EffectiveArchiveDir, Log);
        var resolver = new CommandResolver([], n => n == "PATH" ? "" : null);
        var queue = new TranscriptionQueue(Store, Transcriber, new HandOffRunner(resolver, Notifier), Notifier, Log,
            (_, _) => false, (_, _) => Task.CompletedTask);
        _queues.Add(queue);
        Func<TimeSpan, CancellationToken, Task> noWait = (_, ct) => { ct.ThrowIfCancellationRequested(); return Task.CompletedTask; };
        var recorder = new Recorder(Config, Store, meetings, Prompter, Notifier, new FakeTrash(Path.Combine(Root, "trash")), Clock, Log, queue,
            async ct =>
            {
                ConnectEntered.TrySetResult();
                if (ConnectBlock is { } b) await b.Task.WaitAsync(ct);
                return IsObsRunning() ? Obs : null;
            }, (_, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                if (RecorderDelayFailure is { } e) throw e;
                return Task.CompletedTask;
            });
        var starter = new Starter(Config, Store, meetings, ObsServer is null ? Launcher : new NoopLauncher(), Picker, Notifier, Clock, Log,
            ct => ObsServer is { } server
                ? ObsClient.ConnectAsync("127.0.0.1", server.Port, null, ct)
                : throw new InvalidOperationException("no OBS in tests"),
            IsObsRunning, noWait);
        var services = new TrayServices(paths, HostOs.Linux, Path.Combine(Root, "home"), Config, Store, recorder, starter, queue,
            Prompter, Notifier, Log, Clock, Runner)
        {
            ObsRunning = IsObsRunning,
            CaptureRecovery = Recovery,
            ConnectObs = ct => ObsServer is { } server
                ? ObsClient.ConnectAsync("127.0.0.1", server.Port, null, ct)
                : throw new InvalidOperationException("no OBS in tests"),
            Delay = (d, ct) =>
            {
                lock (Delays) Delays.Add(d);
                if (OnDelay is { } f) return f(d, ct);
                return d == TimeSpan.FromSeconds(15) ? Task.Delay(Timeout.Infinite, ct) : Task.CompletedTask;
            },
        };
        var c = new TrayController(services);
        _controllers.Add(c);
        return c;
    }

    /// <summary>A fake OBS that is not recording and has the scene and source: a start reaches the picker.</summary>
    public void UseObsServer() =>
        ObsServer = new FakeObsServer((type, _) => type switch
        {
            "GetRecordStatus" => (true, new { outputActive = false }),
            "GetSceneItemId" => (true, new { sceneItemId = 7 }),
            _ => (true, null),
        });

    private sealed class NoopLauncher : IObsLauncher
    {
        public Task EnsureRunningAsync(CancellationToken ct) => Task.CompletedTask;
        public string? Password() => null;
    }

    public void ObsRunning(bool running)
    {
        lock (Runner.Gate)
        {
            if (running) Runner.Running.Add("obs");
            else Runner.Running.Remove("obs");
        }
    }

    public void Set(Capture capture) => Store.Update(s => s with { Capture = capture });
    public Capture Capture => Store.Load().Capture;

    public string MkVideo(string path, string mtime = "2026-01-05 10:00")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[4096]);
        File.SetLastWriteTime(path, DateTime.Parse(mtime, CultureInfo.InvariantCulture));
        return path;
    }

    public List<(Urgency Urgency, string Headline, string Body)> Sent
    {
        get { lock (Notifier.Sent) return [.. Notifier.Sent]; }
    }

    /// <summary>Polls until <paramref name="condition"/> holds (5 s at most).</summary>
    public static async Task Until(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail($"timed out waiting for {what}");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var c in _controllers) await c.DisposeAsync();
        foreach (var q in _queues) q.Dispose();
        if (ObsServer is not null) await ObsServer.DisposeAsync();
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
    }
}
