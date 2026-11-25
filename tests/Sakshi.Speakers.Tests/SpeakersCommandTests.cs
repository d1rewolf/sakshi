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
            Entry("S2", SpeakerStatus.Suggested, null, "Ben", 60, "Hi", [0.8f, 0.6f]),
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
        var code = SpeakersCommand.RunAsync(args, [Path.Combine(_root, "meetings")], _voices, o, e, Now).GetAwaiter().GetResult();
        return (code, o.ToString().Replace("\r", ""), e.ToString().Replace("\r", ""));
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
        Assert.Equal("S1  unknown     7:12  \"Can you see my screen?\"", lines[0]);
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
        Assert.Equal([0.8f, 0.6f], lib.Find("Ada")!.Prints.Single().Vector);
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
        // The person stays; the voiceprint taught from this meeting's S3 goes.
        Assert.Empty(VoiceLibrary.Load(_voices).Find("Cy")!.Prints);
    }

    [Fact]
    public void CorrectingANameMovesThePrint()
    {
        Run(_dir, "S1=Ada");
        Assert.Equal(0, Run(_dir, "S1=Ben").Code);
        var lib = VoiceLibrary.Load(_voices);
        Assert.Empty(lib.Find("Ada")!.Prints);
        var ben = Assert.Single(lib.Find("Ben")!.Prints);
        Assert.Equal(("2026-01-05-Mon/standup", "S1"), (ben.Meeting, ben.Speaker));
    }

    private void Relabel(string id, float[] voiceprint)
    {
        // What sakshi speakers --add leaves behind: the same ids, numbered afresh, with new voiceprints.
        var f = Sf;
        f = f with
        {
            Speakers = f.Speakers.Select(x => x.Id == id
                ? x with { Status = SpeakerStatus.Unknown, Name = null, PersonId = null, Voiceprint = voiceprint } : x).ToList(),
        };
        f.Write(Path.Combine(_dir, "speakers.json"));
    }

    [Fact]
    public void CorrectingAfterRelabellingKeepsThePrintTheOldSpeakerTaught()
    {
        Run(_dir, "S1=Ada");
        Relabel("S1", [0.6f, 0.8f]);
        Assert.Equal(0, Run(_dir, "S1=Ben").Code);
        var lib = VoiceLibrary.Load(_voices);
        Assert.Equal([1f, 0f], Assert.Single(lib.Find("Ada")!.Prints).Vector);
        Assert.Equal([0.6f, 0.8f], Assert.Single(lib.Find("Ben")!.Prints).Vector);
    }

    [Fact]
    public void TakingANameOffAfterRelabellingKeepsThePrintTheOldSpeakerTaught()
    {
        Run(_dir, "S1=Ada");
        Relabel("S1", [0.6f, 0.8f]);
        Run(_dir, "S1=Ben");
        Assert.Equal(0, Run(_dir, "S1=").Code);
        var lib = VoiceLibrary.Load(_voices);
        Assert.Single(lib.Find("Ada")!.Prints);
        Assert.Empty(lib.Find("Ben")!.Prints);
    }

    [Fact]
    public void JoiningLeavesOnePrintTheJoinedVoice()
    {
        Run(_dir, "S1=Ada", "S3=Ada");
        var print = Assert.Single(VoiceLibrary.Load(_voices).Find("Ada")!.Prints);
        Assert.Equal("S1", print.Speaker);
        Assert.Equal(Sf.Speakers.Single(s => s.Id == "S1").Voiceprint, print.Vector);
    }

    [Fact]
    public void CorrectingAJoinedSpeakerMovesEverythingItTaught()
    {
        Run(_dir, "S1=Ada");
        Run(_dir, "S3=Ada");
        Assert.Equal(0, Run(_dir, "S1=Ben").Code);
        var lib = VoiceLibrary.Load(_voices);
        Assert.Empty(lib.Find("Ada")!.Prints);
        Assert.Single(lib.Find("Ben")!.Prints);
    }

    [Fact]
    public void TakingTheNameOffAJoinedSpeakerForgetsEverythingItTaught()
    {
        Run(_dir, "S2=Ada", "S3=Ada", "S1=Ada");
        Assert.Single(VoiceLibrary.Load(_voices).Find("Ada")!.Prints);
        Assert.Equal(0, Run(_dir, "S1=").Code);
        Assert.Empty(VoiceLibrary.Load(_voices).Find("Ada")!.Prints);
    }

    [Fact]
    public void JoiningAFullPersonKeepsTheirOtherPrints()
    {
        var lib = VoiceLibrary.Load(_voices);
        for (var k = 0; k < 9; k++) lib.Teach("Ada", [k, 1f], $"x/m{k}", "S1", Now.AddDays(-10 + k));
        lib.Save();
        Run(_dir, "S1=Ada");
        Assert.Equal(10, VoiceLibrary.Load(_voices).Find("Ada")!.Prints.Count);
        Assert.Equal(0, Run(_dir, "S3=Ada").Code);
        var ada = VoiceLibrary.Load(_voices).Find("Ada")!;
        Assert.Equal(10, ada.Prints.Count);
        Assert.Contains(ada.Prints, p => p.Meeting == "x/m0");
    }

    [Fact]
    public void NamingTwiceKeepsOnePrint()
    {
        Run(_dir, "S1=Ada");
        Run(_dir, "S1=Ada");
        Assert.Single(VoiceLibrary.Load(_voices).Find("Ada")!.Prints);
    }

    [Fact]
    public void AcceptTeachesTheSuggestedPersonAfterARename()
    {
        var lib = VoiceLibrary.Load(_voices);
        var ben = lib.Teach("Ben", [0f, 1f], "x/y", "S1", Now);
        lib.Rename("Ben", "Benedict");
        lib.Save();
        var f = Sf;
        f = f with { Speakers = f.Speakers.Select(x => x.Id == "S2" ? x with { PersonId = ben.Id } : x).ToList() };
        f.Write(Path.Combine(_dir, "speakers.json"));

        Assert.Equal("S2 is now Benedict\n", Run(_dir, "--accept").Out.Replace("\r", ""));
        var back = VoiceLibrary.Load(_voices);
        Assert.Single(back.People);
        Assert.Equal(2, back.Find("Benedict")!.Prints.Count);
        Assert.Equal(ben.Id, Sf.Speakers.Single(s => s.Id == "S2").PersonId);
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
    public void TranscriptWithoutTheseSpeakersIsRefused()
    {
        // The meeting was transcribed again without labels; speakers.json is left over from before.
        TranscriptFiles.WriteJson(Path.Combine(_dir, "transcript.json"),
            [new Segment(TimeSpan.Zero, TimeSpan.FromSeconds(2), "Hello there")], "en");
        var before = Snapshot();
        foreach (var args in new[] { new[] { _dir }, [_dir, "S2=Ada"], [_dir, "--accept"] })
        {
            var r = Run(args);
            Assert.Equal(1, r.Code);
            Assert.Contains($"{_dir}: the transcript has no speaker labels; run: sakshi speakers --add {_dir}", r.Err);
        }
        foreach (var (f, bytes) in before) Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(_dir, f)));
        Assert.False(File.Exists(_voices));
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
        var before = Snapshot();
        var r = Run(_dir, "S2=Ada");
        Assert.Equal(1, r.Code);
        foreach (var (f, bytes) in before) Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(_dir, f)));
        Assert.Contains("Error: cannot read " + Path.Combine(_dir, "transcript.json"), r.Err);
    }

    [Fact]
    public void ChainOfJoinsEndsWithTheLowestId()
    {
        var r = Run(_dir, "S2=Ada", "S3=Ada", "S1=Ada");
        Assert.Equal(0, r.Code);
        Assert.Contains("S3 joined S2 (Ada)", r.Out);
        Assert.Contains("S2 joined S1 (Ada)", r.Out);
        var only = Assert.Single(Sf.Speakers);
        Assert.Equal("S1", only.Id);
        Assert.Equal(636, only.Seconds);
        var json = File.ReadAllText(Path.Combine(_dir, "transcript.json"));
        Assert.DoesNotContain("\"S2\"", json);
        Assert.DoesNotContain("\"S3\"", json);
        Assert.Equal(4, json.Split("\"speaker\": \"S1\"").Length - 1);
        Assert.False(File.Exists(Path.Combine(_dir, "speakers", "S2.wav")));
        Assert.False(File.Exists(Path.Combine(_dir, "speakers", "S3.wav")));
        Assert.True(File.Exists(Path.Combine(_dir, "speakers", "S1.wav")));
    }

    [Fact]
    public void AssigningAJoinedAwaySpeakerIsAnErrorAndChangesNothing()
    {
        var before = Snapshot();
        var r = Run(_dir, "S1=Ada", "S3=Ada", "S3=Cy");
        Assert.Equal(2, r.Code);
        Assert.Contains("S3 was joined into S1 earlier in this command", r.Err);
        foreach (var (f, bytes) in before) Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(_dir, f)));
        Assert.True(File.Exists(Path.Combine(_dir, "speakers", "S3.wav")));
        Assert.False(File.Exists(_voices));
    }

    [Fact]
    public void FailedLibrarySaveLeavesTheMeetingAndClips()
    {
        Directory.CreateDirectory(_voices); // a folder where the file should be: Save cannot rename over it
        var before = Snapshot();
        var r = Run(_dir, "S1=Ada", "S3=Ada");
        Assert.Equal(1, r.Code);
        Assert.Contains("Error:", r.Err);
        foreach (var (f, bytes) in before) Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(_dir, f)));
        Assert.True(File.Exists(Path.Combine(_dir, "speakers", "S3.wav")));
    }

    [Fact]
    public void BrokenSpeakersJsonNamesTheFile()
    {
        var path = Path.Combine(_dir, "speakers.json");
        File.WriteAllText(path, "nope");
        var before = Snapshot();
        var r = Run(_dir, "S2=Ada");
        Assert.Equal(1, r.Code);
        Assert.Contains("Error: cannot read " + path, r.Err);
        foreach (var (f, bytes) in before) Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(_dir, f)));
    }

    [Fact]
    public void TalkTimeOverAnHourHasHours()
    {
        var f = Sf;
        f = f with { Speakers = f.Speakers.Select(x => x.Id == "S1" ? x with { Seconds = 3725 } : x).ToList() };
        f.Write(Path.Combine(_dir, "speakers.json"));
        Assert.Contains(" 1:02:05  ", Run(_dir).Out);
    }

    [Fact]
    public void MismatchedVoiceprintLengthIsRefused()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_voices)!);
        var lib = VoiceLibrary.Load(_voices);
        lib.Teach("Cy", [1f, 2f, 3f], "x/y", "S1", Now);
        lib.Save();
        var before = Snapshot();
        var libBytes = File.ReadAllBytes(_voices);
        var r = Run(_dir, "S2=Ada");
        Assert.Equal(1, r.Code);
        Assert.Contains(_voices, r.Err);
        foreach (var (f, bytes) in before) Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(_dir, f)));
        Assert.Equal(libBytes, File.ReadAllBytes(_voices));
    }

    [Fact]
    public void JoinFollowsThePersonNotTheSpelling()
    {
        Run(_dir, "S1=Ada");
        var lib = VoiceLibrary.Load(_voices);
        lib.Rename("Ada", "Adah");
        lib.Save();
        var r = Run(_dir, "S3=Adah");
        Assert.Contains("S3 joined S1 (Adah)", r.Out);
        Assert.Equal("Adah", Sf.Speakers.Single(s => s.Id == "S1").Name);
        Assert.DoesNotContain("**Ada:**", Md);
        Assert.Contains("**Adah:**", Md);
    }

    [Fact]
    public void ListLinesUpTimesToTheLongestName()
    {
        Run(_dir, "S3=Gwendolyn-Ann");
        var lines = Run(_dir).Out.Split('\n').Where(l => l.StartsWith('S')).ToList();
        Assert.Equal(lines[0].IndexOf("7:12"), lines[2].IndexOf("2:24"));
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
