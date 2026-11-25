using Sakshi.Speakers;
using Sakshi.Transcribe;

namespace Sakshi.Speakers.Tests;

public sealed class SpeakerLabellerTests : IDisposable
{
    private const int Rate = 16000;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sakshi-lab-" + Guid.NewGuid().ToString("N"));
    private string Out => Path.Combine(_dir, "2026-11-20-Fri", "steering");
    private string Voices => Path.Combine(_dir, "voices", "voices.json");
    private static readonly float[] Mean = Background.Seed().Mean;
    private static readonly FrameRecord[] Frames = [new(1.5, "frame_00-00-01.jpg")];

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    // The background mean plus a unit step along one axis (and a little along another), so after
    // normalisation the print points along that axis.
    private static float[] V(int axis, int nudgeAxis = -1)
    {
        var v = (float[])Mean.Clone();
        v[axis] += 1f;
        if (nudgeAxis >= 0) v[nudgeAxis] += 0.1f;
        return v;
    }

    private static TimeSpan T(double s) => TimeSpan.FromSeconds(s);

    // One word per second from `from` to `to`, the last ending a sentence.
    private static Segment Seg(int from, int to, string? text = null)
    {
        var words = new List<Word>();
        for (var k = from; k < to; k++)
            words.Add(new Word(T(k + 0.1), T(k + 0.9), $" w{k}" + (k == to - 1 ? "." : "")));
        if (text is not null)
        {
            var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(to - from, parts.Length);
            words = words.Select((w, i) => w with { Text = " " + parts[i] }).ToList();
        }
        return new Segment(T(from), T(to), string.Concat(words.Select(w => w.Text))) { Words = words };
    }

    private static float[] Audio(double seconds, Func<int, float>? at = null)
    {
        var s = new float[(int)(seconds * Rate)];
        if (at is not null) for (var i = 0; i < s.Length; i++) s[i] = at(i);
        return s;
    }

    private sealed class FakeDiarizer(params PieceTurn[] turns) : IDiarizer
    {
        public int Calls { get; private set; }
        public IReadOnlyList<PieceTurn> Diarize(float[] samples, CancellationToken ct) { Calls++; return turns; }
        public void Dispose() { }
    }

    private sealed class FakePrinter(Dictionary<double, float[]> byFirstStart) : IVoiceprinter
    {
        public List<IReadOnlyList<(double Start, double End)>> Calls { get; } = [];
        public string ModelName => "fake_model";
        public float[] Compute(float[] samples, IReadOnlyList<(double Start, double End)> spans)
        {
            Calls.Add(spans.ToList());
            return (float[])byFirstStart[spans[0].Start].Clone();
        }
        public void Dispose() { }
    }

    private SpeakerOutcome Run(IDiarizer d, IVoiceprinter p, IReadOnlyList<Segment> segs, float[] audio,
        VoiceLibrary? lib = null, bool writable = false, TextWriter? stdout = null) =>
        new SpeakerLabeller(d, p, lib ?? VoiceLibrary.Load(Voices), writable)
            .Label(audio, Out, segs, "steering.mp4", Frames, stdout ?? TextWriter.Null, CancellationToken.None);

    private string SpeakersJson => Path.Combine(Out, "speakers.json");

    [Fact]
    public void NoTurnsMeansNoLabels()
    {
        Segment[] segs = [Seg(0, 5)];
        var r = Run(new FakeDiarizer(), new FakePrinter([]), segs, Audio(5));
        Assert.Equal(0, r.Speakers);
        Assert.Same(segs, r.Segments);
        Assert.False(File.Exists(SpeakersJson));
    }

    [Fact]
    public void NoWordsMeansNoLabels()
    {
        Segment[] segs = [new Segment(T(0), T(5), " hello")];
        var r = Run(new FakeDiarizer(new PieceTurn(0, 5, 0)), new FakePrinter(new() { [0] = V(0) }), segs, Audio(5));
        Assert.Equal(0, r.Speakers);
        Assert.Same(segs, r.Segments);
        Assert.False(File.Exists(SpeakersJson));
    }

