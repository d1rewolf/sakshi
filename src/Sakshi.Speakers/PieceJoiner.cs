namespace Sakshi.Speakers;

public static class PieceJoiner
{
    /// <summary>Average-linkage grouping: joins the best pair of groups while their mean pairwise cosine is at least the threshold.
    /// Returns a group index per input, numbered in order of each group's lowest member.</summary>
    public static int[] Join(IReadOnlyList<float[]> normalized, double threshold)
    {
        var n = normalized.Count;
        var groups = Enumerable.Range(0, n).Select(i => new List<int> { i }).ToList();
        while (groups.Count > 1)
        {
            double best = double.NegativeInfinity;
            int bi = -1, bj = -1;
            for (var i = 0; i < groups.Count; i++)
                for (var j = i + 1; j < groups.Count; j++)
                {
                    double sum = 0;
                    foreach (var a in groups[i])
                        foreach (var b in groups[j]) sum += Voiceprint.Cosine(normalized[a], normalized[b]);
                    var avg = sum / (groups[i].Count * groups[j].Count);
                    if (avg > best) { best = avg; bi = i; bj = j; }
                }
            if (best < threshold) break;
            groups[bi].AddRange(groups[bj]);
            groups.RemoveAt(bj);
        }
        var result = new int[n];
        var ordered = groups.OrderBy(g => g.Min()).ToList();
        for (var g = 0; g < ordered.Count; g++)
            foreach (var m in ordered[g]) result[m] = g;
        return result;
    }
}
