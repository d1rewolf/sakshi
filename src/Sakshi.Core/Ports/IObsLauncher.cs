namespace Sakshi.Core.Ports;

public interface IObsLauncher
{
    /// <summary>Makes sure OBS is running with its WebSocket server reachable. Throws when it cannot.</summary>
    Task EnsureRunningAsync(CancellationToken ct);

    /// <summary>The WebSocket password to use, or null when there is none.</summary>
    string? Password();
}