    [Fact]
    public void NoPrintsMeansNoLabels()
    {
        Segment[] segs = [Seg(0, 4)];
        var r = Run(new FakeDiarizer(new PieceTurn(0, 2, 0), new PieceTurn(2, 4, 1)), new FakePrinter([]), segs, Audio(4));
        Assert.Equal(0, r.Speakers);
        Assert.Same(segs, r.Segments);
        Assert.False(File.Exists(SpeakersJson));
    }

    [Fact]
    public void PiecesWithSimilarPrintsAreJoined()
    {
        var d = new FakeDiarizer(new PieceTurn(0, 10, 0), new PieceTurn(10, 20, 1), new PieceTurn(20, 30, 2));
        var p = new FakePrinter(new() { [0] = V(0), [10] = V(1), [20] = V(0, 5) });
        var r = Run(d, p, [Seg(0, 10), Seg(10, 20), Seg(20, 30)], Audio(30));
        Assert.Equal(2, r.Speakers);
        Assert.Equal(["S1", "S2", "S1"], r.Segments.Select(s => s.Speaker));
        Assert.Equal(["Speaker 1", "Speaker 2", "Speaker 1"], r.Segments.Select(s => s.SpeakerLabel));
        Assert.Equal(0, r.Suggested);
        Assert.Equal(2, r.Unknown);
    }

    [Fact]
    public void SpeakersAreNumberedByFirstTurn()
    {
        // Piece numbers from the diarizer do not decide the numbering; the first turn does.
        var d = new FakeDiarizer(new PieceTurn(10, 20, 0), new PieceTurn(0, 10, 1));
        var p = new FakePrinter(new() { [10] = V(0), [0] = V(1) });
        var r = Run(d, p, [Seg(0, 10), Seg(10, 20)], Audio(20));
        Assert.Equal(["S1", "S2"], r.Segments.Select(s => s.Speaker));
        var file = SpeakersFile.Read(SpeakersJson);
        Assert.Equal(V(1), file.Speakers[0].Voiceprint);
    }

    [Fact]
    public void ShortPieceIsGivenToTheTurnBefore()
    {
        var d = new FakeDiarizer(new PieceTurn(0, 10, 0), new PieceTurn(10, 12, 1), new PieceTurn(12, 24, 2));
        var p = new FakePrinter(new() { [0] = V(0), [12] = V(1) });
        var r = Run(d, p, [Seg(0, 10), Seg(10, 12), Seg(12, 24)], Audio(24));
        Assert.Equal(2, p.Calls.Count);
        Assert.Equal(2, r.Speakers);
        Assert.Equal(["S1", "S1", "S2"], r.Segments.Select(s => s.Speaker));
        var file = SpeakersFile.Read(SpeakersJson);
        Assert.Equal(12, file.Speakers[0].Seconds, 3);
    }

    [Fact]
    public void ShortFirstPieceIsGivenToTheTurnAfter()
    {
        var d = new FakeDiarizer(new PieceTurn(0, 2, 1), new PieceTurn(2, 12, 0));
        var p = new FakePrinter(new() { [2] = V(0) });
        var r = Run(d, p, [Seg(0, 2), Seg(2, 12)], Audio(12));
        Assert.Equal(1, r.Speakers);
        Assert.Equal(["S1", "S1"], r.Segments.Select(s => s.Speaker));
    }

    [Fact]
    public void SuggestionFromTheLibrary()
    {
        var lib = VoiceLibrary.Load(Voices);
        lib.Teach("Ada", V(0), "2026-11-01-Sun/other", "S3", DateTimeOffset.UnixEpoch);
        var d = new FakeDiarizer(new PieceTurn(0, 10, 0), new PieceTurn(10, 20, 1));
        var p = new FakePrinter(new() { [0] = V(0), [10] = V(1) });
        var r = Run(d, p, [Seg(0, 10), Seg(10, 20)], Audio(20), lib);
        Assert.Equal(1, r.Suggested);
        Assert.Equal(1, r.Unknown);
        Assert.Equal(["Ada?", "Speaker 2"], r.Segments.Select(s => s.SpeakerLabel));
        var s1 = SpeakersFile.Read(SpeakersJson).Speakers[0];
        Assert.Equal(SpeakerStatus.Suggested, s1.Status);
        Assert.Equal("Ada", s1.Suggestion);
        Assert.Equal(1.0, s1.Score!.Value, 3);
        Assert.Equal(lib.Find("Ada")!.Id, s1.PersonId);
        Assert.Null(s1.Name);
        Assert.Equal("Ada?", s1.Label);
    }

