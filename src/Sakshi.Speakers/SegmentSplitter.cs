using Sakshi.Transcribe;

namespace Sakshi.Speakers;

public static class SegmentSplitter
{
    /// <summary>Split segments where the speaker changes. wordSpeakers indexes every segment's Words in order.</summary>
    public static IReadOnlyList<Segment> Split(IReadOnlyList<Segment> segments, IReadOnlyList<string> wordSpeakers)
    {
        var output = new List<Segment>();
        var wordless = new List<int>(); // indexes in output
        var offset = 0;
        foreach (var seg in segments)
        {
            var n = seg.Words.Count;
            if (n == 0) { wordless.Add(output.Count); output.Add(seg); continue; }

            var runs = new List<(int From, int To)>();
            var from = 0;
            for (var k = 1; k <= n; k++)
                if (k == n || wordSpeakers[offset + k] != wordSpeakers[offset + k - 1]) { runs.Add((from, k)); from = k; }

            if (runs.Count == 1)
            {
                output.Add(seg with { Speaker = wordSpeakers[offset] });
            }
            else
            {
                for (var r = 0; r < runs.Count; r++)
                {
                    var (a, b) = runs[r];
                    var ws = seg.Words.Skip(a).Take(b - a).ToList();
                    output.Add(new Segment(
                        r == 0 ? seg.Start : ws[0].Start,
                        r == runs.Count - 1 ? seg.End : ws[^1].End,
                        string.Concat(ws.Select(w => w.Text)))
                    { Words = ws, Speaker = wordSpeakers[offset + a] });
                }
            }
            offset += n;
        }

        foreach (var idx in wordless)
        {
            string? sp = null;
            for (var k = idx - 1; k >= 0 && sp is null; k--) sp = output[k].Speaker;
            for (var k = idx + 1; k < output.Count && sp is null; k++) sp = output[k].Speaker;
            output[idx] = output[idx] with { Speaker = sp };
        }
        return output;
    }
}
