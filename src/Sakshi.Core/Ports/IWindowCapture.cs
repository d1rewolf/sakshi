using Sakshi.Core.Obs;

namespace Sakshi.Core.Ports;

/// <summary>The platform's way of letting the user pick which window or screen OBS captures.</summary>
public interface IWindowCapture
{
    /// <summary>
    /// Whether a recording's start opens the system's own window picker (Hyprland's), which is slow to appear, so the
    /// Starter shows <see cref="IPickerWait"/> from the start. False where Sakshi asks in its own, quick window.
    /// </summary>
    bool OpensSystemPicker => false;

    /// <summary>The OBS input kind that captures a window on this platform.</summary>
    string InputKind { get; }

    /// <summary>The inputSettings the Starter creates a missing source with.</summary>
    object NewSourceSettings { get; }

    /// <summary>
    /// Lets the user choose what to capture and returns when they are done. The Starter asks on every
    /// start, so the capture decides whether a pick is needed (<paramref name="sourceJustCreated"/>
    /// says the source is new) and returns true at once when not. False means the user chose nothing:
    /// the start stops and nothing is recorded. Throws <see cref="InvalidOperationException"/> with a message
    /// fit to show when it cannot even try.
    /// </summary>
    Task<bool> SelectWindowAsync(ObsClient obs, string scene, string source, bool sourceJustCreated, CancellationToken ct);
}
