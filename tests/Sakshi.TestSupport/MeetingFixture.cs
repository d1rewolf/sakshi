using System.Text.Json;

namespace Sakshi.TestSupport;

/// <summary>Builds meeting folders holding a transcript.json shaped like whisper-cli's.</summary>
public static class MeetingFixture
{
    public static string Make(string root, string day, string name, IEnumerable<(long Ms, string Text)> segments)
    {
        var dir = Path.Combine(root, day, name.Replace('/', Path.DirectorySeparatorChar));
        if (Directory.Exists(dir))
            throw new IOException($"already exists: {dir}");
        Directory.CreateDirectory(dir);
        var payload = new
        {
            transcription = segments.Select(s => new
            {
                offsets = new { from = s.Ms, to = s.Ms + 1000 },
                text = " " + s.Text,
            }).ToArray(),
        };
        File.WriteAllText(Path.Combine(dir, "transcript.json"), JsonSerializer.Serialize(payload));
        return dir;
    }
}
