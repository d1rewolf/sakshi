using System.Text.Json;
using System.Text.Encodings.Web;
using System.Text.Json.Nodes;
using Sakshi.Core.Config;
using Sakshi.Core.Obs;
using Sakshi.Core.Paths;
using Sakshi.Core.Ports;

namespace Sakshi.Platform.Linux;

/// <summary>
/// Re-picks the captured window through the compositor's share picker. Port of reselect_window.
///
/// A PipeWire source talks to the screen-sharing portal only when OBS creates it on its UI thread:
/// one created over obs-websocket (CreateInput, or the "Reload" button) sends its request but never
/// handles the portal's reply, so no picker appears and no frames arrive. Scene collection switches
/// do run on the UI thread. So every (re)start of the source is a hop: to a scratch collection (OBS
/// writes the current one to disk), back again, and OBS recreates the source. Stripping the
/// PipeWire RestoreToken from the file in between makes the compositor show its picker; keeping it
/// reconnects to the same window without asking (<see cref="ReconnectAsync"/>).
/// </summary>
public sealed class PipeWireWindowCapture(
    SakshiConfig config,
    SakshiPaths paths,
    IProcessRunner runner,
    INotifier notifier,
    ILog log,
    Func<TimeSpan, CancellationToken, Task> delay,
    Func<bool>? pickerRunning = null,
    Func<string, string?>? env = null,
    IPickerWait? pickerWait = null,
    Func<bool>? pickerVisible = null) : IWindowCapture
{
    private const string Scratch = "sakshi-scratch";
    private const string Kind = "pipewire-screen-capture-source";

    private readonly Func<bool> _pickerRunning = pickerRunning ?? (() =>
        OperatingSystem.IsLinux()
            ? ProcCmdline.AnyArgv0Named("/proc", config.PickerBin)
            : runner.IsRunning(config.PickerBin));

    /// <summary>The default picker's layer-shell namespace: it draws as an overlay layer, not as a window.</summary>
    private const string PickerLayer = "ch.wysbd.hyprland-preview-share-picker";

    // The process starts seconds before its overlay shows (it previews every window first): the overlay is what the user sees.
    private readonly Func<bool> _pickerVisible = pickerVisible ?? (() => HyprlandLayerShown(runner, PickerLayer));

    private static bool HyprlandLayerShown(IProcessRunner runner, string ns)
    {
        try
        {
            return runner.RunAsync("hyprctl", ["-j", "layers"], TimeSpan.FromSeconds(2), CancellationToken.None)
                .GetAwaiter().GetResult().StdOut.Contains($"\"{ns}\"", StringComparison.Ordinal);
        }
        catch (Exception) { return false; }   // not knowing only keeps the sign up a little longer
    }

    public bool OpensSystemPicker => UnderHyprland && config.ObsReselectWindow;

    /// <summary>Whether this session runs Hyprland (its share picker, hyprctl).</summary>
    public bool UnderHyprland => !string.IsNullOrEmpty((env ?? Environment.GetEnvironmentVariable)("HYPRLAND_INSTANCE_SIGNATURE"));

    public string InputKind => Kind;

    public object NewSourceSettings => new { };

    public async Task<bool> SelectWindowAsync(ObsClient obs, string scene, string source, bool sourceJustCreated, CancellationToken ct)
    {
        // A source created just now has no token yet: reloading it on the UI thread is what opens the picker.
        // Not Hyprland-specific: the lost reply is OBS's, before it even asks the portal which window
        // (watched on D-Bus: CreateSession answered, no SelectSources sent), so no dialog would appear on GNOME either.
        if (sourceJustCreated)
        {
            await HopAsync(obs, stripToken: false, ct);
            await WaitForPickAsync(obs, scene, source, ct);
            return true;
        }
        if (!config.ObsReselectWindow) return true;
        await RepickAsync(obs, scene, source, ct);
        return true;
    }

    /// <summary>
    /// Makes the user pick the window again, whatever <see cref="SakshiConfig.ObsReselectWindow"/> says, and
    /// returns once they have (or the wait gave up). Safe while recording: the recording carries on.
    /// </summary>
    public async Task RepickAsync(ObsClient obs, string scene, string source, CancellationToken ct)
    {
        await HopAsync(obs, stripToken: true, ct);
        await WaitForPickAsync(obs, scene, source, ct);
    }

    /// <summary>
    /// Restarts the source on the window it already captures, without a picker: the portal accepts the
    /// RestoreToken OBS saved. Mends a capture that stopped receiving frames. Safe while recording; the
    /// recording loses about two seconds of picture and sound while OBS reloads its sources.
    /// </summary>
    public Task ReconnectAsync(ObsClient obs, CancellationToken ct) => HopAsync(obs, stripToken: false, ct);

    private async Task HopAsync(ObsClient obs, bool stripToken, CancellationToken ct)
    {
        var collection = await CurrentCollectionAsync(obs, ct);
        // An earlier hop failed to switch back: hopping again would "return" to the empty scratch collection.
        if (collection == Scratch)
            throw new InvalidOperationException($"OBS is on Sakshi's empty '{Scratch}' scene collection; switch back to yours in OBS (Scene Collection menu)");
        var path = "";
        if (stripToken)
        {
            path = CollectionFile(collection) ?? "";
            if (path.Length == 0 || !File.Exists(path))
                throw new InvalidOperationException($"cannot find the scene collection file for '{collection}'");
        }

        try
        {
            await obs.RequestAsync("CreateSceneCollection", new { sceneCollectionName = Scratch }, ct);
        }
        catch (ObsRequestException)
        {
            try
            {
                await obs.RequestAsync("SetCurrentSceneCollection", new { sceneCollectionName = Scratch }, ct);
            }
            catch (ObsRequestException)
            {
                throw new InvalidOperationException("could not switch to the scratch scene collection");
            }
        }
        var bodyFailed = false;
        try
        {
            if (!await WaitCollectionAsync(obs, Scratch, ct))
                throw new InvalidOperationException("OBS did not switch to the scratch scene collection");
            if (stripToken)
            {
                await delay(TimeSpan.FromMilliseconds(500), ct); // let OBS finish writing the previous collection
                StripRestoreToken(path, log);
            }
        }
        catch
        {
            bodyFailed = true;
            throw;
        }
        finally
        {
            // Never leave the user in the scratch collection, even when cancelled. Bounded: one
            // request (ObsClient's per-request timeout) plus at most 40 polls of 250 ms. When the body
            // already failed, a switch-back failure is logged and the original exception propagates.
            try
            {
                await SwitchBackAsync(obs, collection);
            }
            catch (Exception e) when (bodyFailed)
            {
                log.Info($"could not switch back to scene collection '{collection}': {e.Message}");
            }
        }
    }

    private async Task WaitForPickAsync(ObsClient obs, string scene, string source, CancellationToken ct)
    {
        if (!UnderHyprland)
        {
            await AskInScreenSharingDialogAsync(obs, scene, source, ct);
            return;
        }

        if (pickerWait is null) notifier.Notify("Pick the window to record", "Select it in the share picker.", Urgency.Low);
        pickerWait?.Opening();
        try
        {
            await WaitForHyprlandPickerAsync(ct);
        }
        finally
        {
            pickerWait?.Done();
        }
    }

    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(250);
    private static readonly int SlowTicks = (int)(TimeSpan.FromSeconds(10) / Tick);
    private static readonly int StartTicks = (int)(TimeSpan.FromSeconds(20) / Tick);
    private static readonly int OpenTicks = (int)(TimeSpan.FromMinutes(3) / Tick);

    /// <summary>
    /// Waits for the picker to start (up to 20 s), then for the user to choose (it closes; up to 3 min). The sign goes
    /// once its overlay shows, and says so when nothing shows for 10 s.
    /// </summary>
    private async Task WaitForHyprlandPickerAsync(CancellationToken ct)
    {
        var ticks = 0;
        while (!_pickerRunning())
        {
            if (ticks == StartTicks)
            {
                // Hyprland without hyprland-preview-share-picker (another portal picker, or none needed).
                log.Info("picker never appeared, continuing with whatever OBS captures");
                return;
            }
            if (++ticks == SlowTicks) pickerWait?.Slow();
            await delay(Tick, ct);
        }
        var shown = false;
        for (var i = 0; _pickerRunning(); i++)
        {
            if (i == OpenTicks)
            {
                log.Info("picker still open after 3 min; carrying on");
                return;
            }
            if (!shown && _pickerVisible())
            {
                shown = true;
                pickerWait?.Done();
            }
            if (!shown && ++ticks == SlowTicks) pickerWait?.Slow();
            await delay(Tick, ct);
        }
    }

    private async Task AskInScreenSharingDialogAsync(ObsClient obs, string scene, string source, CancellationToken ct)
    {
        notifier.Notify("Pick the window to record", "Select it in the screen-sharing dialog.", Urgency.Low);
        await WaitForSourceSizeAsync(obs, scene, source, ct);
    }

    // GNOME's dialog is not ours to watch, so the only sign that the user has chosen is the source
    // getting a size. Up to 3 min; the Starter then reports "No window selected" if it never did.
    private async Task WaitForSourceSizeAsync(ObsClient obs, string scene, string source, CancellationToken ct)
    {
        for (var i = 0; i < 600; i++)
        {
            if (await SourceHasSizeAsync(obs, scene, source, ct)) return;
            await delay(TimeSpan.FromMilliseconds(300), ct);
        }
        log.Info("no window chosen after 3 min; carrying on");
    }

    // The item id is read every time: the collection switch recreated the source.
    private static async Task<bool> SourceHasSizeAsync(ObsClient obs, string scene, string source, CancellationToken ct)
    {
        try
        {
            var id = await obs.RequestAsync("GetSceneItemId", new { sceneName = scene, sourceName = source }, ct);
            var xf = await obs.RequestAsync("GetSceneItemTransform",
                new { sceneName = scene, sceneItemId = id.GetProperty("sceneItemId").GetInt32() }, ct);
            var t = xf.GetProperty("sceneItemTransform");
            return t.TryGetProperty("sourceWidth", out var w) && w.ValueKind == JsonValueKind.Number && w.GetDouble() > 0
                && t.TryGetProperty("sourceHeight", out var h) && h.ValueKind == JsonValueKind.Number && h.GetDouble() > 0;
        }
        catch (Exception e) when (e is ObsRequestException or KeyNotFoundException)
        {
            return false; // not there yet
        }
    }

    private async Task SwitchBackAsync(ObsClient obs, string collection)
    {
        try
        {
            await obs.RequestAsync("SetCurrentSceneCollection", new { sceneCollectionName = collection }, CancellationToken.None);
        }
        catch (ObsRequestException)
        {
            throw new InvalidOperationException($"could not switch back to scene collection '{collection}'");
        }
        for (var i = 0; i < 40; i++)
        {
            if (await CurrentCollectionAsync(obs, CancellationToken.None) == collection) return;
            await delay(TimeSpan.FromMilliseconds(250), CancellationToken.None);
        }
        throw new InvalidOperationException($"OBS did not switch back to '{collection}'");
    }

    private static async Task<string> CurrentCollectionAsync(ObsClient obs, CancellationToken ct)
    {
        var r = await obs.RequestAsync("GetSceneCollectionList", null, ct);
        return r.TryGetProperty("currentSceneCollectionName", out var n) && n.ValueKind == JsonValueKind.String
            ? n.GetString()! : "";
    }

    private async Task<bool> WaitCollectionAsync(ObsClient obs, string want, CancellationToken ct)
    {
        for (var i = 0; i < 40; i++)
        {
            if (await CurrentCollectionAsync(obs, ct) == want) return true;
            await delay(TimeSpan.FromMilliseconds(250), ct);
        }
        return false;
    }

    /// <summary>
    /// The file of the scene collection called <paramref name="collection"/>: the one whose "name" it is. Not
    /// user.ini's SceneCollectionFile, which OBS updates later: right after a switch (Sakshi's own, before a recording)
    /// it still names the collection OBS just left, and the wrong collection's window would be forgotten.
    /// </summary>
    private string? CollectionFile(string collection)
    {
        if (SakshiObsProfile.CollectionFileOf(paths.ObsConfigDir, collection) is { } found) return found;
        var dir = Path.Combine(paths.ObsConfigDir, "basic", "scenes");
        // A file that cannot be read for its name: user.ini's, when it names this collection.
        return UserIni("SceneCollection") == collection && UserIni("SceneCollectionFile") is { Length: > 0 } f
            ? Path.Combine(dir, f) : null;
    }

    private string? UserIni(string key)
    {
        try
        {
            var path = Path.Combine(paths.ObsConfigDir, "user.ini");
            return File.Exists(path) ? IniText.Get(File.ReadAllText(path), "Basic", key) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // Leaves the file alone when it cannot be parsed or written.
    private static void StripRestoreToken(string path, ILog log)
    {
        var tmp = path + ".sakshi-tmp";
        try
        {
            if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject root)
                throw new JsonException("not a JSON object");
            if (root["sources"] is JsonArray sources)
                foreach (var s in sources)
                    if (s is JsonObject o && o["id"] is JsonValue id && id.TryGetValue<string>(out var kind) && kind == Kind
                        && o["settings"] is JsonObject settings)
                        settings.Remove("RestoreToken");
            File.WriteAllText(tmp, root.ToJsonString(WriteOptions));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            log.Info($"could not strip RestoreToken from {path}: {e.Message}");
            try { File.Delete(tmp); } catch (Exception d) when (d is IOException or UnauthorizedAccessException) { }
        }
    }
}
