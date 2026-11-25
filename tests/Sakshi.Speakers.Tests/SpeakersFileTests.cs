using System.Text.Json;
using Sakshi.Speakers;
using Sakshi.Transcribe;

namespace Sakshi.Speakers.Tests;

public sealed class SpeakersFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sakshi-sf-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static SpeakersFile Sample(IReadOnlyList<FrameRecord>? frames) => new(1, "model", "steering.mp4", frames,
    [
        new("S2", SpeakerStatus.Named, "Ada", "p1", null, 0.87, 412.5, "Hello", "speakers/S2.wav", [0.5f, 0.25f]),
        new("S1", SpeakerStatus.Suggested, null, null, "Ben", 0.7, 10, "Hi", "speakers/S1.wav", [1f]),
        new("S3", SpeakerStatus.Unknown, null, null, null, null, 5, "Yo", "speakers/S3.wav", [2f]),
    ]);

    [Fact]
    public void RoundTripsAndLabels()
    {
        var path = Path.Combine(_dir, "speakers.json");
        Sample([new FrameRecord(130.4, "frame_00-02-10.jpg")]).Write(path);
        var back = SpeakersFile.Read(path);
        Assert.Equal(["Ada", "Ben?", "Speaker 3"], back.Speakers.Select(s => s.Label));
        Assert.Equal(SpeakerStatus.Named, back.Speakers[0].Status);
        Assert.Equal(0.87, back.Speakers[0].Score);
        Assert.Null(back.Speakers[2].Score);
        Assert.Equal([0.5f, 0.25f], back.Speakers[0].Voiceprint);
        Assert.Equal(new FrameRecord(130.4, "frame_00-02-10.jpg"), Assert.Single(back.Frames!));
        var text = File.ReadAllText(path);
        Assert.Contains("\"status\": \"named\"", text.Replace("\":\"", "\": \""));
        Assert.DoesNotContain("\"label\"", text);
        Assert.Contains("\"fileName\"", text);
        Assert.Contains("\"suggestion\": null", text.Replace("\":null", "\": null"));
    }

    [Fact]
    public void NullFramesRoundTrip()
    {
        var path = Path.Combine(_dir, "speakers.json");
        Sample(null).Write(path);
        Assert.Null(SpeakersFile.Read(path).Frames);
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("frames").ValueKind);
    }

    [Theory]
    [InlineData("nope")]
    [InlineData("{\"version\":2,\"speakers\":[]}")]
    public void BadContentThrowsWithPath(string text)
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "speakers.json");
        File.WriteAllText(path, text);
        var ex = Assert.Throws<InvalidDataException>(() => SpeakersFile.Read(path));
        Assert.Contains(path, ex.Message);
    }
}
