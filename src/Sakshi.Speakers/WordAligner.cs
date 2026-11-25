using Sakshi.Transcribe;

namespace Sakshi.Speakers;

public static class WordAligner
{
    const double NearSeconds = 1.0;

    /// <summary>Speaker for each word. All null only when there are no turns.</summary>
    public static string[] Assign(IReadOnlyList<Word> words, IReadOnlyList<SpeakerTurn> turns)
    {
        var result = new string[words.Count];
        if (turns.Count == 0 || words.Count == 0) return result;

        var assigned = new bool[words.Count];
        for (var i = 0; i < words.Count; i++)
        {
            var mid = (words[i].Start.TotalSeconds + words[i].End.TotalSeconds) / 2;
            SpeakerTurn? pick = null;
            foreach (var t in turns)
                if (t.Start <= mid && mid <= t.End && (pick is null || t.Start >= pick.Start)) pick = t;
            if (pick is null)
            {
                var best = double.MaxValue;
                foreach (var t in turns)
                {
                    var d = mid < t.Start ? t.Start - mid : mid - t.End;
                    if (d < best) { best = d; pick = t; }
                }
                if (best > NearSeconds) pick = null;
            }
            if (pick is not null) { result[i] = pick.Speaker; assigned[i] = true; }
        }

        for (var i = 1; i < result.Length; i++)
            if (!assigned[i]) result[i] = result[i - 1];
        var first = Array.FindIndex(assigned, a => a);
        if (first < 0)
        {
            // No word is near any turn: fall back to the nearest turn for all.
            var fallback = turns[0].Speaker;
            Array.Fill(result, fallback);
            return result;
        }
        for (var i = 0; i < first; i++) result[i] = result[first];
        return result;
    }

    /// <summary>Move each change of speaker to the nearest sentence start within SnapWords.</summary>
    public static string[] Snap(IReadOnlyList<Word> words, IReadOnlyList<string> speakers)
    {
        var s = speakers.ToArray();
        var i = 1;
        while (i < s.Length)
        {
            if (s[i] == s[i - 1] || IsStart(words, i)) { i++; continue; }

            var j = -1;
            for (var d = 1; d <= SpeakerSettings.SnapWords && j < 0; d++)
            {
                if (i - d >= 1 && IsStart(words, i - d)) j = i - d;
                else if (i + d < s.Length && IsStart(words, i + d)) j = i + d;
            }
            if (j < 0) { i++; continue; }

            if (j < i) for (var k = j; k < i; k++) s[k] = s[i];
            else for (var k = i; k < j; k++) s[k] = s[i - 1];
            i = Math.Max(i, j) + 1;
        }
        return s;
    }

    static bool IsStart(IReadOnlyList<Word> words, int i)
    {
        var prev = words[i - 1].Text.TrimEnd();
        if (prev.EndsWith('.') || prev.EndsWith('?') || prev.EndsWith('!')) return true;
        return words[i].Start.TotalSeconds - words[i - 1].End.TotalSeconds > SpeakerSettings.PauseSeconds;
    }
}