    [Fact]
    public void SpeakersFileAndClipsAreWritten()
    {
        // Each sample holds its own time / 100, so a clip shows where it was cut from.
        var audio = Audio(20, i => i / (float)Rate / 100f);
        var d = new FakeDiarizer(new PieceTurn(0, 3, 0), new PieceTurn(4, 14, 0), new PieceTurn(14, 17, 1));
        var p = new FakePrinter(new() { [4] = V(0), [14] = V(1) });
        var longText = string.Join(' ', Enumerable.Range(0, 10).Select(i => $"abcdefghijklm{i}"));
        Segment[] segs = [Seg(0, 3, "First short one."), Seg(4, 14, longText + "."), Seg(14, 17, "Yes I agree.")];
        var r = Run(d, p, segs, audio);

        var file = SpeakersFile.Read(SpeakersJson);
        Assert.Equal(1, file.Version);
        Assert.Equal("fake_model", file.EmbeddingModel);
        Assert.Equal("steering.mp4", file.Source);
        Assert.Equal(Frames, file.Frames);
        Assert.Equal(["S1", "S2"], file.Speakers.Select(s => s.Id));
        var (s1, s2) = (file.Speakers[0], file.Speakers[1]);
        Assert.Equal(SpeakerStatus.Unknown, s1.Status);
        Assert.Null(s1.Suggestion);
        Assert.Null(s1.Score);
        Assert.Equal(13, s1.Seconds, 3);
        Assert.Equal(3, s2.Seconds, 3);
        Assert.Equal((longText + ".")[..120], s1.Sample);
        Assert.Equal("Yes I agree.", s2.Sample);
        Assert.Equal("speakers/S1.wav", s1.Clip);
        Assert.Equal("speakers/S2.wav", s2.Clip);
        Assert.Equal(V(0), s1.Voiceprint);

        // S1: 5 s from the middle (9 s) of its longest turn 4-14.
        var (rate1, c1) = WavFile.Read(Path.Combine(Out, "speakers", "S1.wav"));
        Assert.Equal(Rate, rate1);
        Assert.Equal(5 * Rate, c1.Length);
        Assert.Equal(0.065, c1[0], 3);
        // S2: its only turn is 3 s, so the whole turn.
        var (_, c2) = WavFile.Read(Path.Combine(Out, "speakers", "S2.wav"));
        Assert.Equal(3 * Rate, c2.Length);
        Assert.Equal(0.14, c2[0], 3);
        Assert.Equal(2, r.Speakers);
    }

    [Fact]
    public void ClipIsClampedToTheAudio()
    {
        // The turn runs past the end of the audio; the clip stops where the audio does.
        var d = new FakeDiarizer(new PieceTurn(0, 20, 0));
        var p = new FakePrinter(new() { [0] = V(0) });
        Run(d, p, [Seg(0, 12)], Audio(12));
        var (_, c) = WavFile.Read(Path.Combine(Out, "speakers", "S1.wav"));
        Assert.Equal((int)(4.5 * Rate), c.Length); // middle of 0-20 is 10 s: 7.5 s up to the end at 12 s
    }

    [Fact]
    public void BackgroundGrowsOnlyWhenWritable()
    {
        var d = new FakeDiarizer(new PieceTurn(0, 10, 0), new PieceTurn(10, 20, 1), new PieceTurn(20, 21, 2));
        var p = new FakePrinter(new() { [0] = V(0), [10] = V(1) });
        Segment[] segs = [Seg(0, 10), Seg(10, 21)];

        Run(d, p, segs, Audio(21), VoiceLibrary.Load(Voices), writable: false);
        Assert.False(File.Exists(Voices));

        Run(d, p, segs, Audio(21), VoiceLibrary.Load(Voices), writable: true);
        var back = VoiceLibrary.Load(Voices);
        Assert.Equal(2, back.BackgroundSum.Count);
        var expected = V(0).Zip(V(1), (a, b) => (double)a + b).ToArray();
        Assert.Equal(expected, back.BackgroundSum.Sum!, (a, b) => Math.Abs(a - b) < 1e-5);
    }

