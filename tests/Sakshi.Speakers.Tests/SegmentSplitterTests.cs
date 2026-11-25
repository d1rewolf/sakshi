using Sakshi.Speakers;
using Sakshi.Transcribe;

namespace Sakshi.Speakers.Tests;

public class SegmentSplitterTests
{
    static Word W(double s, double e, string t) => new(TimeSpan.FromSeconds(s), TimeSpan.FromSeconds(e), t);
    static TimeSpan T(double s) => TimeSpan.FromSeconds(s);

    [Fact]
    public void Splits_at_speaker_change()
    {
        Word[] w = [W(0.1, 1, " one"), W(1, 2, " two"), W(2.2, 3, " three"), W(3, 3.9, " four")];
        var seg = new Segment(T(0), T(4), " one two three four") { Words = w };
        var r = SegmentSplitter.Split([seg], ["A", "A", "B", "B"]);
        Assert.Equal(2, r.Count);
        Assert.Equal(T(0), r[0].Start);
        Assert.Equal(T(2), r[0].End);
        Assert.Equal(" one two", r[0].Text);
        Assert.Equal("A", r[0].Speaker);
        Assert.Equal(T(2.2), r[1].Start);
        Assert.Equal(T(4), r[1].End);
        Assert.Equal(" three four", r[1].Text);
        Assert.Equal("B", r[1].Speaker);
        Assert.Equal(2, r[1].Words.Count);
    }

    [Fact]
    public void Unsplit_segment_keeps_text_and_times()
    {
        Word[] w = [W(0.1, 1, " one"), W(1, 2, " two")];
        var seg = new Segment(T(0), T(2.5), "  one two ") { Words = w };
        var r = SegmentSplitter.Split([seg], ["A", "A"]);
        Assert.Single(r);
        Assert.Equal("  one two ", r[0].Text);
        Assert.Equal(T(0), r[0].Start);
        Assert.Equal(T(2.5), r[0].End);
        Assert.Equal("A", r[0].Speaker);
    }

    [Fact]
    public void Segment_without_words_takes_previous_speaker()
    {
        var a = new Segment(T(0), T(1), " a") { Words = [W(0, 1, " a")] };
        var b = new Segment(T(1), T(2), " b");
        var r = SegmentSplitter.Split([a, b], ["A"]);
        Assert.Equal(["A", "A"], r.Select(s => s.Speaker));
    }

    [Fact]
    public void First_segment_without_words_takes_next_speaker()
    {
        var a = new Segment(T(0), T(1), " a");
        var b = new Segment(T(1), T(2), " b") { Words = [W(1, 2, " b")] };
        var r = SegmentSplitter.Split([a, b], ["B"]);
        Assert.Equal(["B", "B"], r.Select(s => s.Speaker));
    }

    [Fact]
    public void Three_runs_middle_uses_word_times()
    {
        Word[] w = [W(0.1, 1, " a"), W(1.2, 2, " b"), W(2, 2.8, " c"), W(3.1, 4, " d")];
        var seg = new Segment(T(0), T(4.5), " a b c d") { Words = w };
        var r = SegmentSplitter.Split([seg], ["A", "B", "B", "C"]);
        Assert.Equal(3, r.Count);
        Assert.Equal((T(0), T(1)), (r[0].Start, r[0].End));
        Assert.Equal((T(1.2), T(2.8)), (r[1].Start, r[1].End));
        Assert.Equal(" b c", r[1].Text);
        Assert.Equal((T(3.1), T(4.5)), (r[2].Start, r[2].End));
        Assert.Equal(["A", "B", "C"], r.Select(x => x.Speaker));
    }
}
