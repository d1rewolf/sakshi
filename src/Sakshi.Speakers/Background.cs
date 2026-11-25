using System.Reflection;
using System.Text.Json;

namespace Sakshi.Speakers;

public sealed record Background(float[] Mean, int Count)
{
    public static Background Seed()
    {
        using var s = typeof(Background).Assembly.GetManifestResourceStream("Sakshi.Speakers.background.json")
            ?? throw new InvalidOperationException("Embedded background.json is missing.");
        using var doc = JsonDocument.Parse(s);
        var root = doc.RootElement;
        var mean = root.GetProperty("mean").EnumerateArray().Select(e => e.GetSingle()).ToArray();
        return new Background(mean, root.GetProperty("count").GetInt32());
    }

    /// <summary>(seed.Mean x seed.Count + sum) / (seed.Count + count); the seed mean (a copy) when there is nothing to add.</summary>
    public static float[] Combine(Background seed, double[]? sum, int count)
    {
        if (sum is null || count == 0) return (float[])seed.Mean.Clone();
        if (sum.Length != seed.Mean.Length)
            throw new ArgumentException($"Sum length {sum.Length} does not match background length {seed.Mean.Length}.");
        var r = new float[sum.Length];
        double n = seed.Count + count;
        for (var i = 0; i < r.Length; i++) r[i] = (float)(((double)seed.Mean[i] * seed.Count + sum[i]) / n);
        return r;
    }
}
