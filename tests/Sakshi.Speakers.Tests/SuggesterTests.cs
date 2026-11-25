using Sakshi.Speakers;
using static Sakshi.Speakers.Tests.PieceJoinerTests;

namespace Sakshi.Speakers.Tests;

public class SuggesterTests
{
    static Candidate P(string id, params double[] scoresAgainstZeroDegrees)
        => new(id, id, scoresAgainstZeroDegrees.Select(s => At(Deg(s))).ToList());

    // Speaker at 0 degrees; a person's print at Deg(s) scores s against it.
    static readonly float[] Spk = At(0);

    [Fact]
    public void Best_person_above_threshold_with_margin_is_suggested()
    {
        var r = Suggester.Suggest([Spk], [P("Ada", 0.80), P("Ben", 0.30)]);
        Assert.Equal("Ada", r[0]!.PersonId);
        Assert.Equal(0.80, r[0]!.Score, 4);
    }

    [Fact]
    public void Score_is_the_best_of_a_persons_prints()
    {
        var r = Suggester.Suggest([Spk], [P("Ada", 0.40, 0.75)]);
        Assert.Equal(0.75, r[0]!.Score, 4);
    }

    [Fact]
    public void Too_close_to_the_second_person_gives_nothing()
        => Assert.Null(Suggester.Suggest([Spk], [P("Ada", 0.70), P("Ben", 0.67)])[0]);

    [Fact]
    public void Below_threshold_gives_nothing()
        => Assert.Null(Suggester.Suggest([Spk], [P("Ada", 0.55)])[0]);

    [Fact]
    public void Person_without_prints_is_skipped()
    {
        var r = Suggester.Suggest([Spk], [new Candidate("Cy", "Cy", []), P("Ada", 0.80)]);
        Assert.Equal("Ada", r[0]!.PersonId);
    }

    [Fact]
    public void No_people_gives_nothing()
        => Assert.Null(Suggester.Suggest([Spk], [])[0]);

    [Fact]
    public void Two_speakers_one_person_higher_score_keeps_it_and_loser_falls_back()
    {
        // Speakers at 0 and 50 degrees; Ada at 20 (scores 0.94 and 0.87), Ben at 90 (0 and 0.77).
        // Both speakers pick Ada first; speaker 0 scores higher so keeps her; speaker 1 falls back to Ben.
        var ada = new Candidate("Ada", "Ada", [At(20)]);
        var ben = new Candidate("Ben", "Ben", [At(90)]);
        var r = Suggester.Suggest([At(0), At(50)], [ada, ben]);
        Assert.Equal("Ada", r[0]!.PersonId);
        Assert.Equal("Ben", r[1]!.PersonId);
        Assert.Equal(Math.Cos(40 * Math.PI / 180), r[1]!.Score, 4);
    }

    [Fact]
    public void Loser_whose_fallback_is_below_threshold_gets_nothing()
    {
        // As above but Ben at 120: speaker 1 scores cos(70) = 0.34 against him.
        var ada = new Candidate("Ada", "Ada", [At(20)]);
        var ben = new Candidate("Ben", "Ben", [At(120)]);
        var r = Suggester.Suggest([At(0), At(50)], [ada, ben]);
        Assert.Equal("Ada", r[0]!.PersonId);
        Assert.Null(r[1]);
    }

    [Fact]
    public void Loser_with_no_other_person_left_gets_nothing()
    {
        var s0 = At(0);
        var ada = new Candidate("Ada", "Ada", [At(Deg(0.9))]);
        var r = Suggester.Suggest([s0, At(Deg(0.9) + Deg(0.7))], [ada]);
        // s0 scores 0.9; s1 scores 0.7 against Ada; Ada goes to s0, s1 has nobody left.
        Assert.Equal("Ada", r[0]!.PersonId);
        Assert.Null(r[1]);
    }

    [Fact]
    public void Equal_scores_go_to_the_lower_speaker_index()
    {
        var ada = new Candidate("Ada", "Ada", [At(0)]);
        var same = At(Deg(0.8));
        var r = Suggester.Suggest([same, same], [ada]);
        Assert.Equal("Ada", r[0]!.PersonId);
        Assert.Null(r[1]);
    }

    [Fact]
    public void Speaker_that_never_qualified_stays_null_when_a_conflict_frees_a_person()
    {
        // Ben at 0, X at 47.9 (Ada 0.70, Ben 0.67: fails the margin), Ada at 93.5.
        // Y and Z both best-match Ben; Y keeps him, Z falls back and fails. X must not be revisited.
        var ben = new Candidate("Ben", "Ben", [At(0)]);
        var ada = new Candidate("Ada", "Ada", [At(Deg(0.67) + Deg(0.70))]);
        var r = Suggester.Suggest([At(Deg(0.67)), At(5), At(-10)], [ada, ben]);
        Assert.Null(r[0]);
        Assert.Equal("Ben", r[1]!.PersonId);
        Assert.Null(r[2]);
    }
}
