using Sakshi.Core.Paths;
using Sakshi.Core.Ports;

namespace Sakshi.Core.Obs;

/// <summary>
/// The sound a meeting needs in Sakshi's scene: the microphone and, on Windows and Linux, the computer's own sound
/// (the other people). A new OBS scene collection has neither, unlike OBS's first one. On macOS the computer's sound
/// is MacMeetingAudio's. Only what is missing is added: a source the user changed or muted stays as it is.
/// </summary>
public static class SceneAudio
{
    public const string Microphone = "Sakshi Microphone";
    public const string Desktop = "Sakshi Desktop Audio";
    private const int NameTaken = 601;

    public static async Task EnsureAsync(ObsClient obs, string scene, HostOs os, ILog log, CancellationToken ct)
    {
        var (mic, desktop) = os switch
        {
            HostOs.Windows => ("wasapi_input_capture", "wasapi_output_capture"),
            HostOs.Linux => ("pulse_input_capture", "pulse_output_capture"),
            _ => ("coreaudio_input_capture", (string?)null),
        };
        await AddAsync(obs, scene, Microphone, mic, log, ct);
        if (desktop is not null) await AddAsync(obs, scene, Desktop, desktop, log, ct);
    }

    private static async Task AddAsync(ObsClient obs, string scene, string name, string kind, ILog log, CancellationToken ct)
    {
        try
        {
            await obs.RequestAsync("GetSceneItemId", new { sceneName = scene, sourceName = name }, ct);
            return;   // already there
        }
        catch (ObsRequestException) { }
        try
        {
            await obs.RequestAsync("CreateInput", new
            {
                sceneName = scene, inputName = name, inputKind = kind, inputSettings = new { device_id = "default" },
            }, ct);
            log.Info($"added '{name}' to scene '{scene}'");
        }
        catch (ObsRequestException e) when (e.Code == NameTaken)
        {
            // The source exists outside the scene (the user took it out): put it back in.
            try { await obs.RequestAsync("CreateSceneItem", new { sceneName = scene, sourceName = name }, ct); }
            catch (ObsRequestException e2) { Missing(name, e2, log); }
        }
        catch (ObsRequestException e)
        {
            Missing(name, e, log);
        }
    }

    private static void Missing(string name, ObsRequestException e, ILog log) =>
        log.Info($"could not add '{name}' ({e.Message}); the recording may be missing that sound");
}
