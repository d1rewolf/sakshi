using Sakshi.Core.Config;
using Sakshi.Core.Naming;
using Sakshi.Core.Obs;
using Sakshi.Core.Ports;
using Sakshi.Core.State;
using Sakshi.Core.Store;
using Sakshi.Core.Transcription;

namespace Sakshi.Core.Recording;

/// <summary>
/// Stopping a recording and importing an existing video: the code that makes sure a
/// recording is never lost. Port of cmd_stop, store_and_transcribe and cmd_import.
///
/// Invariants, in order of importance:
///   1. Nothing here ever overwrites a recording, and the only path that removes one is
///      "Don't save" answered twice, which moves the file to the trash. Moves leave the
///      source untouched unless the copy succeeded (MeetingStore.StoreVideo).
///   2. The moment OBS stops, the state points at the video. Every later failure (prompt,
///      mkdir, move) keeps the phase at Naming, so the next stop resumes from the file,
///      not from OBS.
///   3. The name prompt cannot block forever (its timeout) and a stray click that dismisses
///      it gets a second prompt before the default name is used.
///   4. The transcription job is queued before the capture goes idle. A crash between the
///      two leaves Naming with a video path that no longer exists, which the next stop clears.
///
/// One Recorder serialises its own stop and import calls; coordinating separate processes
/// is the caller's job.
/// </summary>
public sealed class Recorder(
    SakshiConfig config,
    StateStore store,
    MeetingStore meetings,
    IPrompter prompter,
    INotifier notifier,
    ITrash trash,
    IClock clock,
    ILog log,
    TranscriptionQueue queue,
    Func<CancellationToken, Task<IObs?>> connectObs,
    Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    private static readonly TimeSpan RecentWindow = TimeSpan.FromMinutes(10);

    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;
    private readonly SemaphoreSlim _busy = new(1, 1);

    public async Task<int> StopAsync(string? presetName, CancellationToken ct)
    {
        if (!await _busy.WaitAsync(0, ct))
        {
            if (store.Load().Capture.Phase == CapturePhase.Naming)
            {
                notifier.Notify("Sakshi", "Type the meeting name in the prompt and press Enter.", Urgency.Low);
                return 0;
            }
            return Die("a start or stop is already in progress");
        }
        try
        {
            return await StopLockedAsync(presetName, ct);
        }
        finally
        {
            _busy.Release();
        }
    }

    public async Task<int> ImportAsync(string video, string? presetName, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(video)) return Die("usage: sakshi import <video> [name]");
        if (!File.Exists(video)) return Die($"No such file: {video}");
        var src = Path.GetFullPath(video);

        if (!await _busy.WaitAsync(0, ct)) return Die("a start or stop is already in progress");
        try
        {
            switch (store.Load().Capture.Phase)
            {
                case CapturePhase.Starting or CapturePhase.Recording:
                    return Die("A recording is in progress — stop it before importing.");
                case CapturePhase.Naming:
                    return Die("An unnamed recording is waiting — run 'sakshi stop' to store it first.");
            }

            var mtime = new DateTimeOffset(File.GetLastWriteTime(src));
            var defaultName = MeetingName.ImportDefault(Path.GetFileName(src), mtime);
            var name = string.IsNullOrEmpty(presetName) ? "" : MeetingName.Sanitize(presetName);

            store.Update(s => s with { Capture = new Capture(CapturePhase.Naming, Video: src, Stopped: mtime) });
            log.Info($"importing: {src}");
            return await StoreAndQueueAsync(src, name, defaultName, mtime, ct);
        }
        finally
        {
            _busy.Release();
        }
    }

    private async Task<int> StopLockedAsync(string? presetName, CancellationToken ct)
    {
        string video;
        string name = "";
        DateTimeOffset stopped;

        var capture = store.Load().Capture;
        if (capture.Phase == CapturePhase.Naming && capture.Video is { } waiting && File.Exists(waiting))
        {
            // A previous stop ended the recording but did not finish storing it.
            video = waiting;
            name = capture.Name ?? "";
            stopped = capture.Stopped ?? clock.Now;
            log.Info($"resuming unnamed recording: {video}");
        }
        else
        {
            var found = await StopObsAsync(ct);
            if (found.Exit is { } exit) return exit;
            (video, stopped) = (found.Video!, found.At);
        }

        var local = stopped.ToLocalTime();
        var defaultName = MeetingName.DefaultFor(local);
        if (name.Length == 0 && !string.IsNullOrEmpty(presetName)) name = MeetingName.Sanitize(presetName);

        // Keep or discard? Only asked while the name is still open: a preset name or a
        // retry after a failed move has already decided to keep it.
        if (name.Length == 0 && !await PromptKeepAsync(ct))
            return DiscardRecording(video);

        return await StoreAndQueueAsync(video, name, defaultName, stopped, ct);
    }

    private readonly record struct ObsStop(string? Video, DateTimeOffset At, int? Exit);

    private async Task<ObsStop> StopObsAsync(CancellationToken ct)
    {
        IObs? obs;
        try
        {
            obs = await connectObs(ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.Info($"OBS connect failed: {e.Message}");
            return new(null, default, Die($"OBS WebSocket port is closed ({e.Message})"));
        }
        if (obs is null)
        {
            ClearCapture();
            return new(null, default, Die("OBS is not running"));
        }

        try
        {
            bool recording;
            try
            {
                recording = await obs.IsRecordingAsync(ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.Info($"GetRecordStatus failed: {e.Message}");
                return new(null, default, Die($"Could not ask OBS whether it is recording ({e.Message})."));
            }
            if (!recording)
            {
                ClearCapture();
                notifier.Notify("Not recording", "OBS has no active recording.", Urgency.Low);
                return new(null, default, 0);
            }

            // From here until the state points at the file nothing may be cancelled: a cancel
            // after OBS stopped would leave the state at Recording and the video forgotten.
            // StopRecord itself is included, as OBS may stop before its reply is read.
            var none = CancellationToken.None;
            string? reported;
            try
            {
                reported = await obs.StopRecordAsync(none);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.Info($"StopRecord failed: {e.Message}");
                return new(null, default, Die("OBS refused to stop recording"));
            }
            var stopped = clock.Now;

            string? recDir = null;
            var video = reported;
            if (video is null || !File.Exists(video))
            {
                try { recDir = await obs.GetRecordDirectoryAsync(none); }
                catch (Exception e) when (e is not OperationCanceledException) { log.Info($"GetRecordDirectory failed: {e.Message}"); }
                log.Info($"OBS reported no output path; looking in {(string.IsNullOrEmpty(recDir) ? "?" : recDir)}");
                await _delay(TimeSpan.FromSeconds(1), none);
                video = FindRecentRecording(recDir);
            }

            if (video is null || !File.Exists(video))
            {
                ClearCapture();
                return new(null, default, Die(
                    $"Recording stopped but no file was found. Check OBS's recording folder ({(string.IsNullOrEmpty(recDir) ? "unknown" : recDir)})."));
            }

            // The recording exists. Point at it before anything else can go wrong.
            // A fresh capture: a Name, Frames or hand-off left by a crashed earlier stop must not carry over.
            store.Update(s => s with { Capture = new Capture(CapturePhase.Naming, Started: s.Capture.Started, Video: video, Stopped: stopped) });
            log.Info($"recording stopped: {video}");
            return new(video, stopped, null);
        }
        finally
        {
            await obs.DisposeAsync();
        }
    }

    /// <summary>Newest video written in the last 10 minutes in OBS's record directory.</summary>
    private string? FindRecentRecording(string? dir) => RecordingFiles.NewestSince(dir, clock.Now.UtcDateTime - RecentWindow);

    // ---------------------------------------------------------------- prompts

    private async Task<string> PromptNameAsync(string defaultName, CancellationToken ct)
    {
        var timeout = TimeSpan.FromSeconds(config.NamePromptTimeout);
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var label = attempt == 2 ? $"Meeting name (Esc again keeps {defaultName})" : "Meeting name";
            string? typed = null;
            try { typed = await prompter.AskTextAsync(label, timeout, ct); }
            catch (Exception e) when (e is not OperationCanceledException) { log.Info($"name prompt failed: {e.Message}"); }
            var name = MeetingName.Sanitize(typed ?? "");
            if (name.Length > 0) return name;
            log.Info($"name prompt dismissed (attempt {attempt})");
            await _delay(TimeSpan.FromMilliseconds(500), ct);
        }
        return defaultName;
    }

    /// <returns>true = save the recording, false = discard it. Discarding takes two explicit answers.</returns>
    private async Task<bool> PromptKeepAsync(CancellationToken ct)
    {
        if (!config.KeepPrompt) return true;
        var timeout = TimeSpan.FromSeconds(config.KeepPromptTimeout);

        var pick = await ChooseAsync("Recording stopped",
        [
            new PromptRow("\U000F0193", "Save it", "Name it and transcribe"),
            new PromptRow("\U000F01B4", "Don't save", "Throw this recording away"),
        ], timeout, ct);
        if (pick != "Don't save")
        {
            if (string.IsNullOrEmpty(pick)) log.Info("keep prompt dismissed; saving");
            return true;
        }

        pick = await ChooseAsync("Delete this recording?",
        [
            new PromptRow("\U000F0193", "Keep it", "Go back to naming it"),
            new PromptRow("\U000F01B4", "Yes, delete it", $"Moves the video to {TrashName}"),
        ], timeout, ct);
        if (pick == "Yes, delete it")
        {
            log.Info("discard confirmed");
            return false;
        }
        log.Info($"discard not confirmed ({(string.IsNullOrEmpty(pick) ? "dismissed" : pick)}); saving");
        return true;
    }

    /// <summary>A menu that throws counts as dismissed: it must never stop a recording from being stored.</summary>
    private async Task<string?> ChooseAsync(string title, IReadOnlyList<PromptRow> rows, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            return await prompter.ChooseAsync(title, rows, timeout, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.Info($"prompt '{title}' failed: {e.Message}");
            return null;
        }
    }

    /// <summary>The chosen frame mode; never fails. Anything but a known label means the configured TranscribeArgs.</summary>
    private async Task<FrameChoice> PromptFramesAsync(CancellationToken ct)
    {
        var fallback = new FrameChoice(config.TranscribeArgs, null);
        if (!config.FramesPrompt) return fallback;

        var rows = config.FramesModes.Select(m => new PromptRow(m.Glyph, m.Label, m.Description)).ToList();
        var pick = await ChooseAsync("Frames to keep", rows, TimeSpan.FromSeconds(config.FramesPromptTimeout), ct);
        if (string.IsNullOrEmpty(pick))
        {
            log.Info("frame prompt dismissed; using TRANSCRIBE_ARGS");
            return fallback;
        }
        var mode = config.FramesModes.FirstOrDefault(m => m.Label == pick);
        if (mode is null)
        {
            log.Info($"unknown frame mode from the menu: {pick}");
            return fallback;
        }
        return new FrameChoice(mode.Args, string.IsNullOrWhiteSpace(mode.HandOff) ? null : mode.HandOff);
    }

    // ---------------------------------------------------------------- tail

    private int DiscardRecording(string video)
    {
        try
        {
            trash.MoveToTrash(video);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.Info($"trash failed: {e.Message}");
            return Die($"Could not move the recording to {TrashName}. It is still at {video} — click stop again to retry.");
        }
        log.Info($"discarded: {video}");
        ClearCapture();
        notifier.Notify("Recording discarded", $"Moved to {TrashName}: {Path.GetFileName(video)}", Urgency.Low);
        return 0;
    }

    /// <summary>
    /// The shared tail of stop and import: ask for a name (when empty) and a frame mode, move
    /// the video to meetings/day/name/name.ext and queue its transcription. The caller has
    /// already written Capture = Naming with the video, so a failure anywhere here leaves a
    /// resumable state, and both answers go into it, so a retry never asks twice.
    /// </summary>
    private async Task<int> StoreAndQueueAsync(string video, string name, string defaultName, DateTimeOffset stopped, CancellationToken ct)
    {
        var day = DayFolder.For(stopped.ToLocalTime());
        if (name.Length == 0) name = await PromptNameAsync(defaultName, ct);
        if (name.Length == 0) name = defaultName;

        var frames = store.Load().Capture.Frames ?? await PromptFramesAsync(ct);
        store.Update(s => s with { Capture = s.Capture with { Name = name, Frames = frames } });

        if (!File.Exists(video))
        {
            ClearCapture();
            return Die($"The recording file disappeared before it could be stored: {video}");
        }
        await MeetingStore.WaitForSettledAsync(video, _delay, ct);
        // WaitForSettled returns quietly when cancelled. A Quit must not move a file OBS may
        // still be finalising: stay at Naming, so the next stop resumes.
        ct.ThrowIfCancellationRequested();

        // On any failure the file stays where it is and the state keeps pointing at it; the
        // next stop retries without asking again.
        string stored;
        try
        {
            stored = meetings.StoreVideo(video, day, name);
        }
        catch (StoreFailedException e)
        {
            log.Info(e.Message);
            return Die($"Could not move the recording into {e.Dir}. It is safe at {video} — click stop again to retry.");
        }

        var dir = Path.GetDirectoryName(stored)!;
        var finalName = Path.GetFileName(dir);
        queue.Enqueue(new TranscriptionJob(Guid.NewGuid(), dir, stored, finalName, frames.Args, frames.HandOff));
        notifier.Notify($"Saved {finalName}", "Transcribing in the background…", Urgency.Low);
        ClearCapture();
        return 0;
    }

    private void ClearCapture() => store.Update(s => s with { Capture = Capture.Idle });

    private int Die(string message)
    {
        log.Info($"sakshi: {message}");
        notifier.Notify("Sakshi", message, Urgency.Critical);
        return 1;
    }

    /// <summary>What the system calls the place deleted files go.</summary>
    public static string TrashName =>
        OperatingSystem.IsWindows() ? "the Recycle Bin" : OperatingSystem.IsMacOS() ? "the Trash" : "the trash";
}
