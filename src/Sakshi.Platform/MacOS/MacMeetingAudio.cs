using System.Text.Json;
using Sakshi.Core.Obs;
using Sakshi.Core.Ports;

namespace Sakshi.Platform.MacOS;

public static class MacMeetingAudio
{
    private const string Kind = "sck_audio_capture";
    private const string InputName = "Sakshi Desktop Audio";
    private const int NameTaken = 601;
    private const int AllDesktopAudio = 0;

    private sealed record AudioItem(string Name, int SceneItemId, bool Enabled, bool Muted, bool AllDesktop)
    {
        public bool Records => Enabled && !Muted && AllDesktop;
    }

    /// <summary>
    /// A fresh OBS on macOS 13+ has no desktop-audio source, so remote voices would be missing. Browser meetings
    /// play sound from helper processes that the window source cannot hear, so all desktop audio is the reliable
    /// capture. The window source is muted only once that audio is confirmed to be recorded, so the meeting is
    /// never silent; at worst (a failure on the way) it is recorded twice.
    /// </summary>
    /// <param name="source">The window source to mute once the desktop sound records; null in a sound-only scene.</param>
    public static async Task EnsureAsync(ObsClient obs, string scene, string? source, ILog log, CancellationToken ct)
    {
        try
        {
            if (!await DesktopAudioRecordsAsync(obs, scene, log, ct)) return;
        }
        catch (Exception e) when (IsObsFailure(e))
        {
            log.Info($"Could not set up desktop audio in scene '{scene}' ({e.Message}); recording with the window's own audio only");
            return;
        }
        if (source is null) return;
        try
        {
            await obs.RequestAsync("SetInputMute", new { inputName = source, inputMuted = true }, ct);
        }
        catch (Exception e) when (IsObsFailure(e))
        {
            log.Info($"Could not mute '{source}' ({e.Message}); the meeting's sound may be recorded twice");
        }
    }

    // A reply of an unexpected shape counts as a failed request.
    private static bool IsObsFailure(Exception e) => e is ObsRequestException or KeyNotFoundException or InvalidOperationException;

    /// <summary>True once an enabled, unmuted, all-desktop audio item is in the recorded scene.</summary>
    private static async Task<bool> DesktopAudioRecordsAsync(ObsClient obs, string scene, ILog log, CancellationToken ct)
    {
        var items = await AudioItemsAsync(obs, scene, ct);
        if (items.Any(i => i.Records)) return true;

        var ours = items.FirstOrDefault(i => i.Name == InputName);
        if (ours is null)
        {
            switch (await AddAsync(obs, scene, log, ct))
            {
                case Added.Failed: return false;
                case Added.Created: return true;   // made just now: all desktop audio, unmuted, visible
            }
            // An existing input was put into the scene. Mute and settings belong to the input, so it may be
            // muted or set to one app, or not be an audio capture at all: check it like any other item.
            ours = (await AudioItemsAsync(obs, scene, ct)).FirstOrDefault(i => i.Name == InputName);
            if (ours is null)
            {
                log.Info($"'{InputName}' in scene '{scene}' is not a desktop audio capture; recording with the window's own audio only");
                return false;
            }
            if (ours.Records) return true;
        }

        if (!ours.AllDesktop)
        {
            // Someone set our input to one app; changing it back would override their choice.
            log.Info($"'{InputName}' in scene '{scene}' captures one app only; recording with the window's own audio only");
            return false;
        }
        if (ours.Muted)
            await obs.RequestAsync("SetInputMute", new { inputName = InputName, inputMuted = false }, ct);
        if (!ours.Enabled)
            await obs.RequestAsync("SetSceneItemEnabled",
                new { sceneName = scene, sceneItemId = ours.SceneItemId, sceneItemEnabled = true }, ct);
        return true;
    }

    // OBS records only the inputs of the recorded scene, so an audio input in another scene does not count.
    private static async Task<List<AudioItem>> AudioItemsAsync(ObsClient obs, string scene, CancellationToken ct)
    {
        var list = await obs.RequestAsync("GetSceneItemList", new { sceneName = scene }, ct);
        var found = new List<AudioItem>();
        foreach (var i in list.GetProperty("sceneItems").EnumerateArray())
        {
            if (!i.TryGetProperty("inputKind", out var k) || k.ValueKind != JsonValueKind.String || k.GetString() != Kind) continue;
            var name = i.GetProperty("sourceName").GetString()!;
            var id = i.TryGetProperty("sceneItemId", out var idEl) && idEl.ValueKind == JsonValueKind.Number ? idEl.GetInt32() : 0;
            var enabled = !i.TryGetProperty("sceneItemEnabled", out var en) || en.ValueKind != JsonValueKind.False;
            var mute = await obs.RequestAsync("GetInputMute", new { inputName = name }, ct);
            var muted = mute.GetProperty("inputMuted").GetBoolean();
            found.Add(new AudioItem(name, id, enabled, muted, await CapturesAllDesktopAsync(obs, name, ct)));
        }
        return found;
    }

    // OBS leaves default settings out of the reply, and type 0 (all desktop audio) is the default.
    private static async Task<bool> CapturesAllDesktopAsync(ObsClient obs, string input, CancellationToken ct)
    {
        var reply = await obs.RequestAsync("GetInputSettings", new { inputName = input }, ct);
        return !reply.TryGetProperty("inputSettings", out var s) || s.ValueKind != JsonValueKind.Object
            || !s.TryGetProperty("type", out var t)
            || (t.ValueKind == JsonValueKind.Number && t.GetInt32() == AllDesktopAudio);
    }

    private enum Added { Created, ExistingInput, Failed }

    private static async Task<Added> AddAsync(ObsClient obs, string scene, ILog log, CancellationToken ct)
    {
        try
        {
            try
            {
                await obs.RequestAsync("CreateInput", new
                {
                    sceneName = scene,
                    inputName = InputName,
                    inputKind = Kind,
                    inputSettings = new { type = AllDesktopAudio },
                }, ct);
                return Added.Created;
            }
            catch (ObsRequestException e) when (e.Code == NameTaken)
            {
                // Our input exists in another scene: put it into this one too.
                await obs.RequestAsync("CreateSceneItem", new { sceneName = scene, sourceName = InputName }, ct);
                return Added.ExistingInput;
            }
        }
        catch (ObsRequestException e)
        {
            // Without desktop audio the window's own audio is all there is, so it must stay unmuted.
            log.Info($"Could not add desktop audio to scene '{scene}' ({e.Message}); recording with the window's own audio only");
            return Added.Failed;
        }
    }
}
