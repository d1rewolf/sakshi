namespace Sakshi.Speakers;

public static class Voiceprint
{
    /// <summary>Subtracts the background mean and scales to length 1. A zero vector stays zero.</summary>
    public static float[] Normalize(float[] raw, float[] mean)
    {
        if (raw.Length != mean.Length)
            throw new ArgumentException($"Voiceprint length {raw.Length} does not match background length {mean.Length}.");
        var r = new float[raw.Length];
        double sq = 0;
        for (var i = 0; i < r.Length; i++)
        {
            r[i] = raw[i] - mean[i];
            sq += (double)r[i] * r[i];
        }
        var len = Math.Sqrt(sq);
        if (len == 0) return r;
        for (var i = 0; i < r.Length; i++) r[i] = (float)(r[i] / len);
        return r;
    }

    /// <summary>Dot product of two normalised vectors; 0 if either is the zero vector.</summary>
    public static double Cosine(float[] a, float[] b)
    {
        if (a.Length != b.Length) throw new ArgumentException("Voiceprints differ in length.");
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += (double)a[i] * b[i];
            na += (double)a[i] * a[i];
            nb += (double)b[i] * b[i];
        }
        return na == 0 || nb == 0 ? 0 : dot;
    }
}
