using Sakshi.Speakers;
using Sakshi.Transcribe;

namespace Sakshi.Speakers.Tests;

public sealed class SpeakersCommandTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sakshi-spk-" + Guid.NewGuid().ToString("N"));
    private readonly string _voices;
    private readonly string _dir;
    private static readonly DateTimeOffset Now = new(2026, 1, 5, 10, 0, 0, TimeSpan.Zero);

    public SpeakersCommandTests()
    {
        _voices = Path.Combine(_root, "voices", "voices.json");
        _dir = Path.Combine(_root, "meetings", "2026-01-05-Mon", "standup");
        Directory.CreateDirectory(Path.Combine(_dir, "speakers"));
        var seg = (int s, string t, string id) => new Segment(TimeSpan.FromSeconds(s), TimeSpan.FromSeconds(s + 2), t) { Speaker = id };
        Segment[] segs = [seg(0, "Hello there", "S1"), seg(3, "Hi", "S2"), seg(6, "Yo", "S3"), seg(9, "More", "S1")];
        var file = new SpeakersFile(1, "m", "standup.mp4", null,
        [
            Entry("S1", SpeakerStatus.Unknown, null, null, 432, "Can you see my screen?", [1f, 0f]),
            Entry("S2", SpeakerStatus.Suggested, null, "Ben", 60, "Hi", [0f, 1f]),
            Entry("S3", SpeakerStatus.Unknown, null, null, 144, "Yo", [0f, 1f]),
        ]);
        foreach (var e in file.Speakers) File.WriteAllText(Path.Combine(_dir, e.Clip), "wav");
        MeetingRewriter.Write(_dir, file, segs, "en");
    }

    private static SpeakerEntry Entry(string id, SpeakerStatus st, string? name, string? sug, double secs, string sample, float[] v) =>
        new(id, st, name, null, sug, sug is null ? null : 0.74, secs, sample, $"speakers/{id}.wav", v);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private (int Code, string Out, string Err) Run(params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var code = SpeakersCommand.Run(args, [Path.Combine(_root, "meetings")], _voices, o, e, Now);
        return (code, o.ToString(), e.ToString());
    }

    private string Md => File.ReadAllText(Path.Combine(_dir, "meeting.md"));
    private SpeakersFile Sf => SpeakersFile.Read(Path.Combine(_dir, "speakers.json"));

    private Dictionary<string, byte[]> Snapshot() =>
        new[] { "transcript.json", "transcript.srt", "meeting.md", "speakers.json" }
            .ToDictionary(f => f, f => File.ReadAllBytes(Path.Combine(_dir, f)));

    [Fact]
    public void ListsEachSpeaker()
    {
        var r = Run(_dir);
        Assert.Equal(0, r.Code);
        var lines = r.Out.TrimEnd().Split('\n');
        Assert.Equal("S1  unknown      7:12  \"Can you see my screen?\"", lines[0]);
        Assert.Equal("    " + Path.Combine(_dir, "speakers", "S1.wav"), lines[1]);
        Assert.StartsWith("S2  Ben? (0.74)", lines[2]);
        Assert.Contains("1:00", lines[2]);
    }

    [Fact]
    public void NamesASpeakerAndTeachesTheLibrary()
    {
        var r = Run(_dir, "S2=Ada");
        Assert.Equal(0, r.Code);
        Assert.Equal("S2 is now Ada\n", r.Out.Replace("\r", ""));
        var s2 = Sf.Speakers.Single(s => s.Id == "S2");
        Assert.Equal(SpeakerStatus.Named, s2.Status);
        Assert.Null(s2.Suggestion);
        Assert.Contains("**Ada:**", Md);
        var lib = VoiceLibrary.Load(_voices);
        Assert.Equal([0f, 1f], lib.Find("Ada")!.Prints.Single().Vector);
        Assert.Equal("2026-01-05-Mon/standup", lib.Find("Ada")!.Prints.Single().Meeting);
        Assert.Equal(lib.Find("Ada")!.Id, s2.PersonId);
    }

    [Fact]
    public void TrimsAcceptsLowercaseIdAndEscapes()
    {
        Assert.Equal(0, Run(_dir, "s2= mary *ann* ").Code);
        Assert.Equal("mary *ann*", Sf.Speakers.Single(s => s.Id == "S2").Name);
        Assert.Contains("**mary \\*ann\\*:**", Md);
    }

    [Fact]
    public void ExistingPersonKeepsFirstSpelling()
    {
        Run(_dir, "S2=Ada");
        Run(_dir, "S1=ADA");
        Assert.DoesNotContain(Sf.Speakers, s => s.Name == "ADA");
    }

    [Fact]
    public void SameNameOnTwoSpeakersJoins()
    {
        var r = Run(_dir, "S1=Ada", "S3=Ada");
        Assert.Equal(0, r.Code);
        Assert.Contains("S3 joined S1 (Ada)", r.Out);
        var f = Sf;
        Assert.Equal(["S1", "S2"], f.Speakers.Select(s => s.Id));
        Assert.Equal(576, f.Speakers[0].Seconds);
        Assert.Equal(0.25f, f.Speakers[0].Voiceprint[1], 3);
        Assert.False(File.Exists(Path.Combine(_dir, "speakers", "S3.wav")));
        Assert.DoesNotContain("S3", File.ReadAllText(Path.Combine(_dir, "transcript.json")));
    }

    [Fact]
    public void NamingWithAnExistingNameJoinsLaterRun()
    {
        Run(_dir, "S1=Ada");
        var r = Run(_dir, "S3=ada");
        Assert.Contains("S3 joined S1 (Ada)", r.Out);
        Assert.Equal(["S1", "S2"], Sf.Speakers.Select(s => s.Id));
    }

    [Fact]
    public void EmptyNameTakesItOff()
    {
        Run(_dir, "S3=Cy");
        var before = File.ReadAllBytes(_voices);
        Assert.Equal(0, Run(_dir, "S3=").Code);
        var s3 = Sf.Speakers.Single(s => s.Id == "S3");
        Assert.Equal(SpeakerStatus.Unknown, s3.Status);
        Assert.Null(s3.Name);
        Assert.Null(s3.PersonId);
        Assert.Contains("Speaker 3", Md);
        Assert.Equal(before, File.ReadAllBytes(_voices));
    }

    [Fact]
    public void AcceptNamesEverySuggestion()
    {
        var r = Run(_dir, "--accept");
        Assert.Equal("S2 is now Ben\n", r.Out.Replace("\r", ""));
        Assert.Equal(SpeakerStatus.Named, Sf.Speakers.Single(s => s.Id == "S2").Status);
        Assert.NotNull(VoiceLibrary.Load(_voices).Find("Ben"));
        Assert.Equal("No suggestions to accept.\n", Run(_dir, "--accept").Out.Replace("\r", ""));
    }

    [Fact]
    public void UnknownIdChangesNothing()
    {
        var before = Snapshot();
        var r = Run(_dir, "S2=Ada", "S9=Cy");
        Assert.Equal(2, r.Code);
        Assert.Contains($"no speaker S9 in {_dir} (S1–S3)", r.Err);
        foreach (var (f, bytes) in before) Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(_dir, f)));
        Assert.False(File.Exists(_voices));
    }

    [Fact]
    public void MeetingWithoutSpeakersJson()
    {
        File.Delete(Path.Combine(_dir, "speakers.json"));
        var r = Run(_dir);
        Assert.Equal(1, r.Code);
        Assert.Contains($"{_dir} has no speaker labels; run: sakshi speakers --add {_dir}", r.Err);
    }

    [Fact]
    public void BadVoicesFileLeavesTheMeeting()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_voices)!);
        File.WriteAllText(_voices, "nope");
        var before = Snapshot();
        var r = Run(_dir, "S2=Ada");
        Assert.Equal(1, r.Code);
        Assert.Contains(_voices, r.Err);
        foreach (var (f, bytes) in before) Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(_dir, f)));
        Assert.Equal("nope", File.ReadAllText(_voices));
    }

    [Fact]
    public void UnreadableTranscriptNamesTheFile()
    {
        File.WriteAllText(Path.Combine(_dir, "transcript.json"), "{\"transcription\":[{}]}");
        var r = Run(_dir, "S2=Ada");
        Assert.Equal(1, r.Code);
        Assert.Contains("Error: cannot read " + Path.Combine(_dir, "transcript.json"), r.Err);
    }

    [Fact]
    public void FindsAMeetingByName()
    {
        Assert.Equal(0, Run("standup").Code);
    }

    [Fact]
    public void HelpNoArgsAndAdd()
    {
        Assert.Equal(0, Run("--help").Code);
        Assert.Contains("sakshi speakers MEETING --accept", Run("-h").Out);
        Assert.Equal(2, Run().Code);
        var r = Run("--add", _dir);
        Assert.Equal(2, r.Code);
        Assert.Contains("sakshi speakers: --add is not available yet", r.Err);
    }
}
