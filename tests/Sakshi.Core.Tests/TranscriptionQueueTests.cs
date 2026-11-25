using System.Collections.Immutable;
using Sakshi.Core.HandOff;
using Sakshi.Core.Ports;
using Sakshi.Core.State;
using Sakshi.Core.Transcription;
using Sakshi.TestSupport;

namespace Sakshi.Core.Tests;

[Collection("Environment")]
public sealed class TranscriptionQueueTests : IDisposable
{
    private sealed class Recorder : INotifier
    {
        public List<(string Headline, string Body, Urgency Urgency)> Sent { get; } = [];
        /// <summary>Headlines starting with this throw (after being recorded), as a broken notifier would.</summary>
        public string? ThrowOn { get; set; }
        public void Notify(string headline, string body, Urgency urgency)
        {
            lock (Sent) Sent.Add((headline, body, urgency));
            if (ThrowOn is not null && headline.StartsWith(ThrowOn, StringComparison.Ordinal))
                throw new InvalidOperationException("notifier broke");
        }
    }

    private sealed class Quiet : ILog { public void Info(string message) { } }

    private sealed class GateTranscriber : ITranscriber
    {
        public List<string> Started { get; } = [];
        public List<string> Snapshot() { lock (Started) return [.. Started]; }
        public HashSet<string> Throwing { get; } = [];
        public Dictionary<string, TaskCompletionSource<int>> Gates { get; } = [];
        public SemaphoreSlim StartedSignal { get; } = new(0);

        public TaskCompletionSource<int> Gate(string name)
        {
            lock (Gates) return Gates.TryGetValue(name, out var g) ? g : Gates[name] = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public async Task<int> RunAsync(TranscriptionJob job, Action<int> onStarted, CancellationToken ct)
        {
            lock (Started) Started.Add(job.Name);
            onStarted(4242);
            StartedSignal.Release();
            if (Throwing.Contains(job.Name)) throw new InvalidOperationException("boom " + job.Name);
            return await Gate(job.Name).Task.WaitAsync(ct);
        }
    }

    private readonly string _root = Path.Combine(Path.GetTempPath(), "sakshi-queue-" + Guid.NewGuid().ToString("N"));
    private readonly Recorder _notifier = new();
    private readonly GateTranscriber _fake = new();
    private readonly StateStore _store;
    private readonly string _argsFile;
    private readonly string _bin;
    private readonly string?[] _saved;
    private static readonly string[] Vars = ["TEST_ARGS_FILE", "TEST_EXIT_CODE", "TEST_SPAWN_SLEEPER", "TEST_READ_STDIN", "TEST_SLEEP_SECONDS"];

    public TranscriptionQueueTests()
    {
        Directory.CreateDirectory(_root);
        _store = new StateStore(Path.Combine(_root, "state.json"), new Quiet(), _notifier);
        _argsFile = Path.Combine(_root, "args.txt");
        _bin = TestHelper.Install(_root);
        _saved = Vars.Select(Environment.GetEnvironmentVariable).ToArray();
        foreach (var v in Vars) Environment.SetEnvironmentVariable(v, null);
    }

    public void Dispose()
    {
        for (var i = 0; i < Vars.Length; i++) Environment.SetEnvironmentVariable(Vars[i], _saved[i]);
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private TranscriptionJob Job(string name, string? handOff = null, int? pid = null)
    {
        var dir = Path.Combine(_root, "meetings", name);
        Directory.CreateDirectory(dir);
        return new TranscriptionJob(Guid.NewGuid(), dir, Path.Combine(dir, name + ".mp4"), name, "--every 30", handOff, pid);
    }

    private HandOffRunner HandOff() =>
        new(new CommandResolver([_bin], n => n == "PATH" ? "" : Environment.GetEnvironmentVariable(n)), _notifier);

    private TranscriptionQueue Queue(ITranscriber t, Func<int, DateTimeOffset?, bool>? alive = null) =>
        new(_store, t, HandOff(), _notifier, new Quiet(), alive ?? ((_, _) => false), (_, _) => Task.CompletedTask);

    // The helper creates the file before it writes the lines: wait for the content.
    private bool ArgsWritten()
    {
        try { return File.Exists(_argsFile) && File.ReadAllLines(_argsFile).Length >= 3; }
        catch (IOException) { return false; }
    }

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("condition not met");
            await Task.Delay(10);
        }
    }

