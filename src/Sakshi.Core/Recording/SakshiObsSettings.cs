using System.Text.Json;
using Sakshi.Core.Config;
using Sakshi.Core.Obs;
using Sakshi.Core.Paths;
using Sakshi.Core.Ports;
using Sakshi.Core.State;

namespace Sakshi.Core.Recording;

/// <summary>
/// Puts OBS on Sakshi's own profile and scene collection for a recording, and back on the user's afterwards
/// (<see cref="SakshiObsProfile"/>). OBS that the user already had open is only switched when they agree. Where to
/// switch back to is kept in the state, so a stop after a restart of the tray still finds it.
/// </summary>
public sealed class SakshiObsSettings(
    SakshiConfig config,
    SakshiPaths paths,
    HostOs os,
    StateStore store,
    IPrompter prompter,
    ILog log,
    Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    private const string Name = SakshiObsProfile.Name;
    private const string SwitchRow = "Switch to Sakshi's settings";
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(250);
    private const int Polls = 40;   // OBS switches on its UI thread: allow it 10 s
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;

    /// <summary>
    /// Switches OBS to Sakshi's profile and scene collection. <paramref name="ask"/>: OBS was open before Sakshi
    /// needed it, so the user is asked first. False when they would rather not (nothing is changed then). Throws
    /// <see cref="InvalidOperationException"/>, with a message fit to show, when OBS cannot switch.
    /// </summary>
    public async Task<bool> EnterAsync(ObsClient obs, bool ask, CancellationToken ct)
    {
        var (profile, profiles) = await ListAsync(obs, "GetProfileList", "currentProfileName", "profiles", ct);
        var (collection, collections) = await ListAsync(obs, "GetSceneCollectionList", "currentSceneCollectionName", "sceneCollections", ct);
        var onOurs = profile == Name && collection == Name;

        if (ask && !onOurs)
        {
            var rows = new List<PromptRow>
            {
                new("\U000F04E1", SwitchRow, "For this recording; yours come back when it stops"),   // swap
                new("\U000F0156", "Don't record", "Leave OBS as it is"),                              // close
            };
            if (await prompter.ChooseAsync("OBS is open with your own settings", rows, TimeSpan.FromMinutes(3), ct) != SwitchRow)
            {
                log.Info("OBS is on the user's settings and they chose not to switch; not recording");
                return false;
            }
        }
        if (await IsActiveAsync(obs, "GetStreamStatus", ct) || await IsActiveAsync(obs, "GetVirtualCamStatus", ct))
            throw new InvalidOperationException("OBS is streaming or running its virtual camera, so Sakshi cannot switch it to its own settings. Stop that in OBS, then try again.");

        // Where to come back to. Already on Sakshi's (after a crash, say): what was saved before, else another profile.
        var saved = store.Load().ObsReturn;
        var back = new ObsReturn(
            profile != Name ? profile : saved?.Profile ?? profiles.FirstOrDefault(p => p != Name) ?? Name,
            collection != Name ? collection : saved?.Collection ?? collection);
        store.Update(s => s with { ObsReturn = back });
        try
        {
            await SwitchToOursAsync(obs, profile, profiles, collection, collections, back, ct);
        }
        catch
        {
            // Half switched (a switch timed out, the profile could not be written, a cancel): put the user's back.
            await LeaveAsync(obs, CancellationToken.None);
            throw;
        }
        log.Info($"OBS on Sakshi's profile and scene collection (back to '{back.Profile}' / '{back.Collection}' after)");
        return true;
    }

    private async Task SwitchToOursAsync(ObsClient obs, string profile, List<string> profiles, string collection,
        List<string> collections, ObsReturn back, CancellationToken ct)
    {
        // OBS reads a profile's file only when it switches to it, so Sakshi's is written while OBS is on another one:
        // also when OBS already sits on Sakshi's, so a changed setting always counts.
        var away = profile != Name ? profile : back.Profile;
        if (away == Name || !profiles.Contains(away))
        {
            log.Info("OBS has no profile but Sakshi's; recording with the settings it has");
        }
        else
        {
            if (profile == Name)
                await SwitchAsync(obs, "SetCurrentProfile", "profileName", "GetProfileList", "currentProfileName", away, ct);
            if (!profiles.Contains(Name))
            {
                // OBS lists only the profiles it found at its start (or made itself): let it make Sakshi's, then go back.
                await obs.RequestAsync("CreateProfile", new { profileName = Name }, ct);
                await WaitAsync(obs, "GetProfileList", "currentProfileName", Name, ct);
                await SwitchAsync(obs, "SetCurrentProfile", "profileName", "GetProfileList", "currentProfileName", away, ct);
            }
            SakshiObsProfile.Write(paths.ObsConfigDir, config, os, away);
            await SwitchAsync(obs, "SetCurrentProfile", "profileName", "GetProfileList", "currentProfileName", Name, ct);
        }
        if (collection != Name)
        {
            if (collections.Contains(Name))
                await SwitchAsync(obs, "SetCurrentSceneCollection", "sceneCollectionName", "GetSceneCollectionList", "currentSceneCollectionName", Name, ct);
            else
                await obs.RequestAsync("CreateSceneCollection", new { sceneCollectionName = Name }, ct);   // switches to it
            await WaitAsync(obs, "GetSceneCollectionList", "currentSceneCollectionName", Name, ct);
        }
    }

    /// <summary>The microphone and the computer's sound in Sakshi's scene (<see cref="SceneAudio"/>).</summary>
    public Task EnsureSceneAudioAsync(ObsClient obs, string scene, CancellationToken ct) =>
        SceneAudio.EnsureAsync(obs, scene, os, log, ct);

    /// <summary>
    /// Switches OBS back to the user's profile and scene collection, when Sakshi switched it and OBS is still on
    /// Sakshi's. Never while OBS records: then it gives up, and the next stop tries again. Never throws.
    /// </summary>
    public async Task LeaveAsync(ObsClient obs, CancellationToken ct)
    {
        if (store.Load().ObsReturn is not { } back) return;
        try
        {
            // StopRecord answers before OBS has finished the file; a profile switch then would rebuild its outputs
            // under a recording that is still being written.
            for (var i = 0; await IsActiveAsync(obs, "GetRecordStatus", ct); i++)
            {
                if (i == Polls * 3)
                {
                    log.Info("OBS is still recording; its settings are switched back after the next stop");
                    return;
                }
                await _delay(Poll, ct);
            }
            // Only from Sakshi's: whatever the user switched OBS to since stays as it is.
            var (collection, collections) = await ListAsync(obs, "GetSceneCollectionList", "currentSceneCollectionName", "sceneCollections", ct);
            if (collection == Name && collections.Contains(back.Collection) && back.Collection != Name)
                await SwitchAsync(obs, "SetCurrentSceneCollection", "sceneCollectionName", "GetSceneCollectionList", "currentSceneCollectionName", back.Collection, ct);
            var (profile, profiles) = await ListAsync(obs, "GetProfileList", "currentProfileName", "profiles", ct);
            if (profile == Name && profiles.Contains(back.Profile) && back.Profile != Name)
                await SwitchAsync(obs, "SetCurrentProfile", "profileName", "GetProfileList", "currentProfileName", back.Profile, ct);
            store.Update(s => s with { ObsReturn = null });
            log.Info($"OBS switched back to '{back.Profile}' / '{back.Collection}'");
        }
        catch (Exception e)
        {
            // Kept in the state: the next recording's switch remembers it, and its stop tries again.
            log.Info($"could not switch OBS back to '{back.Profile}' / '{back.Collection}': {e.Message}");
        }
    }

    private static async Task<(string Current, List<string> All)> ListAsync(ObsClient obs, string request, string currentKey,
        string listKey, CancellationToken ct)
    {
        var r = await obs.RequestAsync(request, null, ct);
        var current = r.GetProperty(currentKey).GetString() ?? "";
        var all = r.GetProperty(listKey).EnumerateArray().Select(e => e.GetString() ?? "").ToList();
        return (current, all);
    }

    private static async Task<bool> IsActiveAsync(ObsClient obs, string request, CancellationToken ct)
    {
        try
        {
            var r = await obs.RequestAsync(request, null, ct);
            return r.TryGetProperty("outputActive", out var a) && a.ValueKind == JsonValueKind.True;
        }
        catch (ObsRequestException) { return false; }   // not available in this OBS: nothing running there
    }

    private async Task SwitchAsync(ObsClient obs, string request, string field, string list, string currentKey, string name,
        CancellationToken ct)
    {
        await obs.RequestAsync(request, new Dictionary<string, string> { [field] = name }, ct);
        await WaitAsync(obs, list, currentKey, name, ct);
    }

    private async Task WaitAsync(ObsClient obs, string list, string currentKey, string name, CancellationToken ct)
    {
        for (var i = 0; i < Polls; i++)
        {
            var r = await obs.RequestAsync(list, null, ct);
            if (r.GetProperty(currentKey).GetString() == name) return;
            await _delay(Poll, ct);
        }
        throw new InvalidOperationException($"OBS did not switch to '{name}'. Try again, or switch it in OBS's menus.");
    }
}
