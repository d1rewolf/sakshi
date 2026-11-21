using System.Text.Json;
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
    Func<ObsClient, string, string, CancellationToken, Task>? prepare = null) : IWindowCapture
{
    private const string Title = "Pick the window to record";

    private sealed record Candidate(string Name, JsonElement Value);

    public string InputKind => os == ChooserOs.MacOS ? "screen_capture" : "window_capture";

    public object NewSourceSettings => os == ChooserOs.MacOS ? new { type = 1 } : new { method = 2 };

    public async Task<bool> SelectWindowAsync(ObsClient obs, string scene, string source, bool sourceJustCreated, CancellationToken ct)
    {
        if (prepare is not null) await prepare(obs, scene, source, ct);

        // Without the source in window mode OBS has no window list to give.
        await SetSettingsAsync(obs, source, null, ct);
        var reply = await obs.RequestAsync("GetInputPropertiesListPropertyItems",
            new { inputName = source, propertyName = "window" }, ct);

        var windows = Candidates(reply);
        if (windows.Count == 0) throw new InvalidOperationException(NoWindowsMessage);

        var ordered = WindowPriority.Order(windows, w => w.Name, config.PickerPriorityRegex);
        var labelled = Labelled(ordered);
        var rows = labelled.Select(w => new PromptRow("", w.Label, "")).ToList();

        var chosen = await prompter.ChooseAsync(Title, rows, TimeSpan.FromMinutes(3), ct);
        if (chosen is null)
        {
            log.Info("No window chosen; not recording.");
            return false;
        }
        await SetSettingsAsync(obs, source, labelled.First(w => w.Label == chosen).Window.Value, ct);
        return true;
    }

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
    /// Distinct windows can share a title (macOS: two CGWindowIDs): number the repeats so each label is unique and
    /// maps back to its window. A number is skipped when a real window already has that title.
    /// </summary>
    private static List<(string Label, Candidate Window)> Labelled(IReadOnlyList<Candidate> ordered)
    {
        var used = ordered.Select(w => w.Name).ToHashSet();
        var firstSeen = new HashSet<string>();
        var result = new List<(string, Candidate)>();
        foreach (var w in ordered)
        {
            if (firstSeen.Add(w.Name))
            {
                result.Add((w.Name, w));
                continue;
            }
            var n = 2;
            while (used.Contains($"{w.Name} ({n})")) n++;
            var label = $"{w.Name} ({n})";
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