    [Fact]
    public void PrintsUseOnlyLongTurnsUpTo60Seconds()
    {
        var d = new FakeDiarizer(
            new PieceTurn(0, 1, 0), new PieceTurn(2, 32, 0), new PieceTurn(40, 65, 0),
            new PieceTurn(70, 80, 0), new PieceTurn(85, 86.4, 0));
        var p = new FakePrinter(new() { [2] = V(0) });
        Run(d, p, [Seg(0, 5)], Audio(87));
        var spans = Assert.Single(p.Calls);
        Assert.Equal([(2.0, 32.0), (40.0, 65.0), (70.0, 75.0)], spans);
    }

    [Fact]
    public void CancelledBeforeStartThrows()
    {
        var d = new FakeDiarizer(new PieceTurn(0, 10, 0));
        var p = new FakePrinter(new() { [0] = V(0) });
        var labeller = new SpeakerLabeller(d, p, VoiceLibrary.Load(Voices), false);
        Assert.Throws<OperationCanceledException>(() =>
            labeller.Label(Audio(10), Out, [Seg(0, 10)], "s", null, TextWriter.Null, new CancellationToken(true)));
    }

    [Fact]
    public void SpeakerWithoutWordsIsDropped()
    {
        // Piece 1 speaks in a stretch with no words: it is left out and the next speaker is S2, not S3.
        var d = new FakeDiarizer(new PieceTurn(0, 10, 0), new PieceTurn(10, 14, 1), new PieceTurn(14, 24, 2));
        var p = new FakePrinter(new() { [0] = V(0), [10] = V(1), [14] = V(2) });
        var r = Run(d, p, [Seg(0, 10), Seg(14, 24)], Audio(24), VoiceLibrary.Load(Voices), writable: true);
        Assert.Equal(2, r.Speakers);
        Assert.Equal(2, r.Unknown);
        Assert.Equal(["S1", "S2"], r.Segments.Select(s => s.Speaker));
        Assert.Equal(["Speaker 1", "Speaker 2"], r.Segments.Select(s => s.SpeakerLabel));
        var file = SpeakersFile.Read(SpeakersJson);
        Assert.Equal(["S1", "S2"], file.Speakers.Select(s => s.Id));
        Assert.Equal(V(2), file.Speakers[1].Voiceprint);
        Assert.Equal(["S1.wav", "S2.wav"],
            Directory.GetFiles(Path.Combine(Out, "speakers")).Select(Path.GetFileName).Order());
        // The dropped piece's print still counts towards the background.
        Assert.Equal(3, VoiceLibrary.Load(Voices).BackgroundSum.Count);
    }

    [Fact]
    public void SpeakersAreNumberedByFirstWords()
    {
        // Piece 0 has the first turn but says nothing until 20 s; piece 1 speaks first.
        var d = new FakeDiarizer(new PieceTurn(0, 4, 0), new PieceTurn(4, 20, 1), new PieceTurn(20, 30, 0));
        var p = new FakePrinter(new() { [20] = V(0), [4] = V(1) });
        var r = Run(d, p, [Seg(4, 20), Seg(20, 30)], Audio(30));
        Assert.Equal(["S1", "S2"], r.Segments.Select(s => s.Speaker));
        var file = SpeakersFile.Read(SpeakersJson);
        Assert.Equal(V(1), file.Speakers[0].Voiceprint);
        Assert.Equal(V(0), file.Speakers[1].Voiceprint);
        Assert.Equal(14, file.Speakers[1].Seconds, 3);
    }

