using System.Text;

namespace Sakshi.Search;

/// <summary>Formats search results for the terminal.</summary>
public static class ResultFormatter
{
    private const int Snippet = 120;
    private const int Shown = 3;

    private static string Bold(string s, bool color) => color ? $"\x1b[1m{s}\x1b[0m" : s;

    private static string MakeSnippet(string text, HashSet<string> terms, bool color)
    {
        var spans = Finder.Token().Matches(text)
            .Where(m => terms.Contains(Spelling.Normalize(m.Value)))
            .Select(m => (S: m.Index, E: m.Index + m.Length))
            .ToList();
        var n = text.Length;
        int lo = 0, hi = n;
        if (n > Snippet)
        {
            var first = spans.Count > 0 ? spans[0].S : 0;
            lo = Math.Max(0, Math.Min(first - 30, n - Snippet));
            hi = Math.Min(n, lo + Snippet);
            if (lo > 0)
                lo++;
            if (hi < n)
                hi--;
        }
        var sb = new StringBuilder();
        if (lo > 0)
            sb.Append('…');
        var pos = lo;
        foreach (var (s, e) in spans)
        {
            if (s < lo || e > hi)
                continue;
            sb.Append(text, pos, s - pos).Append(Bold(text[s..e], color));
            pos = e;
        }
        sb.Append(text, pos, hi - pos);
        if (hi < n)
            sb.Append('…');
        return sb.ToString();
    }

    private static string Clock(long ms)
    {
        var s = ms / 1000;
        return $"{s / 3600:00}:{s / 60 % 60:00}:{s % 60:00}";
    }

    public static string Format(
        IReadOnlyList<Result> results, IReadOnlyDictionary<string, IReadOnlyList<string>> nearUsed,
        IReadOnlyList<string> words, bool allHits, bool color)
    {
        if (results.Count == 0)
            return $"No meetings mention “{string.Join(" ", words)}”.\n";
        var terms = new HashSet<string>(Finder.QueryWords(words));
        foreach (var sps in nearUsed.Values)
            terms.UnionWith(sps);
        var output = new List<string>();

        string Spellings(IEnumerable<Result> group)
        {
            var got = new List<string>();
            foreach (var (w, sps) in nearUsed)
            {
                var have = group.SelectMany(r => r.NearTerms.TryGetValue(w, out var t) ? t : []).ToHashSet();
                var keep = sps.Where(have.Contains).ToList();
                if (keep.Count > 0)
                    got.Add($"{w} → {string.Join(", ", keep)}");
            }
            return string.Join("; ", got);
        }

        foreach (var tier in new[] { "exact", "near" })
        {
            var all = results.Where(r => r.Tier == tier).ToList();
            var hidden = all.Where(r => r.Collapsed && !allHits).ToList();
            var group = all.Where(r => !hidden.Contains(r)).ToList();
            if (group.Count > 0)
                output.Add(tier == "exact"
                    ? "Exact matches"
                    : $"Near spellings (searched also for: {Spellings(group)})");
            foreach (var r in group)
            {
                output.Add("");
                output.Add($"{r.Day}  {r.Name}");
                output.Add($"  {Path.Combine(r.Dir, "meeting.md")}");
                var hits = allHits ? r.Hits : r.Hits.Take(Shown).ToList();
                foreach (var h in hits)
                {
                    var snip = MakeSnippet(h.Text, terms, color);
                    output.Add(h.StartMs is null ? $"  (name) {snip}" : $"  [{Clock(h.StartMs.Value)}] {snip}");
                }
                var more = r.TotalHits - hits.Count;
                if (more > 0)
                    output.Add($"  … and {more} more — add --all to see every match");
            }
            if (hidden.Count > 0)
            {
                if (group.Count > 0)
                    output.Add("");
                output.Add($"… and {hidden.Count} more meetings only match common look-alikes "
                    + $"({Spellings(hidden)}) — add --all to see them");
            }
            if (group.Count > 0 || hidden.Count > 0)
                output.Add("");
        }
        return string.Join("\n", output) + "\n";
    }
}
