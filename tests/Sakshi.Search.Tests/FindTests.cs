using Sakshi.Search;
using Sakshi.TestSupport;

namespace Sakshi.Search.Tests;

public sealed class FindTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "sakshi-find-" + Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly SearchIndex _index;

    public FindTests()
    {
        _root = Path.Combine(_tmp, "meetings");
        Directory.CreateDirectory(_root);
        _index = SearchIndex.Open(Path.Combine(_tmp, "index.db"));
    }

    public void Dispose()
    {
        _index.Dispose();
        try { Directory.Delete(_tmp, recursive: true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private void Make(string day, string name, params (long, string)[] lines) =>
        MeetingFixture.Make(_root, day, name, lines);

    private void Build() => _index.Refresh([_root], _ => { });

    private (IReadOnlyList<Result> Results, IReadOnlyDictionary<string, IReadOnlyList<string>> NearUsed) Find(params string[] words)
    {
        Build();
        return Finder.Search(_index, words);
    }

    private static string Fmt(IReadOnlyList<Result> r, IReadOnlyDictionary<string, IReadOnlyList<string>> n,
        string[] words, bool all = false, bool color = false) =>
        ResultFormatter.Format(r, n, words, all, color);

    [Fact]
    public void NameOnlyMatchIsFound()
    {
        Make("2026-11-04-Wed", "catholic_keys", (0, "Nothing relevant"));
        var (results, _) = Find("catholic");
        Assert.Equal([("exact", "catholic_keys")], results.Select(r => (r.Tier, r.Name)).ToArray());
        Assert.Null(results[0].Hits[0].StartMs);
    }

    [Fact]
    public void TranscriptOnlyMatchIsFound()
    {
        Make("2026-05-27-Wed", "05_07", (0, "hi"), (4000, "the Catholic diocese"));
        var (results, _) = Find("catholic");
        Assert.Single(results);
        Assert.Equal(4000, results[0].Hits[0].StartMs);
    }

    [Fact]
    public void MisspelledMeetingIsInNearTier()
    {
        Make("2026-10-16-Fri", "weekly", (0, "the Katholic diocese"));
        Make("2026-11-04-Wed", "other", (0, "the Catholic one"));
        var (results, near) = Find("catholic");
        Assert.Equal(["exact", "near"], results.Select(r => r.Tier).ToArray());
        Assert.Contains("katholic", near["catholic"]);
    }

    [Fact]
    public void ExactBeforeNearAndNewestFirst()
    {
        Make("2026-05-27-Wed", "a", (0, "Catholic"));
        Make("2026-10-16-Fri", "b", (0, "Katholic"));
        Make("2026-11-04-Wed", "c", (0, "Catholic"));
        var (results, _) = Find("catholic");
        Assert.Equal(
            [("exact", "2026-11-04-Wed"), ("exact", "2026-05-27-Wed"), ("near", "2026-10-16-Fri")],
            results.Select(r => (r.Tier, r.Day)).ToArray());
    }

    [Fact]
    public void AllWordsMustOccurSomewhereInTheMeeting()
    {
        Make("2026-11-04-Wed", "both", (0, "Catholic"), (1000, "budget"));
        Make("2026-11-05-Thu", "one", (0, "Catholic only"));
        var (results, _) = Find("catholic", "budget");
        Assert.Equal(["both"], results.Select(r => r.Name).ToArray());
    }

    [Fact]
    public void FtsSyntaxInQueryIsHarmless()
    {
        Make("2026-11-04-Wed", "m", (0, "o'brien and c++ -x"));
        Build();
        foreach (var q in new[] { "o'brien", "c++", "AND", "\"", "*", "-x", "NEAR(a b)" })
            Finder.Search(_index, q.Split(' '));
        var (results, near) = Finder.Search(_index, ["*"]);
        Assert.Empty(results);
        Assert.Empty(near);
    }

    [Fact]
    public void TokensMatchPythonForViramaAndAccents()
    {
        // python3: re.findall(r"(?:[^\W_]|[\u0300-\u036f])+", "सत्य café_x") == ['सत', 'य', 'café', 'x']
        // (virama U+094D is a Mn mark outside the combining-diacritics range, so it splits the word)
        Assert.Equal(["सत", "य", "cafe", "x"], Finder.QueryWords(["सत्य café_x"]).ToArray());
    }

    [Fact]
    public void AccentedAndUppercaseQuery()
    {
        Make("2026-11-04-Wed", "m", (0, "Talk to José today"));
        var (results, _) = Find("JOSE");
        Assert.Single(results);
    }

    [Fact]
    public void HitsCappedAtThreeUnlessAll()
    {
        Make("2026-11-04-Wed", "m", Enumerable.Range(0, 5).Select(i => ((long)i * 1000, $"keys number {i}")).ToArray());
        var (results, near) = Find("keys");
        Assert.Equal(5, results[0].TotalHits);
        var shortOut = Fmt(results, near, ["keys"]);
        Assert.Equal(3, Count(shortOut, "[00:"));
        Assert.Contains("… and 2 more — add --all to see every match", shortOut);
        var full = Fmt(results, near, ["keys"], all: true);
        Assert.Equal(5, Count(full, "[00:"));
        Assert.DoesNotContain("add --all", full);
    }

    private static int Count(string s, string sub)
    {
        var n = 0;
        for (var i = s.IndexOf(sub, StringComparison.Ordinal); i >= 0; i = s.IndexOf(sub, i + sub.Length, StringComparison.Ordinal))
            n++;
        return n;
    }

    [Fact]
    public void NoResultsMessage() =>
        Assert.Equal("No meetings mention “zebra”.",
            Fmt([], new Dictionary<string, IReadOnlyList<string>>(), ["zebra"]).Trim());

    [Fact]
    public void FormatTwoTiersAndColor()
    {
        Make("2026-11-04-Wed", "catholic_keys", (3661000, "The Catholic diocese"));
        Make("2026-10-16-Fri", "weekly", (0, "the Katholic one"));
        for (var i = 0; i < 6; i++)
            Make($"2026-09-0{i + 1}-Mon", $"filler{i}", (0, "nothing here"));
        var (results, near) = Find("catholic");
        var plain = Fmt(results, near, ["catholic"]);
        Assert.Contains("Exact matches\n", plain);
        Assert.Contains("Near spellings (searched also for: catholic → katholic)", plain);
        Assert.Contains("[01:01:01] The Catholic diocese", plain);
        Assert.Contains("(name) catholic keys", plain);
        Assert.DoesNotContain("\x1b", plain, StringComparison.Ordinal); // ESC is culture-ignorable
        var colored = Fmt(results, near, ["catholic"], color: true);
        Assert.Contains("\x1b[1mCatholic\x1b[0m", colored, StringComparison.Ordinal);
        Assert.Contains("\x1b[1mKatholic\x1b[0m", colored, StringComparison.Ordinal);
    }

    [Fact]
    public void ExactLineShownBeforeEarlierNearSpellingLine()
    {
        Make("2026-11-04-Wed", "m",
            (1000, "changes one"), (2000, "changes two"), (3000, "changes three"), (9000, "Charles said hi"));
        Make("2026-11-03-Tue", "n", (0, "changes only"));
        var (results, near) = Find("charles");
        var r = results[0];
        Assert.Equal("exact", r.Tier);
        Assert.Equal(9000, r.Hits[0].StartMs);
        var output = Fmt(results, near, ["charles"]);
        Assert.Contains("[00:00:09] Charles said hi", output);
    }

    [Fact]
    public void HitOrderNameThenExactThenNear()
    {
        Make("2026-11-04-Wed", "charles_sync", (1000, "changes"), (5000, "Charles"), (3000, "charles again"));
        var (results, _) = Find("charles");
        Assert.Equal(new long?[] { null, 3000, 5000, 1000 }, results[0].Hits.Select(h => h.StartMs).ToArray());
    }

    [Fact]
    public void NearHeadingNamesOnlySpellingsThatMakeNearMeetings()
    {
        Make("2026-11-04-Wed", "a", (0, "Charles and changes"));
        Make("2026-11-03-Tue", "b", (0, "the Charls one"));
        for (var i = 0; i < 6; i++)
            Make($"2026-09-0{i + 1}-Mon", $"filler{i}", (0, "nothing here"));
        var (results, near) = Find("charles");
        Assert.Equal(["exact", "near"], results.Select(r => r.Tier).ToArray());
        Assert.Contains("charls", near["charles"]);
        Assert.DoesNotContain("changes", near["charles"]);
        var output = Fmt(results, near, ["charles"]);
        Assert.Contains("searched also for: charles → charls)", output);
    }

    private void CommonFixture()
    {
        Make("2026-11-10-Tue", "exact_one", (0, "Sears called"));
        for (var i = 0; i < 3; i++)
            Make($"2026-11-0{i + 1}-Mon", $"yr{i}", (0, "in the years before"));
        for (var i = 0; i < 3; i++)
            Make($"2026-10-0{i + 1}-Thu", $"filler{i}", (0, "nothing here"));
    }

    [Fact]
    public void CommonLookAlikeMeetingsAreCollapsed()
    {
        CommonFixture();
        var (results, near) = Find("sears");
        var nearR = results.Where(r => r.Tier == "near").ToList();
        Assert.Equal(3, nearR.Count);
        Assert.All(nearR, r => Assert.True(r.Collapsed));
        Assert.False(results[0].Collapsed);
        var output = Fmt(results, near, ["sears"]);
        Assert.Contains("Sears called", output);
        Assert.DoesNotContain("yr0", output);
        Assert.DoesNotContain("Near spellings", output);
        Assert.Contains("… and 3 more meetings only match common look-alikes "
            + "(sears → years) — add --all to see them", output);
    }

    [Fact]
    public void AllListsCollapsedMeetings()
    {
        CommonFixture();
        var (results, near) = Find("sears");
        var output = Fmt(results, near, ["sears"], all: true);
        Assert.Contains("yr0", output);
        Assert.Contains("Near spellings (searched also for: sears → years)", output);
        Assert.DoesNotContain("common look-alikes", output);
    }

    [Fact]
    public void RareLookAlikeStaysListed()
    {
        Make("2026-11-10-Tue", "a", (0, "Catholic"));
        Make("2026-11-09-Mon", "b", (0, "the Katholic one"));
        for (var i = 0; i < 4; i++)
            Make($"2026-10-0{i + 1}-Thu", $"filler{i}", (0, "nothing here"));
        var (results, near) = Find("catholic");
        Assert.False(results[1].Collapsed);
        var output = Fmt(results, near, ["catholic"]);
        Assert.Contains("Katholic", output);
        Assert.DoesNotContain("common look-alikes", output);
    }

    [Fact]
    public void CollapsedLineAloneWhenNoExactMeetings()
    {
        for (var i = 0; i < 3; i++)
            Make($"2026-11-0{i + 1}-Mon", $"yr{i}", (0, "in the years before"));
        Make("2026-10-01-Thu", "filler", (0, "nothing"));
        var (results, near) = Find("sears");
        Assert.Equal(3, results.Count);
        var output = Fmt(results, near, ["sears"]);
        Assert.Contains("… and 3 more meetings only match common look-alikes", output);
        Assert.DoesNotContain("Exact matches", output);
    }

    [Fact]
    public void LongLineSnippetIsCutAroundMatch()
    {
        var text = string.Concat(Enumerable.Repeat("word ", 60)) + "needle " + string.Concat(Enumerable.Repeat("word ", 60));
        Make("2026-11-04-Wed", "m", (0, text));
        var (results, near) = Find("needle");
        var output = Fmt(results, near, ["needle"]);
        var line = output.Split('\n').First(l => l.StartsWith("  ["));
        var snippet = line[(line.IndexOf("] ", StringComparison.Ordinal) + 2)..];
        Assert.True(snippet.Length <= 120);
        Assert.Contains("needle", snippet);
        Assert.True(snippet.StartsWith('…') && snippet.EndsWith('…'));
    }
}
