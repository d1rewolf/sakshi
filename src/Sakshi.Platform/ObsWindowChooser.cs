using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using Sakshi.Core.Config;
using Sakshi.Core.Obs;
using Sakshi.Core.Ports;
using Sakshi.Core.Text;

namespace Sakshi.Platform;

public enum ChooserOs { MacOS, Windows }

/// <summary>
/// macOS and Windows: asks OBS for its capturable windows, lets the user pick one and points the source at it.
/// It always asks, because window IDs don't survive a restart of the window or of OBS.
/// </summary>
public sealed class ObsWindowChooser(ChooserOs os, SakshiConfig config, IPrompter prompter, ILog log,
    Func<ObsClient, string, string?, CancellationToken, Task>? prepare = null) : IWindowCapture
{
    private const string Title = "Pick the window to record";

    /// <summary>A window as OBS names it: "[App] Title" on macOS, "[app.exe]: Title" on Windows.</summary>
    private sealed record Candidate(string Name, JsonElement Value)
    {
        private Match Parts => OwnerAndTitle.Match(Name);

        /// <summary>What the row says: the window's title (the app's name for a window without one).</summary>
        public string Title => Parts is { Success: true } m ? (m.Groups["title"].Value.Trim() is { Length: > 0 } t ? t : m.Groups["app"].Value) : Name;

        /// <summary>The app the window belongs to, shown under the title; empty when OBS's name does not say.</summary>
        public string App => Parts is { Success: true } m ? m.Groups["app"].Value : "";
    }

    private static readonly HashSet<string> OwnApps = new(["sakshi", "sakshi.exe", "sakshiw.exe"], StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// macOS lists the system's own surfaces as windows too (the Dock, Control Center's menu-bar modules, Spotlight):
    /// never a meeting, and they push the real windows down the list.
    /// </summary>
    private static readonly HashSet<string> MacSystemApps = new(
    [
        "Control Center", "Dock", "Notification Center", "Spotlight", "SystemUIServer", "Window Server", "WindowManager",
        "Wallpaper", "TextInputMenuAgent", "loginwindow", "universalAccessAuthWarn", "CursorUIViewService",
    ], StringComparer.OrdinalIgnoreCase);

    /// <summary>A menu-bar icon (any app's): macOS names its window "Item-0", "Item-1"...</summary>
    private static readonly Regex MenuBarItem = new(@"^Item-\d+$");

    private static readonly Regex OwnerAndTitle = new(@"^\[(?<app>[^\]]+)\]:? ?(?<title>.*)$");

    public string InputKind => os == ChooserOs.MacOS ? "screen_capture" : "window_capture";

    public object NewSourceSettings => os == ChooserOs.MacOS ? new { type = 1 } : new { method = 2 };

    /// <summary>A sound-only scene gets what <c>prepare</c> adds (macOS: the computer's sound), with no window to mute.</summary>
    public Task PrepareSoundOnlyAsync(ObsClient obs, string scene, CancellationToken ct) =>
        prepare is null ? Task.CompletedTask : prepare(obs, scene, null, ct);

    public async Task<bool> SelectWindowAsync(ObsClient obs, string scene, string source, bool sourceJustCreated, CancellationToken ct)
    {
        if (prepare is not null) await prepare(obs, scene, source, ct);

        // Without the source in window mode OBS has no window list to give.
        await SetSettingsAsync(obs, source, null, ct);
        var reply = await obs.RequestAsync("GetInputPropertiesListPropertyItems",
            new { inputName = source, propertyName = "window" }, ct);

        var windows = Candidates(reply).Where(Offered).ToList();
        if (windows.Count == 0) throw new InvalidOperationException(NoWindowsMessage);

        var ordered = WindowPriority.Order(windows, w => w.Name, config.PickerPriorityRegex);
        var labelled = Labelled(ordered);
        var rows = labelled.Select(w => new PromptRow("", w.Label, w.Window.App)).ToList();

        var chosen = await prompter.ChooseAsync(Title, rows, TimeSpan.FromMinutes(3), ct);
        if (chosen is null)
        {
            log.Info("No window chosen; not recording.");
            return false;
        }
        await SetSettingsAsync(obs, source, labelled.First(w => w.Label == chosen).Window.Value, ct);
        return true;
    }

    /// <summary>Sakshi's own windows (Setup, the live transcript) are never the meeting; nor, on macOS, is the system's UI.</summary>
    private bool Offered(Candidate w) =>
        !OwnApps.Contains(w.App)
        && !(os == ChooserOs.MacOS && (MacSystemApps.Contains(w.App) || MenuBarItem.IsMatch(w.Title)));

    private string NoWindowsMessage => os == ChooserOs.MacOS
        ? "OBS can't see any windows. Give OBS screen recording permission (System Settings → Privacy & Security → Screen Recording on macOS 13 and 14, Screen & System Audio Recording on macOS 15), restart OBS, and try again."
        : "OBS found no windows to record. Minimised windows aren't listed: restore the meeting window and try again.";

    /// <summary>Real windows only: enabled, named, and not the "[Select a window]" / blank placeholder (value "" or 0).</summary>
    private List<Candidate> Candidates(JsonElement reply)
    {
        var found = new List<Candidate>();
        if (!reply.TryGetProperty("propertyItems", out var items) || items.ValueKind != JsonValueKind.Array) return found;
        foreach (var item in items.EnumerateArray())
        {
            var name = item.TryGetProperty("itemName", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()! : "";
            var enabled = !item.TryGetProperty("itemEnabled", out var e) || e.ValueKind != JsonValueKind.False;
            if (!enabled || string.IsNullOrWhiteSpace(name) || !item.TryGetProperty("itemValue", out var value)) continue;
            if (IsPlaceholder(value)) continue;
            // An identical value is the same window to OBS. On Windows the value is title:class:exe, so two
            // same-titled windows of one app share it: one row, since numbering them would be a lie.
            if (found.Any(c => c.Value.GetRawText() == value.GetRawText())) continue;
            found.Add(new Candidate(name, value.Clone()));
        }
        return found;
    }

    private bool IsPlaceholder(JsonElement value) => os == ChooserOs.MacOS
        ? value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var id) && id == 0
        : value.ValueKind == JsonValueKind.String && value.GetString() == "";

    /// <summary>
    /// Distinct windows can share a title (macOS: two CGWindowIDs; or two apps): number the repeats so each label is
    /// unique and maps back to its window. A number is skipped when a real window already has that title.
    /// </summary>
    private static List<(string Label, Candidate Window)> Labelled(IReadOnlyList<Candidate> ordered)
    {
        var used = ordered.Select(w => w.Title).ToHashSet();
        var firstSeen = new HashSet<string>();
        var result = new List<(string, Candidate)>();
        foreach (var w in ordered)
        {
            if (firstSeen.Add(w.Title))
            {
                result.Add((w.Title, w));
                continue;
            }
            var n = 2;
            while (used.Contains($"{w.Title} ({n})")) n++;
            var label = $"{w.Title} ({n})";
            used.Add(label);
            result.Add((label, w));
        }
        return result;
    }

    /// <summary>The window is written as OBS gave it (int on macOS, string on Windows): the value is opaque.</summary>
    private Task SetSettingsAsync(ObsClient obs, string source, JsonElement? window, CancellationToken ct)
    {
        var settings = JsonSerializer.SerializeToNode(NewSourceSettings)!.AsObject();
        if (window is { } w) settings["window"] = JsonNode.Parse(w.GetRawText());
        return obs.RequestAsync("SetInputSettings", new { inputName = source, inputSettings = settings, overlay = true }, ct);
    }
}
