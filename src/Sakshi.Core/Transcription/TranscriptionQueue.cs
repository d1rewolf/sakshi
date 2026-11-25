using System.Diagnostics;
using Sakshi.Core.HandOff;
using Sakshi.Core.Ports;
using Sakshi.Core.State;

namespace Sakshi.Core.Transcription;

/// <summary>
/// Runs persisted transcription jobs one at a time, in order. State changes go through
/// <see cref="StateStore.Update"/> on the Jobs list only, so a Capture change made
/// elsewhere is never overwritten. A job is removed only after its notification and
/// hand-off are done, so a crash in between is picked up by <see cref="Recover"/>.
/// </summary>
public sealed class TranscriptionQueue(
    StateStore store,
    ITranscriber transcriber,
    HandOffRunner handOff,
    INotifier notifier,
    ILog log,
    Func<int, DateTimeOffset?, bool> processAlive,
    Func<TimeSpan, CancellationToken, Task>? delay = null) : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan StartSlack = TimeSpan.FromSeconds(10);

    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;
    private readonly SemaphoreSlim _signal = new(0);
    private readonly HashSet<Guid> _adopted = [];
    private int _running;

    /// <summary>
    /// True when a live process has this pid and was not started after <paramref name="started"/>
    /// (plus slack), which rules out a recycled pid. When the start time cannot be read the
    /// process is assumed to be the job's. Pid reuse inside the slack window remains possible.
    /// </summary>
    public static bool IsProcessAlive(int pid, DateTimeOffset? started)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            if (p.HasExited) return false;
            if (started is null) return true;
            try { return p.StartTime <= started.Value + StartSlack; }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { return true; }
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    public void Dispose() => _signal.Dispose();

    public void Enqueue(TranscriptionJob job)
    {
        store.Update(s => s with { Jobs = s.Jobs.Add(job) });
        _signal.Release();
    }

    /// <summary>Call once at startup, before <see cref="RunAsync"/>. Does not block on live pids.</summary>
    public void Recover()
    {
        foreach (var job in store.Load().Jobs)
        {
            if (job.Pid is not { } pid) continue; // never started: simply still queued

            if (processAlive(pid, job.Started))
            {
                _adopted.Add(job.Id);
                log.Info($"transcription of {job.Name} still running (pid {pid}); waiting");
            }
            else if (!File.Exists(Path.Combine(job.Dir, "meeting.md")))
            {
                notifier.Notify($"Transcription interrupted: {job.Name}", $"Video is safe in {job.Dir}", Urgency.Normal);
                Modify(job.Id, j => j with { Pid = null, Started = null });
            }
            else if (!File.Exists(Path.Combine(job.Dir, "handoff.log")))
            {
                _adopted.Add(job.Id); // finished without being reported: RunAsync notifies and hands off
            }
            else
            {
                var logPath = Path.Combine(job.Dir, "handoff.log");
                notifier.Notify($"Hand-off may not have finished: {job.Name}", $"Check {logPath}", Urgency.Critical);
                Remove(job.Id);
            }
        }
    }

    public async Task RunAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _running, 1) == 1)
            throw new InvalidOperationException("The transcription queue is already running.");
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var job = store.Load().Jobs.FirstOrDefault();
                if (job is null)
                {
                    await _signal.WaitAsync(ct);
                    continue;
                }
                await ProcessAsync(job, ct);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    private async Task ProcessAsync(TranscriptionJob job, CancellationToken ct)
    {
        var stage = "Transcription";
        try
        {
            bool ok;
            if (_adopted.Contains(job.Id))
            {
                while (job.Pid is { } pid && processAlive(pid, job.Started)) await _delay(PollInterval, ct);
                _adopted.Remove(job.Id);
                ok = File.Exists(Path.Combine(job.Dir, "meeting.md"));
                if (!ok)
                {
                    // The adopted run died without output: report it, then run the job afresh.
                    notifier.Notify($"Transcription interrupted: {job.Name}", $"Video is safe in {job.Dir}", Urgency.Normal);
                    Modify(job.Id, j => j with { Pid = null, Started = null });
                    return;
                }
            }
            else
            {
                var code = await transcriber.RunAsync(job, pid =>
                {
                    var now = DateTimeOffset.Now;
                    Modify(job.Id, j => j with { Pid = pid, Started = now });
                }, ct);
                ok = code == 0;
            }

            if (ok)
            {
                stage = "Hand-off";
                // A notifier that throws must not cost the hand-off.
                var toName = SpeakerCount.ToName(job.Dir);
                TryNotify($"Transcript ready: {job.Name}",
                    Path.Combine(job.Dir, "meeting.md") + (toName == 0 ? "" : $" · {toName} {(toName == 1 ? "voice" : "voices")} to name"),
                    Urgency.Normal);
                if (!string.IsNullOrWhiteSpace(job.HandOff))
                    await handOff.RunAsync(job.HandOff, job.Dir, job.Name, ct);
            }
            else
            {
                NotifyFailed(job);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // the job stays persisted for the next start
        }
        catch (Exception e)
        {
            // A poison job must not stop the queue.
            log.Info($"{stage} of {job.Name} failed: {e}");
            if (stage == "Transcription") NotifyFailed(job);
            else TryNotify($"Hand-off failed: {job.Name}", $"The meeting is untouched in {job.Dir}; see handoff.log", Urgency.Critical);
        }
        Remove(job.Id);
    }

    private void NotifyFailed(TranscriptionJob job) =>
        TryNotify($"Transcription failed: {job.Name}", $"Video is safe in {job.Dir}; see transcribe.log", Urgency.Critical);

    private void TryNotify(string headline, string body, Urgency urgency)
    {
        try { notifier.Notify(headline, body, urgency); }
        catch (Exception e) { log.Info($"notification failed: {e.Message}"); }
    }

    private void Modify(Guid id, Func<TranscriptionJob, TranscriptionJob> change) =>
        store.Update(s =>
        {
            var i = s.Jobs.FindIndex(j => j.Id == id);
            return i < 0 ? s : s with { Jobs = s.Jobs.SetItem(i, change(s.Jobs[i])) };
        });

    private void Remove(Guid id) => store.Update(s => s with { Jobs = s.Jobs.RemoveAll(j => j.Id == id) });
}