    [Fact]
    public void NoLabelsRemovesAnEarlierRunsOutput()
    {
        var p = new FakePrinter(new() { [0] = V(0), [10] = V(1) });
        Segment[] segs = [Seg(0, 10), Seg(10, 20)];
        Run(new FakeDiarizer(new PieceTurn(0, 10, 0), new PieceTurn(10, 20, 1)), p, segs, Audio(20));
        var clips = Path.Combine(Out, "speakers");
        Assert.True(File.Exists(SpeakersJson));
        File.WriteAllText(Path.Combine(clips, "notes.txt"), "not ours");

        var r = Run(new FakeDiarizer(), p, segs, Audio(20));
        Assert.Equal(0, r.Speakers);
        Assert.False(File.Exists(SpeakersJson));
        Assert.Equal(["notes.txt"], Directory.GetFiles(clips).Select(Path.GetFileName));
    }

    [Fact]
    public void NoLabelsWithoutAnEarlierRunWritesNothing()
    {
        Run(new FakeDiarizer(), new FakePrinter([]), [Seg(0, 5)], Audio(5));
        Assert.False(Directory.Exists(Out));
    }

    [Fact]
    public void EmptyAudioMeansNoLabels()
    {
        var d = new FakeDiarizer(new PieceTurn(0, 10, 0));
        var r = Run(d, new FakePrinter(new() { [0] = V(0) }), [Seg(0, 10)], []);
        Assert.Equal(0, r.Speakers);
        Assert.Equal(0, d.Calls);
    }

    [Fact]
    public void LibrarySaveFailureIsAWarning()
    {
        Directory.CreateDirectory(Voices); // a folder where the file should be: loads as empty, cannot be saved
        var output = new StringWriter();
        var d = new FakeDiarizer(new PieceTurn(0, 10, 0));
        var r = Run(d, new FakePrinter(new() { [0] = V(0) }), [Seg(0, 10)], Audio(10),
            VoiceLibrary.Load(Voices), writable: true, stdout: output);
        Assert.Equal(1, r.Speakers);
        Assert.True(File.Exists(SpeakersJson));
        Assert.StartsWith("Warning: voices not saved: ", output.ToString());
        Assert.Single(output.ToString().TrimEnd().Split('\n'));
    }

    [Fact]
    public void SampleIsTheLongestSegmentWithWords()
    {
        // The wordless 9 s note is longer, but the sample is the spoken segment.
        var d = new FakeDiarizer(new PieceTurn(0, 12, 0));
        var p = new FakePrinter(new() { [0] = V(0) });
        Segment[] segs = [new Segment(T(0), T(9), " [Music]"), Seg(9, 12, "Hello there all.")];
        Run(d, p, segs, Audio(12));
        Assert.Equal("Hello there all.", SpeakersFile.Read(SpeakersJson).Speakers[0].Sample);
    }

    [Fact]
    public void BackgroundUpdateKeepsAChangeMadeToTheLibraryMeanwhile()
    {
        var lib = VoiceLibrary.Load(Voices);
        lib.Teach("Ada", V(0), "2026-11-01-Sun/other", "S3", DateTimeOffset.UnixEpoch);
        lib.Save();
        var stale = VoiceLibrary.Load(Voices);
        // While the meeting is being labelled, Ada is forgotten.
        var other = VoiceLibrary.Load(Voices);
        other.Forget("Ada");
        other.Save();

        var d = new FakeDiarizer(new PieceTurn(0, 10, 0));
        Run(d, new FakePrinter(new() { [0] = V(1) }), [Seg(0, 10)], Audio(10), stale, writable: true);
        var back = VoiceLibrary.Load(Voices);
        Assert.Empty(back.People);
        Assert.Equal(1, back.BackgroundSum.Count);
    }

    [Fact]
    public void LibraryUnreadableAtSaveTimeIsLeftAlone()
    {
        var lib = VoiceLibrary.Load(Voices);
        Directory.CreateDirectory(Path.GetDirectoryName(Voices)!);
        File.WriteAllText(Voices, "nope");
        var output = new StringWriter();
        var d = new FakeDiarizer(new PieceTurn(0, 10, 0));
        var r = Run(d, new FakePrinter(new() { [0] = V(1) }), [Seg(0, 10)], Audio(10), lib, writable: true, stdout: output);
        Assert.Equal(1, r.Speakers);
        Assert.Equal("nope", File.ReadAllText(Voices));
        Assert.StartsWith("Warning: Cannot read the voice library " + Voices, output.ToString());
        Assert.Contains("; voices are not saved this time", output.ToString());
    }
}
