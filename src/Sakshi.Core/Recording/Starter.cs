using System.Text.Json;
using Sakshi.Core.Config;
using Sakshi.Core.Obs;
using Sakshi.Core.Ports;
using Sakshi.Core.State;
using Sakshi.Core.Store;

namespace Sakshi.Core.Recording;

/// <summary>
/// Starting a recording. Port of cmd_start. The ObsClient it connects is disposed on every path,
/// and the capture never stays at Starting after a failure or cancellation. The reminder is not
/// started here: the tray controller owns it. Starter does no locking of its own: the tray
/// controller's single gate serialises start, stop and import.
/// </summary>
public sealed class Starter(
    SakshiConfig config,
    StateStore store,
    MeetingStore meetings,
    IObsLauncher launcher,
    IWindowCapture capture,
    INotifier notifier,
    IClock clock,
    ILog log,
    Func<CancellationToken, Task<ObsClient>> connect,
    Func<bool> obsRunning,
    Func<TimeSpan, CancellationToken, Task>? delay = null,
    SakshiObsSettings? obsSettings = null,
    Func<DateTimeOffset?>? obsStartTime = null,
    IPickerWait? pickerWait = null)
{
    private sealed class StartFailed(string message) : Exception(message);

    private const string UnnamedWaiting = "An unnamed recording is waiting — choose Name recording in the Sakshi menu to name and store it.";

    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(250);

    public async Task<int> StartAsync(CancellationToken ct)
    {
        var starting = false;
        try
        {
            return await RunAsync(ct, () => starting = true, () => starting = false);
        }
        catch (OperationCanceledException)
        {
            if (starting) SetIdle();
            throw;
        }
        catch (Exception e)
        {
            if (starting) SetIdle();
            log.Info(e.ToString());
            return Die(e is InvalidOperationException or TimeoutException or ObsRequestException or StartFailed
                ? e.Message
                : $"Could not start recording: {e.Message} (see sakshi.log)");
        }
        finally
        {
            pickerWait?.Done();
        }
    }

    /// <summary>
    /// The system's picker comes only after OBS starts and Sakshi switches it, seconds later: say so from then on. Not
    /// while Sakshi may still ask whether to switch an OBS the user had open (the sign would cover the question).
    /// </summary>
    private void PickerOnItsWay()
    {
        if (capture.OpensSystemPicker) pickerWait?.Opening();
    }

    private async Task<int> RunAsync(CancellationToken ct, Action markStarting, Action markIdle)
    {
        // A start would overwrite the capture (on the outputActive branch too) and lose the recording's
        // place in the naming flow: the stop names and stores it first, as bash's import refused.
        if (store.Load().Capture is { Phase: CapturePhase.Naming, Video: { } waiting } && File.Exists(waiting))
            return Die(UnnamedWaiting);

        if (store.Load().Capture.Phase == CapturePhase.Recording)
        {
            if (obsRunning())
            {
                notifier.Notify("Already recording", "Click again to stop.", Urgency.Low);
                return 0;
            }
            log.Info("state said recording but OBS is not running; starting afresh");
            SetIdle(); // stale
        }

        try
        {
            var busy = store.Load().Jobs.Select(j => j.Dir).ToList();
            meetings.Housekeep(config.KeepDays, DateOnly.FromDateTime(clock.Now.LocalDateTime), busy, dryRun: false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.Info($"housekeep: failed, carrying on ({e.Message})");
        }

        var wasRunning = obsRunning();
        if (!wasRunning) PickerOnItsWay();
        await launcher.EnsureRunningAsync(ct);
        if (!wasRunning) store.Update(s => s with { ObsLaunched = obsStartTime?.Invoke() });
        await using var obs = await connect(ct);

        var status = await obs.RequestAsync("GetRecordStatus", null, ct);
        if (status.TryGetProperty("outputActive", out var active) && active.ValueKind == JsonValueKind.True)
        {
            var now = clock.Now;
            store.Update(s => s with { Capture = new Capture(CapturePhase.Recording, now) });
            notifier.Notify("Already recording", "OBS was already recording; tracking it.", Urgency.Low);
            return 0;
        }

        if (obsSettings is null) return await RecordAsync(obs, ct, markStarting, markIdle);

        // An OBS the user opened themselves is switched to Sakshi's settings only if they agree.
        var ours = store.Load().ObsLaunched is { } launched && obsStartTime?.Invoke() is { } running
            && (running - launched).Duration() < TimeSpan.FromSeconds(1);
        if (!await obsSettings.EnterAsync(obs, ask: wasRunning && !ours, ct)) return 0;
        PickerOnItsWay();
        try
        {
            return await RecordAsync(obs, ct, markStarting, markIdle);
        }
        catch
        {
            await obsSettings.LeaveAsync(obs, CancellationToken.None);
            throw;
        }
    }

    private async Task<int> RecordAsync(ObsClient obs, CancellationToken ct, Action markStarting, Action markIdle)
    {
        try
        {
            await obs.RequestAsync("SetCurrentProgramScene", new { sceneName = config.ObsScene }, ct);
        }
        catch (ObsRequestException) when (obsSettings is not null)
        {
            // Sakshi's own collection: a new one has OBS's default scene, named in the user's language.
            await obs.RequestAsync("CreateScene", new { sceneName = config.ObsScene }, ct);
            await obs.RequestAsync("SetCurrentProgramScene", new { sceneName = config.ObsScene }, ct);
        }
        catch (ObsRequestException)
        {
            throw new StartFailed($"scene '{config.ObsScene}' not found in OBS");
        }
        if (obsSettings is not null) await obsSettings.EnsureSceneAudioAsync(obs, config.ObsScene, ct);

        var itemId = await GetItemIdAsync(obs, ct);
        var justCreated = false;
        if (itemId is null)
        {
            justCreated = true;
            var notFound = $"source '{config.ObsSource}' not found in scene '{config.ObsScene}'";
            log.Info($"source '{config.ObsSource}' missing in scene '{config.ObsScene}'; creating it");
            try
            {
                await obs.RequestAsync("CreateInput", new
                {
                    sceneName = config.ObsScene,
                    inputName = config.ObsSource,
                    inputKind = capture.InputKind,
                    inputSettings = capture.NewSourceSettings,
                }, ct);
            }
            catch (ObsRequestException e) when (e.Code == 601)
            {
                // The input exists in another scene: add it to this one.
                try
                {
                    await obs.RequestAsync("CreateSceneItem",
                        new { sceneName = config.ObsScene, sourceName = config.ObsSource }, ct);
                }
                catch (ObsRequestException)
                {
                    throw new StartFailed(notFound);
                }
            }
            catch (ObsRequestException)
            {
                throw new StartFailed(notFound);
            }
            itemId = await GetItemIdAsync(obs, ct)
                ?? throw new StartFailed($"source '{config.ObsSource}' not found in scene '{config.ObsScene}'");
        }

        store.Update(s => s with { Capture = new Capture(CapturePhase.Starting, clock.Now) });
        markStarting();

        // The capture knows whether this platform needs a pick (window IDs do not survive restarts on
        // macOS and Windows; PipeWire opens its own picker for a new source).
        if (!await capture.SelectWindowAsync(obs, config.ObsScene, config.ObsSource, justCreated, ct))
            throw new StartFailed("No window selected — recording not started.");
        // A reload may have recreated every source; look the item up again.
        itemId = await GetItemIdAsync(obs, ct)
            ?? throw new StartFailed($"source '{config.ObsSource}' vanished after reload");

        // Wait until the capture has a real size, then fit it to the canvas.
        var vs = await obs.RequestAsync("GetVideoSettings", null, ct);
        var baseW = vs.GetProperty("baseWidth").GetInt32();
        var baseH = vs.GetProperty("baseHeight").GetInt32();
        long srcW = 0, srcH = 0;
        for (var i = 0; i < 40; i++)
        {
            try
            {
                var xf = await obs.RequestAsync("GetSceneItemTransform",
                    new { sceneName = config.ObsScene, sceneItemId = itemId.Value }, ct);
                var t = xf.GetProperty("sceneItemTransform");
                srcW = (long)Math.Floor(Num(t, "sourceWidth"));
                srcH = (long)Math.Floor(Num(t, "sourceHeight"));
            }
            catch (Exception e) when (e is ObsRequestException or KeyNotFoundException)
            {
                break;
            }
            if (srcW > 0 && srcH > 0) break;
            if (i < 39) await _delay(Poll, ct);
        }
        if (srcW <= 0 || srcH <= 0) throw new StartFailed("No window selected — recording not started.");
        if (obsSettings is not null) (baseW, baseH) = await FitFrameAsync(obs, srcW, srcH, baseW, baseH, ct);

        try
        {
            await obs.RequestAsync("SetSceneItemTransform", new
            {
                sceneName = config.ObsScene,
                sceneItemId = itemId.Value,
                sceneItemTransform = new
                {
                    positionX = 0, positionY = 0, rotation = 0, alignment = 5, scaleX = 1, scaleY = 1,
                    cropLeft = 0, cropTop = 0, cropRight = 0, cropBottom = 0,
                    boundsType = "OBS_BOUNDS_SCALE_INNER", boundsAlignment = 0,
                    boundsWidth = baseW, boundsHeight = baseH,
                },
            }, ct);
        }
        catch (ObsRequestException)
        {
            log.Info("warning: fit-to-canvas transform failed");
        }

        ct.ThrowIfCancellationRequested();
        try
        {
            await obs.RequestAsync("StartRecord", null, CancellationToken.None);
        }
        catch (ObsRequestException)
        {
            throw new StartFailed("OBS refused to start recording");
        }

        var started = clock.Now;
        var size = $"{srcW}x{srcH}";
        store.Update(s => s with { Capture = new Capture(CapturePhase.Recording, started, SourceSize: size) });
        markIdle();
        notifier.Notify("Recording started", $"{srcW}×{srcH} → {baseW}×{baseH}. Click the bar icon to stop.", Urgency.Low);
        return 0;
    }

    /// <summary>
    /// Sakshi's profile only: records the window at its own size (scaled down to the configured maximum), so there
    /// are no bars and nothing is enlarged. OBS allows it only while nothing is being output; else the frame stays.
    /// </summary>
    private async Task<(int Width, int Height)> FitFrameAsync(ObsClient obs, long srcW, long srcH, int baseW, int baseH, CancellationToken ct)
    {
        var (w, h) = SakshiObsProfile.Frame(srcW, srcH, config.RecordMaxWidth, config.RecordMaxHeight);
        if ((w, h) == (baseW, baseH)) return (w, h);
        try
        {
            await obs.RequestAsync("SetVideoSettings",
                new { baseWidth = w, baseHeight = h, outputWidth = w, outputHeight = h }, ct);
            return (w, h);
        }
        catch (ObsRequestException e)
        {
            log.Info($"could not size the recording to the window ({e.Message}); keeping {baseW}×{baseH}");
            return (baseW, baseH);
        }
    }

    private static double Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;

    private async Task<int?> GetItemIdAsync(ObsClient obs, CancellationToken ct)
    {
        try
        {
            var r = await obs.RequestAsync("GetSceneItemId",
                new { sceneName = config.ObsScene, sourceName = config.ObsSource }, ct);
            return r.GetProperty("sceneItemId").GetInt32();
        }
        catch (ObsRequestException)
        {
            return null;
        }
    }

    private void SetIdle() => store.Update(s => s with { Capture = Capture.Idle });

    private int Die(string message)
    {
        log.Info(message);
        notifier.Notify("Sakshi", message, Urgency.Critical);
        return 1;
    }
}
