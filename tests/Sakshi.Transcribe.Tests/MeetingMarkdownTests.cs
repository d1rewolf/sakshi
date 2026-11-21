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
}