    private static async Task Stop(CancellationTokenSource cts, Task run)
    {
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task Jobs_RunOneAtATime_InOrder()
    {
        var queue = Queue(_fake);
        using var cts = new CancellationTokenSource();
        var run = queue.RunAsync(cts.Token);
        queue.Enqueue(Job("a"));
        queue.Enqueue(Job("b"));

        await _fake.StartedSignal.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        await Task.Delay(150, TestContext.Current.CancellationToken);
        Assert.Equal(["a"], _fake.Snapshot());
        Assert.Equal(["a", "b"], _store.Load().Jobs.Select(j => j.Name));
        Assert.Equal(4242, _store.Load().Jobs[0].Pid);
        Assert.NotNull(_store.Load().Jobs[0].Started);

        _fake.Gate("a").SetResult(0);
        await _fake.StartedSignal.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        Assert.Equal(["a", "b"], _fake.Snapshot());
        _fake.Gate("b").SetResult(0);
        await Until(() => _store.Load().Jobs.IsEmpty);
        await Stop(cts, run);
    }

    [Theory]
    [InlineData(2, " · 2 voices to name")]
    [InlineData(1, " · 1 voice to name")]
    [InlineData(0, "")]
    public async Task Success_BodyCountsVoicesStillToName(int unnamed, string suffix)
    {
        var job = Job("spk");
        var speakers = new List<string> { "{\"id\":\"S1\",\"status\":\"named\",\"name\":\"Ada\"}" };
        speakers.AddRange(Enumerable.Range(2, unnamed).Select(i =>
            $"{{\"id\":\"S{i}\",\"status\":\"{(i % 2 == 0 ? "unknown" : "suggested")}\"}}"));
        File.WriteAllText(Path.Combine(job.Dir, "speakers.json"), $"{{\"version\":1,\"speakers\":[{string.Join(",", speakers)}]}}");
        _fake.Gate("spk").SetResult(0);
        var queue = Queue(_fake);
        using var cts = new CancellationTokenSource();
        var run = queue.RunAsync(cts.Token);
        queue.Enqueue(job);
        await Until(() => _store.Load().Jobs.IsEmpty && _notifier.Sent.Count > 0);

        Assert.Equal(Path.Combine(job.Dir, "meeting.md") + suffix, Assert.Single(_notifier.Sent).Body);
        await Stop(cts, run);
    }

    [Fact]
    public void SpeakerCount_is_zero_for_a_missing_or_broken_file()
    {
        var dir = Path.Combine(_root, "none");
        Directory.CreateDirectory(dir);
        Assert.Equal(0, SpeakerCount.ToName(dir));
        File.WriteAllText(Path.Combine(dir, "speakers.json"), "{ not json");
        Assert.Equal(0, SpeakerCount.ToName(dir));
        File.WriteAllText(Path.Combine(dir, "speakers.json"), "{\"speakers\":5}");
        Assert.Equal(0, SpeakerCount.ToName(dir));
    }

    [Fact]
    public async Task Success_NotifiesThenHandsOff()
    {
        Environment.SetEnvironmentVariable("TEST_ARGS_FILE", _argsFile);
        var job = Job("ok", $"{TestHelper.Name} requirements new");
        _fake.Gate("ok").SetResult(0);
        var queue = Queue(_fake);
        using var cts = new CancellationTokenSource();
        var run = queue.RunAsync(cts.Token);
        queue.Enqueue(job);
        await Until(() => ArgsWritten() && _store.Load().Jobs.IsEmpty && _notifier.Sent.Count > 0);

        var n = Assert.Single(_notifier.Sent);
        Assert.Equal("Transcript ready: ok", n.Headline);
        Assert.Equal(Path.Combine(job.Dir, "meeting.md"), n.Body);
        Assert.Equal(Urgency.Normal, n.Urgency);
        Assert.Equal(["requirements", "new", job.Dir], File.ReadAllLines(_argsFile));
        await Stop(cts, run);
    }

    [Fact]
    public async Task Success_NotifierThrows_HandOffStillRuns()
    {
        Environment.SetEnvironmentVariable("TEST_ARGS_FILE", _argsFile);
        var job = Job("loud", $"{TestHelper.Name} requirements new");
        _fake.Gate("loud").SetResult(0);
        _notifier.ThrowOn = "Transcript ready";
        var queue = Queue(_fake);
        using var cts = new CancellationTokenSource();
        var run = queue.RunAsync(cts.Token);
        queue.Enqueue(job);
        await Until(() => _store.Load().Jobs.IsEmpty && _notifier.Sent.Count > 0);

        Assert.True(ArgsWritten(), "the hand-off did not run");
        Assert.Equal(["requirements", "new", job.Dir], File.ReadAllLines(_argsFile));
        Assert.DoesNotContain(_notifier.Sent, n => n.Headline.StartsWith("Hand-off failed", StringComparison.Ordinal));
        await Stop(cts, run);
    }

    [Fact]
    public async Task Failure_NoHandOff_CriticalNotice()
    {
        Environment.SetEnvironmentVariable("TEST_ARGS_FILE", _argsFile);
        var job = Job("bad", $"{TestHelper.Name} requirements new");
        _fake.Gate("bad").SetResult(2);
        var queue = Queue(_fake);
        using var cts = new CancellationTokenSource();
        var run = queue.RunAsync(cts.Token);
        queue.Enqueue(job);
        await Until(() => _store.Load().Jobs.IsEmpty && _notifier.Sent.Count > 0);

        var n = Assert.Single(_notifier.Sent);
        Assert.Equal("Transcription failed: bad", n.Headline);
        Assert.Equal($"Video is safe in {job.Dir}; see transcribe.log", n.Body);
        Assert.Equal(Urgency.Critical, n.Urgency);
        Assert.False(File.Exists(_argsFile));
        await Stop(cts, run);
    }

    /// <summary>Exits non-zero like a crash in the speaker step: after (or, with old=true, without) writing meeting.md.</summary>
    private sealed class CrashingTranscriber(bool old) : ITranscriber
    {
        public Task<int> RunAsync(TranscriptionJob job, Action<int> onStarted, CancellationToken ct)
        {
            var md = Path.Combine(job.Dir, "meeting.md");
            if (old)
            {
                File.WriteAllText(md, "# from an earlier run");
                File.SetLastWriteTimeUtc(md, DateTime.UtcNow.AddHours(-1));
            }
            onStarted(4242);
            if (!old) File.WriteAllText(md, "# Meeting");
            return Task.FromResult(139);
        }
    }

    [Fact]
    public async Task NonZeroExit_AfterMeetingMdWasWritten_CountsAsDone()
    {
        Environment.SetEnvironmentVariable("TEST_ARGS_FILE", _argsFile);
        var job = Job("crash", $"{TestHelper.Name} requirements new");
        var queue = Queue(new CrashingTranscriber(old: false));
        using var cts = new CancellationTokenSource();
        var run = queue.RunAsync(cts.Token);
        queue.Enqueue(job);
        await Until(() => ArgsWritten() && _store.Load().Jobs.IsEmpty && _notifier.Sent.Count > 0);

        var n = Assert.Single(_notifier.Sent);
        Assert.Equal("Transcript ready: crash", n.Headline);
        Assert.Equal(Path.Combine(job.Dir, "meeting.md") + " · speaker labels skipped", n.Body);
        Assert.Equal(Urgency.Normal, n.Urgency);
        Assert.Equal(["requirements", "new", job.Dir], File.ReadAllLines(_argsFile));
        await Stop(cts, run);
    }

    [Fact]
    public async Task NonZeroExit_WithAnOlderMeetingMd_Fails()
    {
        Environment.SetEnvironmentVariable("TEST_ARGS_FILE", _argsFile);
        var job = Job("stale", $"{TestHelper.Name} requirements new");
        var queue = Queue(new CrashingTranscriber(old: true));
        using var cts = new CancellationTokenSource();
        var run = queue.RunAsync(cts.Token);
        queue.Enqueue(job);
        await Until(() => _store.Load().Jobs.IsEmpty && _notifier.Sent.Count > 0);

        Assert.Equal("Transcription failed: stale", Assert.Single(_notifier.Sent).Headline);
        Assert.False(File.Exists(_argsFile));
        await Stop(cts, run);
    }

    [Fact]
    public async Task FinishedJob_DoesNotClearRecordingCapture()
    {
        var recording = new Capture(CapturePhase.Recording, DateTimeOffset.Now, "/v/next.mkv");
        var queue = Queue(_fake);
        using var cts = new CancellationTokenSource();
        var run = queue.RunAsync(cts.Token);
        queue.Enqueue(Job("first"));
        await _fake.StartedSignal.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        // A new recording starts while the job is running (the queue's pid save is already done).
        _store.Update(st => st with { Capture = recording });
        _fake.Gate("first").SetResult(0);
        await Until(() => _store.Load().Jobs.IsEmpty);
        Assert.Equal(recording, _store.Load().Capture);
        await Stop(cts, run);
    }

    [Fact]
    public void Recover_DeadPidWithoutMeetingMd_RequeuesAndNotifiesOnce()
    {
        var job = Job("dead", pid: 99);
        _store.Update(_ => SakshiState.Empty with { Jobs = [job with { Started = DateTimeOffset.Now }] });
        Queue(_fake).Recover();

        var n = Assert.Single(_notifier.Sent);
        Assert.Equal("Transcription interrupted: dead", n.Headline);
        Assert.Equal($"Video is safe in {job.Dir}", n.Body);
        var kept = Assert.Single(_store.Load().Jobs);
        Assert.Equal(job.Id, kept.Id);
        Assert.Null(kept.Pid);
        Assert.Null(kept.Started);
    }

    [Fact]
    public async Task Recover_RequeuedJob_RunsAgain()
    {
        var job = Job("again", pid: 99);
        _store.Update(_ => SakshiState.Empty with { Jobs = [job] });
        _fake.Gate("again").SetResult(0);
        var queue = Queue(_fake);
        queue.Recover();
        using var cts = new CancellationTokenSource();
        var run = queue.RunAsync(cts.Token);
        await Until(() => _store.Load().Jobs.IsEmpty);
        Assert.Equal(["again"], _fake.Snapshot());
        await Stop(cts, run);
    }

    [Fact]
    public async Task Recover_AlivePid_WaitsThenCompletes()
    {
        Environment.SetEnvironmentVariable("TEST_ARGS_FILE", _argsFile);
        var job = Job("alive", $"{TestHelper.Name} requirements new", pid: 77);
        _store.Update(_ => SakshiState.Empty with { Jobs = [job] });
        var polls = 0;
        var queue = Queue(_fake, (_, _) => ++polls <= 3);
        queue.Recover();
        Assert.Equal(1, polls);
        Assert.Empty(_notifier.Sent);

        File.WriteAllText(Path.Combine(job.Dir, "meeting.md"), "# m");
        using var cts = new CancellationTokenSource();
        var run = queue.RunAsync(cts.Token);
        await Until(() => ArgsWritten() && _store.Load().Jobs.IsEmpty && _notifier.Sent.Count > 0);

        Assert.Empty(_fake.Snapshot());
        Assert.True(polls >= 4);
        var n = Assert.Single(_notifier.Sent);
        Assert.Equal("Transcript ready: alive", n.Headline);
        await Stop(cts, run);
    }

    [Fact]
    public async Task Recover_DeadPidWithMeetingMd_NoHandOffLog_NotifiesAndHandsOff()
    {
        Environment.SetEnvironmentVariable("TEST_ARGS_FILE", _argsFile);
        var job = Job("done", $"{TestHelper.Name} requirements new", pid: 99);
        File.WriteAllText(Path.Combine(job.Dir, "meeting.md"), "# m");
        _store.Update(_ => SakshiState.Empty with { Jobs = [job] });
        var queue = Queue(_fake);
        queue.Recover();
        Assert.Empty(_notifier.Sent);
        Assert.Single(_store.Load().Jobs);

        using var cts = new CancellationTokenSource();
        var run = queue.RunAsync(cts.Token);
        await Until(() => ArgsWritten() && _store.Load().Jobs.IsEmpty && _notifier.Sent.Count > 0);
        var n = Assert.Single(_notifier.Sent);
        Assert.Equal("Transcript ready: done", n.Headline);
        Assert.Equal(Path.Combine(job.Dir, "meeting.md"), n.Body);
        Assert.Empty(_fake.Snapshot());
        await Stop(cts, run);
    }

    [Fact]
    public void Recover_DeadPidWithMeetingMd_AndHandOffLog_WarnsAndDoesNotRerun()
    {
        var job = Job("done", $"{TestHelper.Name} requirements new", pid: 99);
        File.WriteAllText(Path.Combine(job.Dir, "meeting.md"), "# m");
        File.WriteAllText(Path.Combine(job.Dir, "handoff.log"), "$ x");
        _store.Update(_ => SakshiState.Empty with { Jobs = [job] });
        Queue(_fake).Recover();

        var n = Assert.Single(_notifier.Sent);
        Assert.Equal("Hand-off may not have finished: done", n.Headline);
        Assert.Equal($"Check {Path.Combine(job.Dir, "handoff.log")}", n.Body);
        Assert.Equal(Urgency.Critical, n.Urgency);
        Assert.Empty(_store.Load().Jobs);
        Assert.False(File.Exists(_argsFile));
    }

    [Fact]
    public void Recover_QueuedJobWithoutPid_NoNotice()
    {
        var job = Job("queued");
        _store.Update(_ => SakshiState.Empty with { Jobs = [job] });
        Queue(_fake).Recover();
        Assert.Empty(_notifier.Sent);
        Assert.Equal(job, Assert.Single(_store.Load().Jobs));
    }

    [Fact]
    public async Task ThrowingTranscriber_FailsThatJob_NextJobStillRuns()
    {
        _fake.Throwing.Add("poison");
        _fake.Gate("next").SetResult(0);
        var queue = Queue(_fake);
        using var cts = new CancellationTokenSource();
        var run = queue.RunAsync(cts.Token);
        queue.Enqueue(Job("poison"));
        queue.Enqueue(Job("next"));
        await Until(() => _store.Load().Jobs.IsEmpty && _notifier.Sent.Count >= 2);

        Assert.Equal(["poison", "next"], _fake.Snapshot());
        Assert.Equal("Transcription failed: poison", _notifier.Sent[0].Headline);
        Assert.Equal(Urgency.Critical, _notifier.Sent[0].Urgency);
        Assert.Equal("Transcript ready: next", _notifier.Sent[1].Headline);
        await Stop(cts, run);
    }

    [Fact]
    public async Task JobStaysPersisted_UntilHandOffHasRun()
    {
        Environment.SetEnvironmentVariable("TEST_SLEEP_SECONDS", "1");
        var job = Job("slowhand", $"{TestHelper.Name} requirements new");
        _fake.Gate("slowhand").SetResult(0);
        var queue = Queue(_fake);
        using var cts = new CancellationTokenSource();
        var run = queue.RunAsync(cts.Token);
        queue.Enqueue(job);
        await Until(() => _notifier.Sent.Count > 0);
        Assert.Single(_store.Load().Jobs); // hand-off (sleeping) not finished yet
        await Until(() => _store.Load().Jobs.IsEmpty);
        await Stop(cts, run);
    }

    [Fact]
    public async Task RunAsync_RefusesASecondConcurrentCall()
    {
        var queue = Queue(_fake);
        using var cts = new CancellationTokenSource();
        var run = queue.RunAsync(cts.Token);
        await Assert.ThrowsAsync<InvalidOperationException>(() => queue.RunAsync(cts.Token));
        await Stop(cts, run);
    }

    [Fact]
    public void ProcessAlive_RejectsAReusedPid()
    {
        var self = Environment.ProcessId;
        Assert.True(TranscriptionQueue.IsProcessAlive(self, DateTimeOffset.Now));
        Assert.True(TranscriptionQueue.IsProcessAlive(self, null));
        Assert.False(TranscriptionQueue.IsProcessAlive(self, DateTimeOffset.Now.AddDays(-1)));
    }

    private string Helper => Path.Combine(_bin, TestHelper.Name + (OperatingSystem.IsWindows() ? ".exe" : ""));

    [Fact]
    public async Task ChildProcess_LogsToTranscribeLog()
    {
        Environment.SetEnvironmentVariable("TEST_ARGS_FILE", _argsFile);
        var job = Job("child");
        var logPath = Path.Combine(job.Dir, "transcribe.log");
        File.WriteAllText(logPath, "stale from an earlier run");
        var pids = new List<int>();
        var code = await new ChildProcessTranscriber(Helper, ["transcribe"]).RunAsync(job, pids.Add, CancellationToken.None);

        Assert.Equal(0, code);
        Assert.Single(pids);
        Assert.Equal(["transcribe", "--log", logPath, "--every", "30", "-o", job.Dir, job.Video], File.ReadAllLines(_argsFile));
        var log = File.ReadAllText(logPath);
        Assert.Contains("helper stdout", log);
        Assert.Contains("helper stderr", log);
        Assert.DoesNotContain("stale", log);
    }

    [Fact]
    public async Task ChildProcess_CancelLeavesChildRunning_AndALaterQueueAdoptsIt()
    {
        Environment.SetEnvironmentVariable("TEST_SLEEP_SECONDS", "3");
        var job = Job("survivor");
        File.WriteAllText(Path.Combine(job.Dir, "meeting.md"), "# m");
        var transcriber = new ChildProcessTranscriber(Helper, []);
        var first = new TranscriptionQueue(_store, transcriber, HandOff(), _notifier, new Quiet(), TranscriptionQueue.IsProcessAlive, (_, _) => Task.CompletedTask);
        using var cts = new CancellationTokenSource();
        var run = first.RunAsync(cts.Token);
        first.Enqueue(job);
        await Until(() => _store.Load().Jobs.FirstOrDefault()?.Pid is not null);
        var pid = _store.Load().Jobs[0].Pid!.Value;
        await Stop(cts, run);

        Assert.True(TranscriptionQueue.IsProcessAlive(pid, null)); // not killed
        Assert.Equal(pid, Assert.Single(_store.Load().Jobs).Pid);
        Assert.Empty(_notifier.Sent);

        var second = new TranscriptionQueue(_store, transcriber, HandOff(), _notifier, new Quiet(), TranscriptionQueue.IsProcessAlive,
            (t, c) => Task.Delay(50, c));
        second.Recover();
        using var cts2 = new CancellationTokenSource();
        var run2 = second.RunAsync(cts2.Token);
        await Until(() => _store.Load().Jobs.IsEmpty && _notifier.Sent.Count > 0);
        Assert.Equal("Transcript ready: survivor", Assert.Single(_notifier.Sent).Headline);
        await Stop(cts2, run2);
    }

    [Fact]
    public async Task Store_ParallelUpdates_LoseNothing()
    {
        const int n = 150;
        var jobs = Task.Run(() =>
        {
            for (var i = 0; i < n; i++)
            {
                var job = Job("p" + i);
                _store.Update(st => st with { Jobs = st.Jobs.Add(job) });
            }
        }, TestContext.Current.CancellationToken);
        var capture = Task.Run(() =>
        {
            for (var i = 0; i < n; i++)
                _store.Update(st => st with { Capture = new Capture(CapturePhase.Recording, null, "v" + i) });
        }, TestContext.Current.CancellationToken);
        await Task.WhenAll(jobs, capture);

        var state = _store.Load();
        Assert.Equal(n, state.Jobs.Count);
        Assert.Equal("v" + (n - 1), state.Capture.Video);
    }
}
