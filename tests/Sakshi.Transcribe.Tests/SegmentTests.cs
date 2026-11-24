using Sakshi.Transcribe;

namespace Sakshi.Transcribe.Tests;

public sealed class SegmentTests
{
    [Theory]
    [InlineData("[BLANK_AUDIO]", true)]
    [InlineData(" [BLANK_AUDIO] ", true)]
    [InlineData("(door opens)", true)]
    [InlineData("(bell dings) (door closes)", true)]
    [InlineData("[Music]", true)]
    [InlineData("*sighs*", true)]
    [InlineData("♪♪", true)]
    [InlineData("[inaudible].", true)]
    [InlineData("We left it [BLANK_AUDIO] there", false)]
    [InlineData("(laughs) That's right.", false)]
    [InlineData("Thank you.", false)]
    [InlineData("", false)]
    [InlineData(" . ", false)]
    [InlineData("(unclosed", false)]
    public void Only_segments_without_words_are_blank(string text, bool blank) => Assert.Equal(blank, Segment.IsBlank(text));

    [Fact]
    public void Long_runs_of_notes_are_judged_at_once()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.False(Segment.IsBlank(new string('♪', 5000) + " la"));
        Assert.False(Segment.IsBlank(string.Concat(Enumerable.Repeat("(a)  ", 5000)) + "x"));
        Assert.True(Segment.IsBlank(string.Concat(Enumerable.Repeat("(laughs) ", 5000))));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1));
    }
}
