using Sakshi.Search;
using Sakshi.TestSupport;

namespace Sakshi.Search.Tests;

public sealed class SearchCommandTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "sakshi-cli-" + Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly string _index;

    public SearchCommandTests()
    {
        _root = Path.Combine(_tmp, "meetings");
        Directory.CreateDirectory(_root);
        _index = Path.Combine(_tmp, "index.db");
        MeetingFixture.Make(_root, "2026-11-04-Wed", "catholic", [(0, "hello"), (3000, "the Catholic diocese")]);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private (int Code, string Out, string Err) Run(string[] args, params string[] roots)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var code = SearchCommand.Run(args, roots.Length == 0 ? [_root] : roots, _index, o, e, isTty: false);
        return (code, o.ToString(), e.ToString());
    }

    [Fact]
    public void FindHit()
    {
        var (code, output, _) = Run(["find", "catholic"]);
        Assert.Equal(0, code);
        Assert.Contains("2026-11-04-Wed  catholic", output);
        Assert.False(output.EndsWith("\n\n"));
    }

    [Fact]
    public void FindMiss()
    {
        var (code, output, _) = Run(["find", "zebra"]);
        Assert.Equal(1, code);
        Assert.Contains("No meetings mention", output);
    }

    // Replaces Python's `find` with only `--root` (roots are now a parameter, not an option).
    [Fact]
    public void FindWithoutWordsIsUsageError()
    {
        var (code, _, err) = Run(["find"]);
        Assert.Equal(2, code);
        Assert.Contains("usage:", err);
        Assert.Equal(2, Run([]).Code);
    }

    [Fact]
    public void MissingRootIsTolerated()
    {
        var (code, _, _) = Run(["find", "catholic"], _root, Path.Combine(_tmp, "nope"));
        Assert.Equal(0, code);
    }

    [Fact]
    public void IndexRebuildReportsCount()
    {
        var (code, output, _) = Run(["index", "--rebuild"]);
        Assert.Equal(0, code);
        Assert.Equal("Indexed 1 meetings (1 new, 0 changed, 0 gone, 0 skipped).\n", output);
        (_, output, _) = Run(["index"]);
        Assert.Equal("Indexed 1 meetings (0 new, 0 changed, 0 gone, 0 skipped).\n", output);
    }

    [Fact]
    public void RebuildWithNoIndexDirectoryYet()
    {
        var o = new StringWriter();
        var path = Path.Combine(_tmp, "not", "yet", "index.db");
        var code = SearchCommand.Run(["index", "--rebuild"], [_root], path, o, new StringWriter(), isTty: false);
        Assert.Equal(0, code);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void HelpPrintsUsageToStdout()
    {
        var (code, output, _) = Run(["find", "--help"]);
        Assert.Equal(0, code);
        Assert.StartsWith("usage: sakshi find [-h] [--all] WORD [WORD ...]", output);
    }
}
