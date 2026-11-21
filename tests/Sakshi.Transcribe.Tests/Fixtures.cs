using System.Text.Json;

namespace Sakshi.Transcribe.Tests;

internal static class Fixtures
{
    public static string Root
    {
        get
        {
            for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            {
                var p = Path.Combine(d.FullName, "tests", "fixtures", "transcribe");
                if (Directory.Exists(p)) return p;
            }
            throw new DirectoryNotFoundException("tests/fixtures/transcribe not found above " + AppContext.BaseDirectory);
        }
    }

    public static string Dir(string name) => Path.Combine(Root, name);

    /// <summary>Segments from a whisper-cli style transcript.json (offsets in ms, text as written).</summary>
    public static IReadOnlyList<Segment> ReadSegments(string transcriptJson)
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(transcriptJson));
        return doc.RootElement.GetProperty("transcription").EnumerateArray()
            .Select(s =>
            {
                var o = s.GetProperty("offsets");
                return new Segment(
                    TimeSpan.FromTicks(o.GetProperty("from").GetInt64() * TimeSpan.TicksPerMillisecond),
                    TimeSpan.FromTicks(o.GetProperty("to").GetInt64() * TimeSpan.TicksPerMillisecond),
                    s.GetProperty("text").GetString()!);
            })
            .ToList();
    }

    public static void CopyDir(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)));
        foreach (var d in Directory.GetDirectories(from)) CopyDir(d, Path.Combine(to, Path.GetFileName(d)));
    }
}
