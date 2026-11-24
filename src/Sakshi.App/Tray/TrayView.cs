using Sakshi.Core.State;

namespace Sakshi.App.Tray;

/// <summary>What the tray shows for a status: tooltip, icon, the menu's status line, toggle label and whether recording-only items (Re-pick window, Live transcript) are enabled. Pure.</summary>
/// <remarks>
/// The tooltip is always "Sakshi": on Linux, Avalonia uses the tooltip text as the tray item's ID, and
/// bars such as Omarchy's pin icons by that ID, so a changing tooltip would lose the pin. The live
/// status (elapsed time, what is transcribing) goes in the menu's first line instead.
/// </remarks>
internal sealed record TrayView(string Tooltip, TrayIconState Icon, string StatusLine, string ToggleLabel, bool Recording,
    bool CanStart = false)
{
    public const string Name = "Sakshi";

    public static TrayView For(Status status, bool blinkOn) => new(
        Name,
        Icons.StateFor(status.State, blinkOn),
        MenuLine(status.Tooltip),
        status.State switch { "recording" => "Stop recording", "naming" => "Name recording", _ => "Start recording" },
        status.State == "recording",
        status.State is "idle" or "transcribing");

    /// <summary>
    /// The status for the menu's first line. The status's own " — click to …" hint is for bars that run
    /// <c>sakshi status</c> and toggle on a click; inside the menu, the next item does it.
    /// </summary>
    internal static string MenuLine(string tooltip)
    {
        var hint = tooltip.IndexOf(" — click to ", StringComparison.Ordinal);
        return hint < 0 ? tooltip : tooltip[..hint];
    }
}
