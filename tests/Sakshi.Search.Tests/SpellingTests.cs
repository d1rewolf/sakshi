using System.Diagnostics;
using Sakshi.Search;

namespace Sakshi.Search.Tests;

public class SpellingTests
{
    private static readonly string[] Vocab =
    [
        "catholic", "katholic", "katolik", "catholics", "cathedral",
        "diocese", "dioceses", "jose", "cat",
    ];

    [Fact]
    public void WhisperMisspellingsOfCatholic()
    {
        var found = Spelling.NearSpellings("catholic", Vocab);
        Assert.Subset(found.ToHashSet(), new HashSet<string> { "katholic", "katolik", "catholics" });
    }

    [Fact]
    public void DoesNotPullInUnrelatedWords() =>
        Assert.DoesNotContain("cathedral", Spelling.NearSpellings("catholic", Vocab));

    [Fact]
    public void ShortWordsHaveNoNearSpellings() =>
        Assert.Empty(Spelling.NearSpellings("cat", Vocab));

    [Fact]
    public void AccentsAndCase()
    {
        Assert.Equal("jose", Spelling.Normalize("José"));
        Assert.Equal("catholic", Spelling.Normalize("CATHOLIC"));
    }

    [Fact]
    public void WordItselfIsNotReturned() =>
        Assert.DoesNotContain("diocese", Spelling.NearSpellings("Diocese", Vocab));

    [Fact]
    public void ResultIsSorted()
    {
        var result = Spelling.NearSpellings("catholic", Vocab);
        Assert.Equal(result.OrderBy(x => x, StringComparer.Ordinal), result);
    }

    [Fact]
    public void TranspositionCountsAsOneEdit() =>
        Assert.Equal(["dioecse"], Spelling.NearSpellings("diocese", ["dioecse"]));

    [Fact]
    public void LongerWordsAllowTwoEdits() =>
        Assert.Contains("dioceses", Spelling.NearSpellings("diocesan", ["dioceses"]));

    [Fact]
    public void SoundKeyAloneCanMatch() =>
        // "katolik" is 3 edits from "catholic"; only the sound key finds it.
        Assert.Contains("katolik", Spelling.NearSpellings("catholic", ["katolik"]));

    [Fact]
    public void ShortSoundKeysDoNotMatch()
    {
        Assert.Equal(["dioceses"], Spelling.NearSpellings("diocese", ["does", "dishes", "dioceses"]));
        var result = Spelling.NearSpellings("parish", ["price", "purse", "parse", "parrish"]);
        Assert.Contains("parrish", result);
        foreach (var bad in new[] { "price", "purse", "parse" })
            Assert.DoesNotContain(bad, result);
    }

    [Fact]
    public void LargeVocabularyIsFast()
    {
        var vocab = Enumerable.Range(0, 50000).Select(i => $"term{i}abc").ToList();
        Spelling.NearSpellings("catholic", ["warmup"]);
        var sw = Stopwatch.StartNew();
        Spelling.NearSpellings("catholic", vocab);
        Assert.True(sw.Elapsed.TotalSeconds < 1.0, $"took {sw.Elapsed.TotalSeconds}s");
    }
}
