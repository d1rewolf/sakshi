using System.Text.Json;

namespace Sakshi.Core.Obs;

public static class ObsPassword
{
    /// <summary>Reads server_password from OBS's plugin_config/obs-websocket/config.json; null when unavailable.</summary>
    public static string? Read(string obsConfigDir)
    {
        try
        {
            var file = Path.Combine(obsConfigDir, "plugin_config", "obs-websocket", "config.json");
            if (!File.Exists(file)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("server_password", out var p)
                || p.ValueKind != JsonValueKind.String)
                return null;
            var s = p.GetString();
            return string.IsNullOrEmpty(s) ? null : s;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
