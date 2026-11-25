using Sakshi.Speakers;
using Sakshi.Transcribe;

namespace Sakshi.Speakers.Tests;

public sealed class AddSpeakersCommandTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sakshi-add-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private sealed class FakeWhisper : IWhisper
    {
        public List<(string Wav, string Model, string Language, int MaxContext, bool Words)> Calls { get; } = [];
        public Task<IReadOnlyList<Segment>> TranscribeAsync(string wavPath, string modelPath, string language, int maxContext,
            CancellationToken ct, bool words = false)
        {
            Calls.Add((wavPath, modelPath, language, maxContext, words));
            IReadOnlyList<Segment> segs = [new Segment(TimeSpan.Zero, TimeSpan.FromSeconds(2), "fresh text")];
            return Task.FromResult(segs);
        }
    }

    private sealed class FakeStep : ISpeakerStep
    {
        public int Speakers { get; set; } = 2;
        public Exception? Throw { get; set; }
        public List<(string Wav, string Dir, string Source, IReadOnlyList<FrameRecord>? Frames)> Calls { get; } = [];
        public Task<SpeakerOutcome> LabelAsync(string audioWav, string outputDir, IReadOnlyList<Segment> segments, string source,
            IReadOnlyList<FrameRecord>? frames, TextWriter stdout, CancellationToken ct)
        {
            Calls.Add((audioWav, outputDir, source, frames));
            if (Throw is not null)
            {
                File.WriteAllText(Path.Combine(outputDir, "speakers.json"), "partial");
                throw Throw;
            }
            if (Speakers == 0 && Throw is null) { Directory.CreateDirectory(Path.Combine(outputDir, "speakers")); File.WriteAllText(Path.Combine(outputDir, "speakers", "X.wav"), "x"); }
            if (Speakers == 0) return Task.FromResult(new SpeakerOutcome(segments, 0, 0, 0));
            var labelled = segments.Select(s => s with { Speaker = "S1", SpeakerLabel = "Speaker 1" }).ToList();
            Directory.CreateDirectory(Path.Combine(outputDir, "speakers"));
            File.WriteAllText(Path.Combine(outputDir, "speakers", "S1.wav"), "new clip");
            var entry = new SpeakerEntry("S1", SpeakerStatus.Unknown, null, null, null, null, 2, "x", "speakers/S1.wav", [1f]);
            new SpeakersFile(1, "m", source, frames, [entry]).Write(Path.Combine(outputDir, "speakers.json"));
            return Task.FromResult(new SpeakerOutcome(labelled, Speakers, 0, Speakers));
        }
    }

    private string Meeting(string name, bool audio = true, string? recording = null, bool transcript = true,
        string language = "de", string[]? frames = null, string? sourceInMd = null)
    {
        var dir = Path.Combine(_root, "meetings", "2026-01-05-Mon", name);
        Directory.CreateDirectory(dir);
        if (audio) File.WriteAllText(Path.Combine(dir, "audio.wav"), "wav");
        if (recording is not null) File.WriteAllText(Path.Combine(dir, recording), "video");
        if (transcript)
            TranscriptFiles.WriteJson(Path.Combine(dir, "transcript.json"),
                [new Segment(TimeSpan.Zero, TimeSpan.FromSeconds(2), "old text")], language);
        if (sourceInMd is not null)
            File.WriteAllText(Path.Combine(dir, "meeting.md"), $"# Meeting: x\n\n- Source: `{sourceInMd}`\n- Segments: 1\n");
        if (frames is not null)
        {
            Directory.CreateDirectory(Path.Combine(dir, "frames"));
            foreach (var f in frames) File.WriteAllText(Path.Combine(dir, "frames", f), "jpg");
        }
        return dir;
    }

    private (int Code, string Out, string Err) Run(FakeWhisper w, FakeStep s, params string[] meetings) =>
        RunWith(w, s, null, meetings);

    private (int Code, string Out, string Err) RunWith(FakeWhisper w, FakeStep s, Func<bool>? ffmpeg, params string[] meetings)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var code = AddSpeakersCommand.RunAsync(meetings, [Path.Combine(_root, "meetings")], w, "/m/model.bin", s, o, e,
            CancellationToken.None, ffmpeg).GetAwaiter().GetResult();
        return (code, o.ToString().Replace("\r", ""), e.ToString().Replace("\r", ""));
    }

    [Fact]
    public void LabelsAMeetingWithItsAudioAndLanguage()
    {
        var dir = Meeting("standup", recording: "standup.mp4", language: "de", frames:
            ["frame_00-01-05_1.jpg", "frame_00-00-10.jpg", "frame_00-01-05.jpg"]);
        var w = new FakeWhisper();
        var s = new FakeStep();
        var r = Run(w, s, dir);
        Assert.Equal(0, r.Code);
        var call = Assert.Single(w.Calls);
        Assert.Equal(Path.Combine(dir, "audio.wav"), call.Wav);
        Assert.Equal("/m/model.bin", call.Model);
        Assert.Equal("de", call.Language);
        Assert.Equal(0, call.MaxContext);
        Assert.True(call.Words);
        var step = Assert.Single(s.Calls);
        Assert.Equal("standup.mp4", step.Source);
        Assert.Equal(["frame_00-00-10.jpg", "frame_00-01-05.jpg", "frame_00-01-05_1.jpg"], step.Frames!.Select(f => f.FileName));
        Assert.Equal([10d, 65d, 65d], step.Frames!.Select(f => f.Seconds));
        Assert.Contains("**Speaker 1:**", File.ReadAllText(Path.Combine(dir, "meeting.md")));
        Assert.Contains("fresh text", File.ReadAllText(Path.Combine(dir, "transcript.json")));
        Assert.Contains("fresh text", File.ReadAllText(Path.Combine(dir, "transcript.srt")));
        Assert.Contains($"==> {dir}\n", r.Out);
        Assert.Contains("   found 2 speakers (2 to name)", r.Out);
    }

    [Fact]
    public void NoFramesFolderMeansNullFrames()
    {
        var dir = Meeting("call", recording: "call.m4a");
        var s = new FakeStep { Speakers = 1 };
        var r = Run(new FakeWhisper(), s, dir);
        Assert.Null(s.Calls.Single().Frames);
        Assert.Contains("   found 1 speaker (1 to name)", r.Out);
    }

    [Fact]
    public void WithoutATranscriptTheLanguageIsEnglish()
    {
        var dir = Meeting("call", recording: "call.mkv", transcript: false);
        var w = new FakeWhisper();
        Assert.Equal(0, Run(w, new FakeStep(), dir).Code);
        Assert.Equal("en", w.Calls.Single().Language);
    }

    [Fact]
    public void OnlyAudioUsesTheSourceNamedInMeetingMdElseAudioWav()
    {
        var a = Meeting("a", sourceInMd: "orig.mp4");
        var b = Meeting("b");
        var s = new FakeStep();
        Run(new FakeWhisper(), s, a, b);
        Assert.Equal(["orig.mp4", "audio.wav"], s.Calls.Select(c => c.Source));
    }

    [Fact]
    public void ExistingSpeakersJsonSuppliesTheFramesAndIsReplaced()
    {
        var dir = Meeting("m", recording: "m.mp4", frames: ["frame_00-00-10.jpg"]);
        FrameRecord[] kept = [new(3, "frame_x.jpg")];
        new SpeakersFile(1, "m", "m.mp4", kept, []).Write(Path.Combine(dir, "speakers.json"));
        var s = new FakeStep();
        var r = Run(new FakeWhisper(), s, dir);
        Assert.Equal(["frame_x.jpg"], s.Calls.Single().Frames!.Select(f => f.FileName));
        Assert.Contains("   replacing the existing speaker labels", r.Out);
    }

    [Fact]
    public void NoAudioAndNoRecordingFails()
    {
        var dir = Meeting("empty", audio: false);
        var w = new FakeWhisper();
        var r = Run(w, new FakeStep(), dir);
        Assert.Equal(1, r.Code);
        Assert.Contains($"{dir} has no audio.wav or recording to label", r.Err);
        Assert.Empty(w.Calls);
    }

    [Fact]
    public void UnknownMeetingIsAUsageError()
    {
        var r = Run(new FakeWhisper(), new FakeStep(), "nosuch");
        Assert.Equal(2, r.Code);
        Assert.Contains("no meeting named nosuch", r.Err);
    }

    [Fact]
    public void ZeroSpeakersLeavesTheFilesUntouched()
    {
        var dir = Meeting("quiet", sourceInMd: "q.mp4");
        var before = File.ReadAllBytes(Path.Combine(dir, "transcript.json"));
        var r = Run(new FakeWhisper(), new FakeStep { Speakers = 0 }, dir);
        Assert.Equal(0, r.Code);
        Assert.Contains($"{dir}: no speakers found", r.Out);
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(dir, "transcript.json")));
        Assert.False(File.Exists(Path.Combine(dir, "transcript.srt")));
    }

    [Fact]
    public async Task AStepFailureWarnsLeavesFilesAndContinuesThenExitsOne()
    {
        var a = Meeting("a");
        var b = Meeting("b");
        var before = File.ReadAllBytes(Path.Combine(a, "transcript.json"));
        var s = new FakeStep();
        var calls = 0;
        var step = new ThrowFirst(s, () => calls++ == 0);
        var o = new StringWriter();
        var e = new StringWriter();
        var code = await AddSpeakersCommand.RunAsync([a, b], [_root], new FakeWhisper(), "m", step, o, e, CancellationToken.None);
        Assert.Equal(1, code);
        Assert.Contains($"Warning: {a}: speaker labels skipped: boom", e.ToString());
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(a, "transcript.json")));
        Assert.Contains("**Speaker 1:**", File.ReadAllText(Path.Combine(b, "transcript.srt")) + File.ReadAllText(Path.Combine(b, "meeting.md")));
        Assert.Contains("   found 2 speakers", o.ToString());
    }

    private sealed class ThrowFirst(ISpeakerStep inner, Func<bool> first) : ISpeakerStep
    {
        public Task<SpeakerOutcome> LabelAsync(string audioWav, string outputDir, IReadOnlyList<Segment> segments, string source,
            IReadOnlyList<FrameRecord>? frames, TextWriter stdout, CancellationToken ct) =>
            first() ? throw new InvalidOperationException("boom") : inner.LabelAsync(audioWav, outputDir, segments, source, frames, stdout, ct);
    }

    [Fact]
    public void TwoMeetingsAreBothReported()
    {
        var a = Meeting("a");
        var b = Meeting("b");
        var r = Run(new FakeWhisper(), new FakeStep(), a, b);
        Assert.Equal(0, r.Code);
        Assert.Contains($"==> {a}", r.Out);
        Assert.Contains($"==> {b}", r.Out);
        Assert.Equal(2, r.Out.Split("   found 2 speakers").Length - 1);
    }

    private static void SeedOld(string dir)
    {
        Directory.CreateDirectory(Path.Combine(dir, "speakers"));
        File.WriteAllText(Path.Combine(dir, "speakers", "S9.wav"), "old clip");
        File.WriteAllText(Path.Combine(dir, "speakers.json"), "old json");
    }

    private static void AssertOldKept(string dir)
    {
        Assert.Equal("old json", File.ReadAllText(Path.Combine(dir, "speakers.json")));
        Assert.Equal("old clip", File.ReadAllText(Path.Combine(dir, "speakers", "S9.wav")));
        Assert.Single(Directory.GetFiles(Path.Combine(dir, "speakers")));
        Assert.False(Directory.Exists(Path.Combine(dir, ".speakers-staging")));
    }

    [Fact]
    public void ZeroSpeakersKeepsExistingSpeakersJsonAndClips()
    {
        var dir = Meeting("z");
        SeedOld(dir);
        Run(new FakeWhisper(), new FakeStep { Speakers = 0 }, dir);
        AssertOldKept(dir);
    }

    [Fact]
    public void AThrowingStepKeepsExistingSpeakersJsonAndClips()
    {
        var dir = Meeting("t");
        SeedOld(dir);
        var r = Run(new FakeWhisper(), new FakeStep { Throw = new InvalidOperationException("boom") }, dir);
        Assert.Equal(1, r.Code);
        AssertOldKept(dir);
    }

    [Fact]
    public void ARewriteFailureKeepsExistingSpeakersJsonAndClips()
    {
        var dir = Meeting("r");
        SeedOld(dir);
        Directory.CreateDirectory(Path.Combine(dir, "meeting.md.tmp")); // makes the meeting.md write fail
        var r = Run(new FakeWhisper(), new FakeStep(), dir);
        Assert.Equal(1, r.Code);
        AssertOldKept(dir);
    }

    [Fact]
    public void SuccessReplacesSpeakersJsonAndDropsOldClips()
    {
        var dir = Meeting("ok");
        SeedOld(dir);
        Assert.Equal(0, Run(new FakeWhisper(), new FakeStep(), dir).Code);
        Assert.Equal("new clip", File.ReadAllText(Path.Combine(dir, "speakers", "S1.wav")));
        Assert.False(File.Exists(Path.Combine(dir, "speakers", "S9.wav")));
        Assert.Equal(1, SpeakersFile.Read(Path.Combine(dir, "speakers.json")).Version);
        Assert.False(Directory.Exists(Path.Combine(dir, ".speakers-staging")));
    }

    [Fact]
    public void ExtractsAudioFromARecordingWithoutLeavingPartFiles()
    {
        Assert.SkipUnless(FfmpegExecutable.Available, "ffmpeg is not available");
        var dir = Meeting("rec", audio: false);
        var m4a = Path.Combine(dir, "rec.m4a");
        var psi = new System.Diagnostics.ProcessStartInfo(FfmpegExecutable.Command)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        foreach (var a in new[] { "-y", "-loglevel", "error", "-f", "lavfi", "-i", "sine=frequency=440:duration=2", m4a })
            psi.ArgumentList.Add(a);
        using (var p = System.Diagnostics.Process.Start(psi)!)
        {
            p.StandardError.ReadToEnd();
            p.WaitForExit();
            Assert.Equal(0, p.ExitCode);
        }
        var s = new FakeStep();
        var r = Run(new FakeWhisper(), s, dir);
        Assert.Equal(0, r.Code);
        Assert.True(File.Exists(Path.Combine(dir, "audio.wav")));
        Assert.Empty(Directory.GetFiles(dir, "*.part*"));
        Assert.Equal(Path.Combine(dir, "audio.wav"), s.Calls.Single().Wav);
        Assert.Equal("rec.m4a", s.Calls.Single().Source);
    }

    [Fact]
    public void FfmpegIsNeededOnlyForAMeetingWithoutAudioWav()
    {
        var withWav = Meeting("a", recording: "a.mp4");
        var withoutWav = Meeting("b", audio: false, recording: "b.mp4");
        var w = new FakeWhisper();
        var r = RunWith(w, new FakeStep(), () => false, withWav, withoutWav);
        Assert.Equal(1, r.Code);
        Assert.Contains("**Speaker 1:**", File.ReadAllText(Path.Combine(withWav, "meeting.md")));
        Assert.Contains($"Warning: {withoutWav}: speaker labels skipped: ffmpeg was not found; it is needed to take the sound out of b.mp4", r.Err);
        Assert.Single(w.Calls);
        Assert.False(File.Exists(Path.Combine(withoutWav, "audio.wav")));
    }

    [Fact]
    public void AClipThatCannotBeReplacedGoesInUnderAnotherName()
    {
        var dir = Meeting("clips");
        SeedOld(dir);
        // A folder where the new clip should go stands in for a clip held open by a player on Windows.
        Directory.CreateDirectory(Path.Combine(dir, "speakers", "S1.wav"));
        var r = Run(new FakeWhisper(), new FakeStep(), dir);
        Assert.Equal(0, r.Code);
        Assert.Equal("", r.Err);
        Assert.Contains("   found 2 speakers", r.Out);
        var json = SpeakersFile.Read(Path.Combine(dir, "speakers.json"));
        var s1 = Assert.Single(json.Speakers);
        Assert.Equal("speakers/S1-2.wav", s1.Clip);
        Assert.Equal("new clip", File.ReadAllText(Path.Combine(dir, "speakers", "S1-2.wav")));
        Assert.Contains("\"speaker\": \"S1\"", File.ReadAllText(Path.Combine(dir, "transcript.json")));
        Assert.Contains("**Speaker 1:**", File.ReadAllText(Path.Combine(dir, "meeting.md")));
        Assert.False(File.Exists(Path.Combine(dir, "speakers", "S9.wav")));
        Assert.False(Directory.Exists(Path.Combine(dir, ".speakers-staging")));
    }

    [Fact]
    public void AnotherNameSkipsNamesAlreadyTaken()
    {
        var dir = Meeting("taken");
        Directory.CreateDirectory(Path.Combine(dir, "speakers", "S1.wav"));
        Directory.CreateDirectory(Path.Combine(dir, "speakers", "S1-2.wav"));
        Assert.Equal(0, Run(new FakeWhisper(), new FakeStep(), dir).Code);
        Assert.Equal("speakers/S1-3.wav", SpeakersFile.Read(Path.Combine(dir, "speakers.json")).Speakers[0].Clip);
        Assert.Equal("new clip", File.ReadAllText(Path.Combine(dir, "speakers", "S1-3.wav")));
    }

    [Fact]
    public void AClipPutInUnderAnotherNameEarlierIsCleanedUp()
    {
        var dir = Meeting("again");
        SeedOld(dir);
        File.WriteAllText(Path.Combine(dir, "speakers", "S1-2.wav"), "older clip");
        File.WriteAllText(Path.Combine(dir, "speakers", "notes.txt"), "mine");
        Assert.Equal(0, Run(new FakeWhisper(), new FakeStep(), dir).Code);
        Assert.Equal(["S1.wav", "notes.txt"],
            Directory.GetFiles(Path.Combine(dir, "speakers")).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal("speakers/S1.wav", SpeakersFile.Read(Path.Combine(dir, "speakers.json")).Speakers[0].Clip);
    }

    [Fact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void AClipThatCannotGoInAtAllIsWarnedAbout()
    {
        if (OperatingSystem.IsWindows()) Assert.Skip("uses a read-only folder, which Windows does not enforce this way");
        if (Environment.UserName == "root") Assert.Skip("root can write to a read-only folder");
        var dir = Meeting("stuck");
        var clips = Path.Combine(dir, "speakers");
        Directory.CreateDirectory(clips);
        File.WriteAllText(Path.Combine(clips, "S1.wav"), "old voice");
        File.SetUnixFileMode(clips, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var r = Run(new FakeWhisper(), new FakeStep(), dir);
            Assert.Equal(0, r.Code);
            Assert.Contains($"Warning: {dir}: could not put the new voice clip for S1 in place: ", r.Err);
            Assert.Contains("speakers/S1.wav may still play an earlier voice", r.Err);
            // The labels are done; only the clip is out of date.
            Assert.Equal("speakers/S1.wav", SpeakersFile.Read(Path.Combine(dir, "speakers.json")).Speakers[0].Clip);
            Assert.Contains("**Speaker 1:**", File.ReadAllText(Path.Combine(dir, "meeting.md")));
        }
        finally
        {
            File.SetUnixFileMode(clips, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void ALeftoverSpeakersOldFileDoesNotStopTheLabels()
    {
        var dir = Meeting("old");
        SeedOld(dir);
        File.WriteAllText(Path.Combine(dir, "speakers.old"), "in the way");
        var r = Run(new FakeWhisper(), new FakeStep(), dir);
        Assert.Equal(0, r.Code);
        Assert.Equal(1, SpeakersFile.Read(Path.Combine(dir, "speakers.json")).Version);
        Assert.Equal("new clip", File.ReadAllText(Path.Combine(dir, "speakers", "S1.wav")));
        Assert.False(File.Exists(Path.Combine(dir, "speakers", "S9.wav")));
    }
}
