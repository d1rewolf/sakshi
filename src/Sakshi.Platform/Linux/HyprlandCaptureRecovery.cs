using System.Text.Json;
using Sakshi.Core.Obs;
using Sakshi.Core.Ports;

namespace Sakshi.Platform.Linux;

/// <summary>
/// Capture recovery under Hyprland: window sizes from hyprctl, reconnect and re-pick through the
/// PipeWire capture's collection hop.
/// </summary>
public sealed class HyprlandCaptureRecovery(PipeWireWindowCapture capture, IProcessRunner runner) : ICaptureRecovery
{
    private static readonly TimeSpan HyprctlTimeout = TimeSpan.FromSeconds(3);

    public async Task<IReadOnlySet<PixelSize>> WindowSizesAsync(CancellationToken ct)
    {
        var scales = new Dictionary<int, double>();
        foreach (var m in await HyprctlAsync("monitors", ct))
            if (m.TryGetProperty("id", out var id) && id.TryGetInt32(out var i)
                && m.TryGetProperty("scale", out var s) && s.TryGetDouble(out var scale))
                scales[i] = scale;

        var sizes = new HashSet<PixelSize>();
        foreach (var c in await HyprctlAsync("clients", ct))
        {
            if (!c.TryGetProperty("size", out var size) || size.ValueKind != JsonValueKind.Array || size.GetArrayLength() != 2
                || !size[0].TryGetInt32(out var w) || !size[1].TryGetInt32(out var h))
                continue;
            sizes.Add(new PixelSize(w, h));
            // The stream carries the window's buffer, which is the logical size times the monitor's scale.
            if (c.TryGetProperty("monitor", out var mon) && mon.TryGetInt32(out var m) && scales.TryGetValue(m, out var scale)
                && scale != 1)
            {
                sizes.Add(new PixelSize((int)Math.Floor(w * scale), (int)Math.Floor(h * scale)));
                sizes.Add(new PixelSize((int)Math.Ceiling(w * scale), (int)Math.Ceiling(h * scale)));
            }
        }
        return sizes;
    }

    public Task ReconnectAsync(ObsClient obs, CancellationToken ct) => capture.ReconnectAsync(obs, ct);

    public Task RepickAsync(ObsClient obs, string scene, string source, CancellationToken ct) =>
        capture.RepickAsync(obs, scene, source, ct);

    private async Task<List<JsonElement>> HyprctlAsync(string what, CancellationToken ct)
    {
        var r = await runner.RunAsync("hyprctl", ["-j", what], HyprctlTimeout, ct);
        if (r.ExitCode != 0) throw new InvalidOperationException($"hyprctl {what} failed ({r.ExitCode})");
        using var doc = JsonDocument.Parse(r.StdOut);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidOperationException($"hyprctl {what}: not a list");
        return [.. doc.RootElement.EnumerateArray().Select(e => e.Clone())];
    }
}
