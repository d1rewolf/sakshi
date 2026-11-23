using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sakshi.Platform;

/// <summary>Reads OBS's obs-websocket config; shared by the launcher (which enables the server) and doctor (which reports it).</summary>
internal static class ObsWebSocketConfig
{
    public const string ServerOffMessage =
        "OBS is running but its WebSocket server is off. In OBS: Tools → WebSocket Server Settings → Enable, then try again.";

    public static string FileIn(string obsConfigDir) =>
        Path.Combine(obsConfigDir, "plugin_config", "obs-websocket", "config.json");

    /// <summary>The config as an object. Throws <see cref="InvalidOperationException"/>, with a message fit to show, when it is not one.</summary>
    public static JsonObject Parse(string file, string text)
    {
        try
        {
            return JsonNode.Parse(text) as JsonObject
                ?? throw new InvalidOperationException($"OBS WebSocket config {file} is not a JSON object; fix or remove it.");
        }
        catch (JsonException e)
        {
            throw new InvalidOperationException($"OBS WebSocket config {file} is not valid JSON: {e.Message}", e);
        }
    }

    /// <summary>obs-websocket asks for a password unless the file says otherwise.</summary>
    public static bool AuthRequired(JsonObject root) =>
        root["auth_required"] is not JsonValue v || !v.TryGetValue<bool>(out var on) || on;

    public static bool ServerEnabled(JsonObject root) =>
        root["server_enabled"] is JsonValue v && v.TryGetValue<bool>(out var on) && on;
}
