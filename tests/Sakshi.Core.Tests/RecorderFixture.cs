using System.Globalization;
using Sakshi.Core.Config;
using Sakshi.Core.HandOff;
using Sakshi.Core.Obs;
using Sakshi.Core.Ports;
using Sakshi.Core.Recording;
using Sakshi.Core.State;
using Sakshi.Core.Store;
using Sakshi.Core.Transcription;
using Sakshi.TestSupport;

namespace Sakshi.Core.Tests;

/// <summary>Everything a Recorder test needs, in a throwaway directory. No env vars, no windows, no OBS.</summary>
public sealed class RecorderFixture : IDisposable
{
    private sealed class Quiet : ILog { public void Info(string message) { } }

    private sealed class NeverRuns : ITranscriber
    {
        public Task<int> RunAsync(TranscriptionJob job, Action<int> onStarted, CancellationToken ct) =>
            throw new InvalidOperationException("recorder tests never run the queue");
    }

    public string Root { get; } = Path.Combine(Path.GetTempPath(), "sakshi-rec-" + Guid.NewGuid().ToString("N"));
    public string Rec => Path.Combine(Root, "rec");
    public string Meetings => Path.Combine(Root, "meetings");
    public string TrashDir => Path.Combine(Root, "trash");
    public FakePrompter Prompter { get; } = new();
    public FakeNotifier Notifier { get; } = new();
    public FakeTrash Trash { get; }
    public FakeClock Clock { get; } = new(new DateTimeOffset(new DateTime(2026, 10, 14, 10, 0, 0, DateTimeKind.Local)));
    public FakeObs Obs { get; } = new();
    public StateStore Store { get; }
    public SakshiConfig Config { get; set; }
    public bool ObsReachable { get; set; } = true;
    public Exception? ConnectFailure { get; set; }
    private readonly List<TranscriptionQueue> _queues = [];
    public List<TimeSpan> Delays { get; } = [];

    /// <summary>Runs inside every Recorder delay, after it was recorded (to cancel mid-wait, say).</summary>
    public Action<TimeSpan>? OnDelay { get; set; }

    public RecorderFixture()
    {
        Directory.CreateDirectory(Rec);
        Directory.CreateDirectory(Meetings);
        Trash = new FakeTrash(TrashDir);
        Store = new StateStore(Path.Combine(Root, "state.json"), new Quiet(), Notifier);
        Config = new SakshiConfig { MeetingsDir = Meetings, ArchiveDir = Path.Combine(Root, "archive"), FramesPrompt = false };
    }

    public Recorder Recorder()
    {
        var log = new Quiet();
        var resolver = new CommandResolver([], n => n == "PATH" ? "" : null);
        var queue = new TranscriptionQueue(Store, new NeverRuns(), new HandOffRunner(resolver, Notifier), Notifier, log,
            (_, _) => false, (_, _) => Task.CompletedTask);
        _queues.Add(queue);
        return new Recorder(Config, Store, new MeetingStore(Meetings, Config.EffectiveArchiveDir, log), Prompter, Notifier,
            Trash, Clock, log, queue, _ => ConnectFailure is not null ? throw ConnectFailure : Task.FromResult<IObs?>(ObsReachable ? Obs : null),
            (d, ct) => { ct.ThrowIfCancellationRequested(); Delays.Add(d); OnDelay?.Invoke(d); return Task.CompletedTask; });
    }

    public string MkVideo(string path, string mtime = "2026-01-05 10:00")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[4096]);
        File.SetLastWriteTime(path, DateTime.Parse(mtime, CultureInfo.InvariantCulture));
        return path;
    }

    public static DateTimeOffset Local(int y, int mo, int d, int h, int mi) =>
        new(new DateTime(y, mo, d, h, mi, 0, DateTimeKind.Local));

    /// <summary>A stranded recording: Capture = Naming with an existing video.</summary>
    public string Stranded(DateTimeOffset? stopped = null, string? name = null, FrameChoice? frames = null, string file = "2026-10-14 10-00-00.mp4")
    {
        var video = MkVideo(Path.Combine(Rec, file));
        Set(new Capture(CapturePhase.Naming, Video: video, Stopped: stopped ?? Clock.Now, Name: name, Frames: frames));
        return video;
    }

    public void Set(Capture capture) => Store.Update(s => s with { Capture = capture });

    public Capture Capture => Store.Load().Capture;
    public IReadOnlyList<TranscriptionJob> Jobs => Store.Load().Jobs;

    public IEnumerable<string> Files(string dir) =>
        Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories) : [];

    public int Stored(string name) => Files(Meetings).Count(f => Path.GetFileName(f).StartsWith(name + ".", StringComparison.Ordinal)
        && Path.GetFileName(Path.GetDirectoryName(f)) == name);

    public void Dispose()
    {
        foreach (var q in _queues) q.Dispose();
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
    }
}
