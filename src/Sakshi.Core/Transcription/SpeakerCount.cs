using System.Text.Json;

namespace Sakshi.Core.Transcription;

/// <summary>How many voices in a meeting still need a name, read from its speakers.json.</summary>
public static class SpeakerCount
{
    /// <summary>Entries whose status is not "named"; 0 when the file is missing or unreadable.</summary>
    public static int ToName(string meetingDir)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(meetingDir, "speakers.json")));
            if (!doc.RootElement.TryGetProperty("speakers", out var list) || list.ValueKind != JsonValueKind.Array) return 0;
            return list.EnumerateArray().Count(s =>
                !(s.ValueKind == JsonValueKind.Object && s.TryGetProperty("status", out var st)
                  && st.ValueKind == JsonValueKind.String && st.GetString() == "named"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return 0;
        }
    }
}
