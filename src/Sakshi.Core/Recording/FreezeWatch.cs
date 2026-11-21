using System.Text.Json;
using Sakshi.Core.Config;
using Sakshi.Core.Obs;
using Sakshi.Core.Ports;

namespace Sakshi.Core.Recording;

/// <summary>
/// Notices a window capture that stopped receiving frames during a recording and mends it.
///
/// The capture counts as frozen when its picture has not changed for <see cref="StillFor"/> and no open
/// window has the size it reports: a frozen stream stays at a size from before the window last changed,
/// while a still slide keeps its window's size and is left alone. A frozen capture is reconnected once,
/// silently; if it does not come back to life, the user is told once to re-pick the window. Another
/// reconnect waits until the picture has been seen changing again, so a capture that stays dead costs
/// one gap in the recording, not one every few seconds, and a recording gets at most
/// <see cref="MaxReconnects"/> (each costs about 2 s of sound). A source with no size (the window was
/// closed, or a pick was dismissed) is not reconnected: without a window to return to, the portal
/// would open its picker mid-meeting, so the user is told to re-pick instead.
///
/// Known gap: the size is compared with every open window, so a frozen capture whose stale size
/// happens to equal another window's (two windows tiled alone on their workspaces) goes unnoticed.
///
/// One instance per tray; the caller calls <see cref="CheckAsync"/> every <see cref="Poll"/> while
/// recording and <see cref="Reset"/> when a recording starts or ends. Not thread-safe.
/// </summary>
public sealed class FreezeWatch(SakshiConfig config, ICaptureRecovery recovery, INotifier notifier, ILog log, IClock clock)
{
    public static readonly TimeSpan Poll = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan StillFor = TimeSpan.FromSeconds(10);
    public const int MaxReconnects = 3;

    // Small, so the check is cheap; still fine enough to see a clock or a speaker's face change.
    private const int ShotWidth = 320;

    internal const string ReconnectedHeadline = "Screen capture froze";
    internal const string ReconnectedBody = "Sakshi reconnected it; about 2 seconds of the recording are lost.";
    internal const string StuckHeadline = "Screen capture is frozen";
    internal const string StuckBody = "The recording is not getting new pictures. Choose Re-pick window in the Sakshi menu.";

    private string? _picture;          // null: no baseline yet
    private DateTimeOffset _since;     // when _picture was first seen
    private bool _liveSinceReconnect = true;
    private bool _warned;
    private int _reconnects;

    /// <summary>Forgets everything seen: the next check takes a fresh baseline.</summary>
    public void Reset()
    {
        _picture = null;
        _liveSinceReconnect = true;
        _warned = false;
        _reconnects = 0;
    }

    /// <summary>One check. OBS and hyprctl failures propagate; nothing is reconnected when they happen.</summary>
    public async Task CheckAsync(ObsClient obs, CancellationToken ct)
    {
        // The state file can say "recording" after OBS stopped on its own: never reload sources then.
        var status = await obs.RequestAsync("GetRecordStatus", null, ct);
        if (status.ValueKind == JsonValueKind.Object && status.TryGetProperty("outputActive", out var active)
            && active.ValueKind == JsonValueKind.False)
            return;

        var now = clock.Now;
        var picture = await PictureAsync(obs, ct);
        if (_picture is null || picture != _picture)
        {
            if (_picture is not null)
            {
                _liveSinceReconnect = true;
                _warned = false;
            }
            _picture = picture;
            _since = now;
            return;
        }
        if (now - _since < StillFor) return;

        var size = await SourceSizeAsync(obs, ct);
        if ((await recovery.WindowSizesAsync(ct)).Contains(size)) return; // a still picture of a live window

        if (_liveSinceReconnect && _reconnects < MaxReconnects && size != default)
        {
            log.Info($"capture: no new picture for {(int)(now - _since).TotalSeconds} s and no window is {size.Width}x{size.Height}; reconnecting");
            // The attempt counts even when it fails: a failing reconnect is not retried every few seconds.
            _liveSinceReconnect = false;
            _reconnects++;
            _picture = null; // the reloaded source's first picture is the new baseline, not a sign of life
            try
            {
                await recovery.ReconnectAsync(obs, ct);
            }
            catch (InvalidOperationException e)
            {
                log.Info($"capture: reconnect failed: {e.Message}");
                Warn();
                return;
            }
            notifier.Notify(ReconnectedHeadline, ReconnectedBody, Urgency.Normal);
            return;
        }
        if (_warned) return;
        log.Info($"capture: frozen at {size.Width}x{size.Height} and not reconnecting ({_reconnects} reconnect(s) so far); asking for a re-pick");
        Warn();
    }

    private void Warn()
    {
        notifier.Notify(StuckHeadline, StuckBody, Urgency.Critical);
        _warned = true;
    }

    // A source that cannot be rendered (no size, gone) has the same empty picture every time: it counts as still.
    private async Task<string> PictureAsync(ObsClient obs, CancellationToken ct)
    {
        try
        {
            var r = await obs.RequestAsync("GetSourceScreenshot",
                new { sourceName = config.ObsSource, imageFormat = "png", imageWidth = ShotWidth }, ct);
            return r.TryGetProperty("imageData", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString()! : "";
        }
        catch (ObsRequestException)
        {
            return "";
        }
    }

    private async Task<PixelSize> SourceSizeAsync(ObsClient obs, CancellationToken ct)
    {
        try
        {
            var id = await obs.RequestAsync("GetSceneItemId", new { sceneName = config.ObsScene, sourceName = config.ObsSource }, ct);
            var xf = await obs.RequestAsync("GetSceneItemTransform",
                new { sceneName = config.ObsScene, sceneItemId = id.GetProperty("sceneItemId").GetInt32() }, ct);
            var t = xf.GetProperty("sceneItemTransform");
            return new PixelSize((int)Math.Floor(Num(t, "sourceWidth")), (int)Math.Floor(Num(t, "sourceHeight")));
        }
        catch (Exception e) when (e is ObsRequestException or KeyNotFoundException)
        {
            return new PixelSize(0, 0);
        }
    }

    private static double Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
}
