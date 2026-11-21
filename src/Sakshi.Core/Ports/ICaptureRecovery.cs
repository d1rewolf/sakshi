using Sakshi.Core.Obs;

namespace Sakshi.Core.Ports;

/// <summary>A window's size in pixels.</summary>
public readonly record struct PixelSize(int Width, int Height);

/// <summary>
/// Mending a window capture that stopped receiving frames while recording. Only platforms where the
/// freeze happens provide one (Linux under Hyprland: a resize can leave PipeWire's stream stuck on its
/// last frame); elsewhere there is none.
/// </summary>
public interface ICaptureRecovery
{
    /// <summary>
    /// Every size a live capture of an open window could report: each window's size, also scaled by its
    /// monitor's scale. A capture whose size is not among them has fallen behind its window.
    /// </summary>
    Task<IReadOnlySet<PixelSize>> WindowSizesAsync(CancellationToken ct);

    /// <summary>Restarts the capture on the same window without asking the user. Throws <see cref="InvalidOperationException"/> when OBS refuses.</summary>
    Task ReconnectAsync(ObsClient obs, CancellationToken ct);

    /// <summary>Asks the user to pick the window again and returns once they have. Throws <see cref="InvalidOperationException"/> when OBS refuses.</summary>
    Task RepickAsync(ObsClient obs, string scene, string source, CancellationToken ct);
}
