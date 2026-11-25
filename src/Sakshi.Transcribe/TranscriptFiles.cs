using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Sakshi.Transcribe;

/// <summary>transcript.json and transcript.srt in whisper-cli's layout.</summary>
public static class TranscriptFiles
{
    public static void WriteJson(string path, IReadOnlyList<Segment> segs, string language)
    {
        using var stream = File.Create(path);
        using var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        w.WriteStartObject();
        w.WriteStartObject("result");
        w.WriteString("language", language);
        w.WriteEndObject();
        w.WriteStartArray("transcription");
        foreach (var s in segs)
        {
            w.WriteStartObject();
            w.WriteStartObject("timestamps");
            w.WriteString("from", Stamp(s.Start));
            w.WriteString("to", Stamp(s.End));
            w.WriteEndObject();
            w.WriteStartObject("offsets");
            w.WriteNumber("from", Millis(s.Start));
            w.WriteNumber("to", Millis(s.End));
            w.WriteEndObject();
            w.WriteString("text", Spaced(s.Text));
            if (s.Speaker is not null && s.SpeakerLabel is not null)
            {
                w.WriteString("speaker", s.Speaker);
                w.WriteString("speaker_label", s.SpeakerLabel);
            }
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();
    }

    public static void WriteSrt(string path, IReadOnlyList<Segment> segs)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < segs.Count; i++)
        {
            var s = segs[i];
            sb.Append(CultureInfo.InvariantCulture, $"{i + 1}\n{Stamp(s.Start)} --> {Stamp(s.End)}\n{SrtText(s)}\n\n");
        }
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }

    /// <summary>Reads a transcript.json written by Sakshi or whisper-cli. Text comes back as written.</summary>
    public static (IReadOnlyList<Segment> Segments, string Language) ReadJson(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        var root = doc.RootElement;
        var language = "en";
        if (root.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object
            && result.TryGetProperty("language", out var lang) && lang.ValueKind == JsonValueKind.String)
            language = lang.GetString() ?? "en";

        var segs = new List<Segment>();
        if (root.TryGetProperty("transcription", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
            {
                var offsets = item.GetProperty("offsets");
                var seg = new Segment(
                    TimeSpan.FromMilliseconds(offsets.GetProperty("from").GetInt64()),
                    TimeSpan.FromMilliseconds(offsets.GetProperty("to").GetInt64()),
                    item.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "");
                if (item.TryGetProperty("speaker", out var sp) && sp.ValueKind == JsonValueKind.String
                    && item.TryGetProperty("speaker_label", out var sl) && sl.ValueKind == JsonValueKind.String)
                    seg = seg with { Speaker = sp.GetString(), SpeakerLabel = sl.GetString() };
                segs.Add(seg);
            }
        }
        return (segs, language);
    }

    private static string SrtText(Segment s) =>
        s.Speaker is not null && s.SpeakerLabel is not null
            ? " " + s.SpeakerLabel + ": " + s.Text.TrimStart()
            : Spaced(s.Text);

    private static long Millis(TimeSpan t) => (long)Math.Round(t.TotalMilliseconds);

    private static string Stamp(TimeSpan t)
    {
        var ms = Millis(t);
        return string.Create(CultureInfo.InvariantCulture,
            $"{ms / 3_600_000:00}:{ms / 60_000 % 60:00}:{ms / 1000 % 60:00},{ms % 1000:000}");
    }

    // whisper-cli writes each segment's text with a single leading space.
    private static string Spaced(string text) => " " + text.TrimStart();
}
