using System.Text.Json;
using Sakshi.Search;

namespace Sakshi.Transcribe.Tests;

public class TranscriptFilesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sakshi-tf-" + Guid.NewGuid().ToString("N"));

    public TranscriptFilesTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, true);

    private static Segment Seg(double fromS, double toS, string text) =>
        new(TimeSpan.FromSeconds(fromS), TimeSpan.FromSeconds(toS), text);

    [Fact]
    public void WriteJson_RoundTripsThroughSearchCorpus()
    {
        var segs = new[] { Seg(0, 8.16, " the team's meeting."), Seg(8.16, 18.16, " Yeah, café."), Seg(19, 20, "  ") };
        TranscriptFiles.WriteJson(Path.Combine(_dir, "transcript.json"), segs, "en");

        var lines = Corpus.ReadLines(_dir);
        Assert.Equal(2, lines.Count);
        Assert.Equal(0, lines[0].StartMs);
        Assert.Equal("the team's meeting.", lines[0].Text);
        Assert.Equal(8160, lines[1].StartMs);
        Assert.Equal("Yeah, café.", lines[1].Text);
    }

    [Fact]
    public void WriteJson_Shape()
    {
        TranscriptFiles.WriteJson(Path.Combine(_dir, "t.json"), [Seg(3725.4, 3800, " hi")], "auto");
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(_dir, "t.json")));
        var s = doc.RootElement.GetProperty("transcription")[0];
        Assert.Equal("01:02:05,400", s.GetProperty("timestamps").GetProperty("from").GetString());
        Assert.Equal("01:03:20,000", s.GetProperty("timestamps").GetProperty("to").GetString());
        Assert.Equal(3725400, s.GetProperty("offsets").GetProperty("from").GetInt64());
        Assert.Equal(3800000, s.GetProperty("offsets").GetProperty("to").GetInt64());
        Assert.Equal(" hi", s.GetProperty("text").GetString());
        Assert.Equal("auto", doc.RootElement.GetProperty("result").GetProperty("language").GetString());
    }

    [Fact]
    public void WriteJson_WritesNonAsciiRaw()
    {
        var path = Path.Combine(_dir, "t.json");
        TranscriptFiles.WriteJson(path, [Seg(0, 1, " café <ok> \"q\"")], "en");
        var text = File.ReadAllText(path);
        Assert.Contains("café", text);
        Assert.DoesNotContain("\\u00E9", text);
        Assert.Equal(" café <ok> \"q\"", JsonDocument.Parse(text).RootElement.GetProperty("transcription")[0].GetProperty("text").GetString());
    }

    [Fact]
    public void WriteJson_ZeroSegments_IsValid()
    {
        TranscriptFiles.WriteJson(Path.Combine(_dir, Corpus.Transcript), [], "en");
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(_dir, Corpus.Transcript)));
        Assert.Equal(0, doc.RootElement.GetProperty("transcription").GetArrayLength());
        Assert.Empty(Corpus.ReadLines(_dir));
    }

    [Fact]
    public void WriteSrt_Format()
    {
        var path = Path.Combine(_dir, "transcript.srt");
        TranscriptFiles.WriteSrt(path, [Seg(0, 4, " text"), Seg(4, 3725.5, "no leading space")]);
        Assert.Equal(
            "1\n00:00:00,000 --> 00:00:04,000\n text\n\n"
            + "2\n00:00:04,000 --> 01:02:05,500\n no leading space\n\n",
            File.ReadAllText(path));
    }

    private static Segment Spk(double fromS, double toS, string text, string id, string label) =>
        Seg(fromS, toS, text) with { Speaker = id, SpeakerLabel = label };

    [Fact]
    public void JsonWithSpeakersHasSpeakerFields()
    {
        var path = Path.Combine(_dir, "t.json");
        TranscriptFiles.WriteJson(path, [Spk(0, 1, " hi", "S2", "Ada"), Seg(1, 2, " plain")], "en");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var items = doc.RootElement.GetProperty("transcription");
        Assert.Equal("S2", items[0].GetProperty("speaker").GetString());
        Assert.Equal("Ada", items[0].GetProperty("speaker_label").GetString());
        var names = items[0].EnumerateObject().Select(p => p.Name).ToList();
        Assert.Equal(["timestamps", "offsets", "text", "speaker", "speaker_label"], names);
        Assert.False(items[1].TryGetProperty("speaker", out _));
        Assert.False(items[1].TryGetProperty("speaker_label", out _));
    }

    [Fact]
    public void JsonRoundTrips()
    {
        foreach (var segs in new[]
        {
            new[] { Seg(0, 1.5, " one"), Seg(2, 3, " café") },
            new[] { Spk(0, 1.5, " one", "S1", "Ada"), Seg(2, 3, " two"), Spk(3, 4, " three", "S3", "Speaker 3") },
        })
        {
            var a = Path.Combine(_dir, "a.json");
            var b = Path.Combine(_dir, "b.json");
            TranscriptFiles.WriteJson(a, segs, "fr");
            var (read, lang) = TranscriptFiles.ReadJson(a);
            Assert.Equal("fr", lang);
            TranscriptFiles.WriteJson(b, read, lang);
            Assert.Equal(File.ReadAllBytes(a), File.ReadAllBytes(b));
            Assert.Equal(segs.Select(x => x.Speaker), read.Select(x => x.Speaker));
            Assert.Equal(segs.Select(x => x.SpeakerLabel), read.Select(x => x.SpeakerLabel));
            Assert.Equal(segs.Select(x => x.Start), read.Select(x => x.Start));
        }
    }

    [Fact]
    public void ReadJson_AcceptsWhisperCliExtras()
    {
        var path = Path.Combine(_dir, "w.json");
        File.WriteAllText(path, """
            {"systeminfo":"x","model":{"type":"base"},"params":{"a":1},
             "result":{"language":"de"},
             "transcription":[{"timestamps":{"from":"00:00:01,000","to":"00:00:02,000"},"offsets":{"from":1000,"to":2000},"text":" hallo"}]}
            """);
        var (segs, lang) = TranscriptFiles.ReadJson(path);
        Assert.Equal("de", lang);
        Assert.Equal(TimeSpan.FromSeconds(1), segs[0].Start);
        Assert.Equal(TimeSpan.FromSeconds(2), segs[0].End);
        Assert.Equal(" hallo", segs[0].Text);
        Assert.Null(segs[0].Speaker);
        File.WriteAllText(path, """{"transcription":[]}""");
        Assert.Equal("en", TranscriptFiles.ReadJson(path).Language);
    }

    [Fact]
    public void SrtPrefixesTheLabel()
    {
        var path = Path.Combine(_dir, "t.srt");
        TranscriptFiles.WriteSrt(path, [Spk(0, 1, " Hello there", "S1", "Ada"), Seg(1, 2, " plain")]);
        var lines = File.ReadAllText(path).Split('\n');
        Assert.Equal(" Ada: Hello there", lines[2]);
        Assert.Equal(" plain", lines[6]);
    }
}
