using Sakshi.Search;
using Sakshi.TestSupport;

namespace Sakshi.Search.Tests;

public sealed class CorpusTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sakshi-corpus-" + Guid.NewGuid().ToString("N"));

    public CorpusTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private static (long, string)[] Hi => [(0, "hi")];

    private string[] Nested()
    {
        MeetingFixture.Make(_root, "2026-11-04-Wed", "catholic", Hi);
        MeetingFixture.Make(Path.Combine(_root, "archives"), "2026-09-25-Fri", "Brighstar/Brighstar", Hi);
        return [_root, Path.Combine(_root, "archives")];
    }

    [Fact]
    public void FindsMeetingsAtAnyDepthUnderDayFolders()
    {
        var found = Corpus.Discover(Nested()).Select(m => (m.Day, m.Name)).ToHashSet();
        Assert.Equal(
            new HashSet<(string, string)>
            {
                ("2026-11-04-Wed", "catholic"),
                ("2026-09-25-Fri", "Brighstar/Brighstar"),
            },
            found);
    }

    [Fact]
    public void ArchiveInsideMeetingsIsNotListedTwice() =>
        Assert.Equal(2, Corpus.Discover(Nested()).Count);

    [Fact]
    public void Signature_ChangesWhenContentRestoredWithOldMtime()
    {
        var d = MeetingFixture.Make(_root, "2026-11-04-Wed", "m", [(0, "hi")]);
        var path = Path.Combine(d, "transcript.json");
        var original = File.GetLastWriteTimeUtc(path);
        var m1 = Assert.Single(Corpus.Discover([_root]));
        Assert.Equal(d, m1.Dir);
        Assert.Equal(m1.Signature, Corpus.Discover([_root])[0].Signature);

        File.WriteAllText(path, File.ReadAllText(path).Replace("hi", "ho"));
        File.SetLastWriteTimeUtc(path, original);
        var m2 = Assert.Single(Corpus.Discover([_root]));
        Assert.NotEqual(m1.Signature, m2.Signature);
    }

    [Fact]
    public void IgnoresLooseFilesAndNonDayFolders()
    {
        var d = Path.Combine(_root, "2025-01-02-Thu");
        Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(d, "x.srt"), "1\n");
        MeetingFixture.Make(_root, "notes", "misc", Hi);
        Assert.Empty(Corpus.Discover([_root]));
    }

    [Fact]
    public void MissingRootIsSkipped() =>
        Assert.Empty(Corpus.Discover([Path.Combine(_root, "nope")]));

    [Fact]
    public void ReadLinesStripsTextAndKeepsOffsets()
    {
        MeetingFixture.Make(_root, "2026-11-04-Wed", "m", [(2580, "  >> Hang on. ")]);
        Assert.Equal([new Line(2580, ">> Hang on.")],
            Corpus.ReadLines(Path.Combine(_root, "2026-11-04-Wed", "m")));
    }

    [Fact]
    public void ReadLinesDropsEmptyText()
    {
        MeetingFixture.Make(_root, "2026-11-04-Wed", "m", [(0, "  "), (5, "x")]);
        Assert.Equal([new Line(5, "x")], Corpus.ReadLines(Path.Combine(_root, "2026-11-04-Wed", "m")));
    }

    [Fact]
    public void ReadLinesRaisesOnTruncatedJson()
    {
        var d = Path.Combine(_root, "2026-11-04-Wed", "m");
        Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(d, "transcript.json"), "{\"transcription\": [");
        Assert.Throws<TranscriptException>(() => Corpus.ReadLines(d));
    }

    [Fact]
    public void ReadLinesRaisesOnMissingFile() =>
        Assert.Throws<TranscriptException>(() => Corpus.ReadLines(Path.Combine(_root, "nothing")));

    private static void MakeLinkOrSkip(string link, string target)
    {
        try { Directory.CreateSymbolicLink(link, target); }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            Assert.Skip("cannot create symlinks here: " + e.Message);
        }
    }

    [Fact]
    public void RootAndSymlinkToRootListEachMeetingOnce()
    {
        MeetingFixture.Make(_root, "2026-11-04-Wed", "m", Hi);
        var link = Path.Combine(Path.GetTempPath(), "sakshi-link-" + Guid.NewGuid().ToString("N"));
        MakeLinkOrSkip(link, _root);
        try
        {
            Assert.Single(Corpus.Discover([_root, link]));
        }
        finally { Directory.Delete(link); }
    }

    [Fact]
    public void RootBehindSymlinkedAncestorIsListedOnce()
    {
        var real = Path.Combine(_root, "real");
        MeetingFixture.Make(Path.Combine(real, "meetings"), "2026-11-04-Wed", "m", Hi);
        var linkParent = Path.Combine(_root, "linked");
        MakeLinkOrSkip(linkParent, real);
        Assert.Single(Corpus.Discover([Path.Combine(real, "meetings"), Path.Combine(linkParent, "meetings")]));
    }

    [Fact]
    public void FramesFolderIsNotWalked()
    {
        var d = MeetingFixture.Make(_root, "2026-11-04-Wed", "m", Hi);
        var frames = Path.Combine(d, "frames");
        Directory.CreateDirectory(frames);
        File.WriteAllText(Path.Combine(frames, "transcript.json"), "{}");
        var found = Corpus.Discover([_root]);
        Assert.Equal(["m"], found.Select(m => m.Name).ToArray());
    }
}
