using Sakshi.Speakers;

namespace Sakshi.Speakers.Tests;

public class PieceJoinerTests
{
    // Unit vector at an angle (degrees); cosine between two = cos(difference).
    internal static float[] At(double deg) => [(float)Math.Cos(deg * Math.PI / 180), (float)Math.Sin(deg * Math.PI / 180)];
    internal static double Deg(double cos) => Math.Acos(cos) * 180 / Math.PI;

    [Fact]
    public void Joins_close_pieces_and_leaves_a_far_one()
    {
        var r = PieceJoiner.Join([At(0), At(Deg(0.95)), At(90)], 0.90);
        Assert.Equal([0, 0, 1], r);
    }

    [Fact]
    public void Higher_threshold_keeps_everything_apart()
        => Assert.Equal([0, 1, 2], PieceJoiner.Join([At(0), At(Deg(0.95)), At(90)], 0.99));

    [Fact]
    public void Average_linkage_not_single_linkage()
    {
        // A-B 0.96, B-C 0.95, A-C about 0.82: C to {A,B} averages about 0.89, under 0.90,
        // though single linkage (B-C 0.95) would have joined it.
        var b = Deg(0.96);
        Assert.Equal([0, 0, 1], PieceJoiner.Join([At(0), At(b), At(b + Deg(0.95))], 0.90));
    }

    [Fact]
    public void Groups_are_numbered_by_their_lowest_member()
        => Assert.Equal([0, 1, 0], PieceJoiner.Join([At(0), At(90), At(1)], 0.90));

    [Fact]
    public void Empty_input_gives_empty_result()
        => Assert.Empty(PieceJoiner.Join([], 0.90));
}
