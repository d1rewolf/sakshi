using Sakshi.Speakers;

namespace Sakshi.Speakers.Tests;

public class VoiceprintTests
{
    [Fact]
    public void Seed_has_256_values_and_count_40()
    {
        var seed = Background.Seed();
        Assert.Equal(256, seed.Mean.Length);
        Assert.Equal(40, seed.Count);
    }

    [Fact]
    public void Combine_weights_seed_by_its_count()
    {
        var seed = new Background([1f, 1f], 40);
        var r = Background.Combine(seed, [10.0, 0.0], 10);
        Assert.Equal(1.0, r[0], 5);
        Assert.Equal(0.8, r[1], 5);
    }

    [Fact]
    public void Combine_without_samples_returns_a_copy_of_the_seed_mean()
    {
        var seed = new Background([1f, 2f], 40);
        var r = Background.Combine(seed, null, 0);
        Assert.Equal(seed.Mean, r);
        Assert.NotSame(seed.Mean, r);
    }

    [Fact]
    public void Combine_with_mismatched_lengths_throws()
        => Assert.Throws<ArgumentException>(() => Background.Combine(new Background([1f, 1f], 40), [1.0], 1));

    [Fact]
    public void Normalize_subtracts_the_mean_and_scales_to_length_one()
    {
        var r = Voiceprint.Normalize([3f, 4f], [0f, 0f]);
        Assert.Equal(0.6, r[0], 5);
        Assert.Equal(0.8, r[1], 5);
    }

    [Fact]
    public void Normalize_subtracts_before_scaling()
    {
        var r = Voiceprint.Normalize([4f, 4f], [1f, 0f]);
        Assert.Equal(0.6, r[0], 5);
        Assert.Equal(0.8, r[1], 5);
    }

    [Fact]
    public void Normalize_keeps_a_zero_vector_zero()
        => Assert.Equal([0f, 0f], Voiceprint.Normalize([1f, 2f], [1f, 2f]));

    [Fact]
    public void Normalize_with_different_lengths_throws()
        => Assert.Throws<ArgumentException>(() => Voiceprint.Normalize([1f], [1f, 2f]));

    [Fact]
    public void Cosine_is_the_dot_product_and_zero_for_a_zero_vector()
    {
        Assert.Equal(0.6, Voiceprint.Cosine([0.6f, 0.8f], [1f, 0f]), 5);
        Assert.Equal(0.0, Voiceprint.Cosine([0f, 0f], [1f, 0f]));
    }
}
