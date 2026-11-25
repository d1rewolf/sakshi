namespace Sakshi.Transcribe.Tests;

public class MeetingMarkdownTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "sakshi-md-" + Guid.NewGuid().ToString("N"));

    public MeetingMarkdownTests() => Directory.CreateDirectory(_tmp);
    public void Dispose() => Directory.Delete(_tmp, true);

    private string Render(string fixture, bool noFrames)
    {
        var work = Path.Combine(_tmp, fixture);
        Fixtures.CopyDir(Fixtures.Dir(fixture), work);
        var segs = Fixtures.ReadSegments(Path.Combine(work, "transcript.json"));
        var framesDir = Path.Combine(work, "frames");
        var meta = Path.Combine(framesDir, ".metadata.txt");
        var frames = File.Exists(meta)
            ? FrameFiles.RenameRaw(framesDir, FrameFiles.ParseMetadata(File.ReadAllText(meta)))
            : [];
        return MeetingMarkdown.Render("weekly.sync.mp4", frames, segs, noFrames);
    }

    private static string Expected(string fixture) =>
        File.ReadAllText(Path.Combine(Fixtures.Dir(fixture), "expected.md"));

    [Fact] public void Render_MatchesPython_Frames() => Assert.Equal(Expected("frames"), Render("frames", false));
    [Fact] public void Render_MatchesPython_NoFrames() => Assert.Equal(Expected("noframes"), Render("noframes", true));
    [Fact] public void Render_MatchesPython_Empty() => Assert.Equal(Expected("empty"), Render("empty", false));

    private static Segment Sp(double from, string text, string? label) =>
        new(TimeSpan.FromSeconds(from), TimeSpan.FromSeconds(from + 1), text)
        { Speaker = label is null ? null : "S1", SpeakerLabel = label };

    private static string[] Quotes(string md) => md.Split('\n').Where(l => l.StartsWith("> ")).ToArray();

    [Fact]
    public void MarkdownShowsLabelWhenSpeakerChanges()
    {
        var md = MeetingMarkdown.Render("a.mp4", [new FrameRecord(0, "f.jpg")],
            [Sp(1, "one", "Ada"), Sp(3, "two", "Ada"), Sp(5, "three", "Ben")], false);
        Assert.Equal(["> [00:00:01 → 00:00:02] **Ada:** one", "> [00:00:03 → 00:00:04] two", "> [00:00:05 → 00:00:06] **Ben:** three"], Quotes(md));
    }

    [Fact]
    public void MarkdownRepeatsLabelAfterEachHeading()
    {
        var frames = new[] { new FrameRecord(10, "f.jpg") };
        var md = MeetingMarkdown.Render("a.mp4", frames,
            [Sp(1, "pre", "Ada"), Sp(2, "pre2", "Ada"), Sp(11, "post", "Ada")], false);
        var q = Quotes(md);
        Assert.Contains("**Ada:** pre", q[0]);
        Assert.DoesNotContain("**", q[1]);
        Assert.Contains("**Ada:** post", q[2]);
        var nf = Quotes(MeetingMarkdown.Render("a.mp4", [], [Sp(1, "x", "Ada"), Sp(2, "y", "Ada")], true));
        Assert.Contains("**Ada:** x", nf[0]);
        Assert.DoesNotContain("**", nf[1]);
    }

    [Fact]
    public void MarkdownListsSpeakers()
    {
        var md = MeetingMarkdown.Render("a.mp4", [new FrameRecord(0, "f.jpg")],
            [Sp(1, "a", "Ada"), Sp(2, "b", null), Sp(3, "c", "Ben?"), Sp(4, "d", "Ada"), Sp(5, "e", "Speaker 3")], false);
        var l = md.Split('\n');
        Assert.StartsWith("- Frames: 1", l[3]);
        Assert.Equal("- Speakers: Ada, Ben?, Speaker 3", l[4]);
        var plain = MeetingMarkdown.Render("a.mp4", [], [Sp(1, "a", null)], true);
        Assert.DoesNotContain("Speakers", plain);
    }

    [Fact]
    public void MarkdownEscapesLabels()
    {
        Assert.Equal("mary \\*ann\\*\\_\\[x\\]", MeetingMarkdown.EscapeLabel("mary *ann*_[x]"));
        var md = MeetingMarkdown.Render("a.mp4", [], [Sp(1, "hi", "mary *ann*_[x]")], true);
        Assert.Contains("**mary \\*ann\\*\\_\\[x\\]:** hi", md);
    }
}
