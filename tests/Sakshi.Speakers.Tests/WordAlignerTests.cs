using Sakshi.Speakers;
using Sakshi.Transcribe;

namespace Sakshi.Speakers.Tests;

public class WordAlignerTests
{
    static Word W(double s, double e, string t) => new(TimeSpan.FromSeconds(s), TimeSpan.FromSeconds(e), t);

    [Fact]
    public void Midpoint_inside_turn_gets_that_speaker()
    {
        var r = WordAligner.Assign([W(1, 2, " hi")], [new SpeakerTurn(0, 5, "S1"), new SpeakerTurn(6, 9, "S2")]);
        Assert.Equal(["S1"], r);
    }

    [Fact]
    public void Latest_starting_turn_wins_on_overlap()
    {
        var r = WordAligner.Assign([W(3, 4, " hi")], [new SpeakerTurn(0, 5, "S1"), new SpeakerTurn(2, 6, "S2")]);
        Assert.Equal(["S2"], r);
    }

    [Fact]
    public void Word_half_second_from_turn_gets_nearest()
    {
        var r = WordAligner.Assign([W(5.4, 5.6, " hi")], [new SpeakerTurn(0, 5, "S1"), new SpeakerTurn(20, 30, "S2")]);
        Assert.Equal(["S1"], r);
    }

    [Fact]
    public void Word_two_seconds_from_any_turn_takes_previous_word()
    {
        var r = WordAligner.Assign([W(1, 2, " a"), W(7, 7.2, " b")], [new SpeakerTurn(0, 5, "S1"), new SpeakerTurn(20, 30, "S2")]);
        Assert.Equal(["S1", "S1"], r);
    }

    [Fact]
    public void First_word_with_no_turn_near_takes_next_assigned()
    {
        var r = WordAligner.Assign([W(0, 0.2, " a"), W(10, 11, " b")], [new SpeakerTurn(10, 12, "S2")]);
        Assert.Equal(["S2", "S2"], r);
    }

    [Fact]
    public void No_turns_gives_all_null()
    {
        var r = WordAligner.Assign([W(0, 1, " a"), W(1, 2, " b")], []);
        Assert.Equal([null, null], r);
    }

    [Fact]
    public void Snap_moves_change_to_sentence_end()
    {
        Word[] w = [W(0, 1, " I"), W(1, 2, " think"), W(2, 3, " so."), W(3, 4, " Okay")];
        Assert.Equal(["A", "A", "A", "B"], WordAligner.Snap(w, ["A", "A", "B", "B"]));
    }

    [Fact]
    public void Snap_counts_long_pause_as_sentence_end()
    {
        Word[] w = [W(0, 1, " I"), W(1, 2, " think"), W(2, 3, " so"), W(3.8, 4, " okay")];
        Assert.Equal(["A", "A", "A", "B"], WordAligner.Snap(w, ["A", "A", "B", "B"]));
    }

    [Fact]
    public void Snap_leaves_change_when_no_sentence_end_within_four_words()
    {
        var w = Enumerable.Range(0, 12).Select(i => W(i, i + 1, " w")).ToArray();
        string[] sp = [.. Enumerable.Repeat("A", 6), .. Enumerable.Repeat("B", 6)];
        Assert.Equal(sp, WordAligner.Snap(w, sp));
    }

    [Fact]
    public void Snap_single_speaker_unchanged()
    {
        Word[] w = [W(0, 1, " a."), W(1, 2, " b")];
        Assert.Equal(["A", "A"], WordAligner.Snap(w, ["A", "A"]));
    }

    [Fact]
    public void Assign_falls_back_to_nearest_turn_when_none_is_near()
    {
        var r = WordAligner.Assign([W(10, 11, " a"), W(40, 41, " b")], [new SpeakerTurn(0, 5, "S1"), new SpeakerTurn(60, 70, "S2")]);
        Assert.Equal(["S1", "S2"], r);
    }

    [Fact]
    public void Assign_unsorted_turns_and_ties_go_to_earlier_start()
    {
        // Word at 5.0 is 1 s from both turns; the later-listed turn starts earlier and wins.
        var r = WordAligner.Assign([W(4.9, 5.1, " a")], [new SpeakerTurn(6, 8, "S2"), new SpeakerTurn(0, 4, "S1")]);
        Assert.Equal(["S1"], r);
    }

    static string[] Snap(string[] speakers, params string[] texts) =>
        WordAligner.Snap(texts.Select((t, i) => W(i, i + 0.5, t)).ToArray(), speakers);

    [Fact]
    public void Snap_change_at_index_one()
        => Assert.Equal(["A", "A", "A", "B"], Snap(["A", "B", "B", "B"], " x", " y", " z.", " w"));

    [Fact]
    public void Snap_change_at_last_index()
        => Assert.Equal(["A", "A", "B", "B"], Snap(["A", "A", "A", "B"], " a", " b.", " c", " d"));

    [Fact]
    public void Snap_change_already_at_sentence_start_stays()
        => Assert.Equal(["A", "B", "B"], Snap(["A", "B", "B"], " a.", " b", " c"));

    [Fact]
    public void Snap_tie_goes_to_earlier_sentence_start()
        => Assert.Equal(["A", "A", "B", "B", "B"], Snap(["A", "A", "A", "B", "B"], " a", " b.", " c", " d.", " e"));

    [Fact]
    public void Snap_two_changes_close_together()
        => Assert.Equal(["A", "A", "A", "B", "B", "B", "A"],
            Snap(["A", "A", "B", "B", "B", "A", "A"], " a", " b", " c.", " d", " e", " f.", " g"));

    [Fact]
    public void Snap_one_word_run_inside_sentence_is_left_alone()
        // A one-word run inside a sentence is treated as noise: no sentence start nearby, so nothing moves.
        => Assert.Equal(["A", "A", "B", "A", "A"], Snap(["A", "A", "B", "A", "A"], " a", " b", " c", " d", " e."));
}
