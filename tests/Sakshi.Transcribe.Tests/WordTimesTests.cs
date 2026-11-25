using Sakshi.Transcribe;

namespace Sakshi.Transcribe.Tests;

public sealed class WordTimesTests
{
    private static TimeSpan Ms(int ms) => TimeSpan.FromMilliseconds(ms);

    [Fact]
    public void ControlTokensAreDropped()
    {
        var words = WordTimes.FromTokens([("[_BEG_]", 0, 0), (" Hi", 0, 40), ("[_TT_150]", 150, 150)]);
        var w = Assert.Single(words);
        Assert.Equal(" Hi", w.Text);
        Assert.Equal(Ms(0), w.Start);
        Assert.Equal(Ms(400), w.End);
    }

    [Fact]
    public void TokensWithoutLeadingSpaceJoinTheWordBefore()
    {
        var words = WordTimes.FromTokens([(" Hel", 10, 20), ("lo", 20, 35), (",", 35, 36), (" you", 40, 60)]);
        Assert.Equal(2, words.Count);
        Assert.Equal(new Word(Ms(100), Ms(360), " Hello,"), words[0]);
        Assert.Equal(new Word(Ms(400), Ms(600), " you"), words[1]);
    }

    [Fact]
    public void FirstTokenWithoutSpaceStartsAWord()
    {
        var w = Assert.Single(WordTimes.FromTokens([("Hi", 0, 10)]));
        Assert.Equal("Hi", w.Text);
    }

    [Fact]
    public void EmptyInputGivesNoWords() => Assert.Empty(WordTimes.FromTokens([]));
}
