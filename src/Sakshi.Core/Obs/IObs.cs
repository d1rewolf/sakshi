namespace Sakshi.Core.Obs;

/// <summary>
/// One connection to OBS for one stop. Disposing it closes the connection; the Recorder
/// disposes it on every path.
/// </summary>
public interface IObs : IAsyncDisposable
{
    Task<bool> IsRecordingAsync(CancellationToken ct);
    Task<string?> StopRecordAsync(CancellationToken ct);
    Task<string?> GetRecordDirectoryAsync(CancellationToken ct);
}
