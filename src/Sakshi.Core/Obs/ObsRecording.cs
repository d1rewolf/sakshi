using System.Text.Json;

namespace Sakshi.Core.Obs;

/// <summary>The recording requests over an <see cref="ObsClient"/> it owns: disposing this closes the websocket.</summary>
public sealed class ObsRecording(ObsClient client) : IObs
{
    public ValueTask DisposeAsync() => client.DisposeAsync();

    public async Task<bool> IsRecordingAsync(CancellationToken ct)
    {
        var d = await client.RequestAsync("GetRecordStatus", null, ct);
        return d.ValueKind == JsonValueKind.Object
            && d.TryGetProperty("outputActive", out var p)
            && p.ValueKind == JsonValueKind.True;
    }

    public async Task<string?> StopRecordAsync(CancellationToken ct) =>
        Str(await client.RequestAsync("StopRecord", null, ct), "outputPath");

    public async Task<string?> GetRecordDirectoryAsync(CancellationToken ct) =>
        Str(await client.RequestAsync("GetRecordDirectory", null, ct), "recordDirectory");

    private static string? Str(JsonElement d, string name)
    {
        if (d.ValueKind != JsonValueKind.Object || !d.TryGetProperty(name, out var p)
            || p.ValueKind != JsonValueKind.String)
            return null;
        var s = p.GetString();
        return string.IsNullOrEmpty(s) ? null : s;
    }
}
