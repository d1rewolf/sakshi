using Sakshi.Transcribe;

namespace Sakshi.Speakers;

public static class WordAligner
{
    const double NearSeconds = 1.0;

    /// <summary>
    /// Speaker for each word. Turns need not be sorted. A word whose midpoint is inside a turn gets it
    /// (the latest-starting wins on overlap); otherwise the nearest turn within a second, with distance
    /// ties going to the turn that starts earlier. Other words take the previous word's speaker (leading
    /// ones the next assigned word's). If no word is near any turn, each word gets its nearest turn.
    /// All null only when there are no turns.
    /// </summary>
    public static string[] Assign(IReadOnlyList<Word> words, IReadOnlyList<SpeakerTurn> turns)
    {
        var result = new string[words.Count];
        if (turns.Count == 0 || words.Count == 0) return result;

        var assigned = new bool[words.Count];
        var nearest = new string[words.Count];
        for (var i = 0; i < words.Count; i++)
        {
            var mid = (words[i].Start.TotalSeconds + words[i].End.TotalSeconds) / 2;
            SpeakerTurn? pick = null;
            foreach (var t in turns)
                if (t.Start <= mid && mid <= t.End && (pick is null || t.Start > pick.Start)) pick = t;
            if (pick is not null) { result[i] = nearest[i] = pick.Speaker; assigned[i] = true; continue; }

            var best = double.MaxValue;
            foreach (var t in turns)
            {
                var d = mid < t.Start ? t.Start - mid : mid - t.End;
                if (d < best || (d == best && t.Start < pick!.Start)) { best = d; pick = t; }
            }
            nearest[i] = pick!.Speaker;
            if (best <= NearSeconds) { result[i] = pick.Speaker; assigned[i] = true; }
        }

        var first = Array.FindIndex(assigned, a => a);
        if (first < 0) return nearest;
        for (var i = first + 1; i < result.Length; i++)
            if (!assigned[i]) result[i] = result[i - 1];
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
