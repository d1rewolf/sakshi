using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Sakshi.Search;

public sealed record Hit(long? StartMs, string Text);

public sealed record Result(
    string Tier, string Day, string Name, string Dir,
    IReadOnlyList<Hit> Hits, int TotalHits, bool Collapsed,
    IReadOnlyDictionary<string, IReadOnlyList<string>> NearTerms);

/// <summary>Search the index: exact tier, then near-spelling tier.</summary>
public static partial class Finder
{
    /// <summary>A near spelling in more than this share of all meetings is a "common look-alike".</summary>
    public const double CommonShare = 0.25;

    // letters/digits (and combining marks) are token characters, like FTS5 unicode61; same set as Python (?:[^\W_]|[\u0300-\u036f])+
    [GeneratedRegex(@"[\p{L}\p{N}̀-ͯ]+")]
    internal static partial Regex Token();

    public static IReadOnlyList<string> QueryWords(IEnumerable<string> words)
    {
        var output = new List<string>();
        foreach (var w in words)
            foreach (Match m in Token().Matches(w))
            {
                var n = Spelling.Normalize(m.Value);
                if (n.Length > 0 && !output.Contains(n))
                    output.Add(n);
            }
        return output;
    }

    private static string Literal(string term) => "\"" + term.Replace("\"", "\"\"") + "\"";

    private static Dictionary<long, HashSet<long>> LinesMatching(SqliteConnection conn, string term)
    {
        var found = new Dictionary<long, HashSet<long>>();
        using var cmd = SearchIndex.Command(conn,
            "SELECT lines.id, lines.meeting_id FROM lines_fts "
            + "JOIN lines ON lines.id = lines_fts.rowid WHERE lines_fts MATCH $p0", Literal(term));
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var mid = r.GetInt64(1);
            if (!found.TryGetValue(mid, out var set))
                found[mid] = set = [];
            set.Add(r.GetInt64(0));
        }
        return found;
    }

    public static (IReadOnlyList<Result> Results, IReadOnlyDictionary<string, IReadOnlyList<string>> NearUsed) Search(
        SearchIndex index, IReadOnlyList<string> inputWords)
    {
        var conn = index.Connection;
        var words = QueryWords(inputWords);
        if (words.Count == 0)
            return ([], new Dictionary<string, IReadOnlyList<string>>());
        var vocab = index.Vocabulary();
        var total = Math.Max(1, SearchIndex.Scalar(conn, "SELECT count(*) FROM meetings"));
        var exactIds = new List<HashSet<long>>();
        var anyIds = new List<HashSet<long>>();
        var spellingMeetings = new Dictionary<string, Dictionary<string, HashSet<long>>>(); // word -> spelling -> meetings
        var linesByMeeting = new Dictionary<long, HashSet<long>>();
        var exactLines = new HashSet<long>();

        HashSet<long> Collect(string term, bool isExact)
        {
            var per = LinesMatching(conn, term);
            foreach (var (mid, lids) in per)
            {
                if (!linesByMeeting.TryGetValue(mid, out var set))
                    linesByMeeting[mid] = set = [];
                set.UnionWith(lids);
                if (isExact)
                    exactLines.UnionWith(lids);
            }
            return [.. per.Keys];
        }

        foreach (var w in words)
        {
            var e = Collect(w, true);
            exactIds.Add(e);
            var a = new HashSet<long>(e);
            var used = new Dictionary<string, HashSet<long>>();
            foreach (var sp in Spelling.NearSpellings(w, vocab))
            {
                var got = Collect(sp, false);
                if (got.Count > 0)
                {
                    used[sp] = got;
                    a.UnionWith(got);
                }
            }
            if (used.Count > 0)
                spellingMeetings[w] = used;
            anyIds.Add(a);
        }

        var exact = Intersect(exactIds);
        var near = Intersect(anyIds);
        near.ExceptWith(exact);
        var exactByWord = new Dictionary<string, HashSet<long>>();
        for (var i = 0; i < words.Count; i++)
            exactByWord[words[i]] = exactIds[i];

        bool Common(HashSet<long> spMeetings) => (double)spMeetings.Count / total > CommonShare;

        Dictionary<string, List<string>> NearTermsOf(long mid)
        {
            var output = new Dictionary<string, List<string>>();
            foreach (var (w, used) in spellingMeetings)
            {
                if (exactByWord[w].Contains(mid))
                    continue;
                var sps = used.Where(kv => kv.Value.Contains(mid)).Select(kv => kv.Key).ToList();
                if (sps.Count > 0)
                    output[w] = sps;
            }
            return output;
        }

        var results = new List<Result>();
        foreach (var (tier, ids) in new[] { ("exact", exact), ("near", near) })
        {
            var rows = new List<(long Id, string Dir, string Day, string Name)>();
            foreach (var mid in ids.Order())
            {
                using var cmd = SearchIndex.Command(conn, "SELECT id, dir, day, name FROM meetings WHERE id=$p0", mid);
                using var r = cmd.ExecuteReader();
                if (r.Read())
                    rows.Add((r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3)));
            }
            // day newest first, then name; OrderBy is stable like Python's two sorts
            var ordered = rows.OrderByDescending(r => r.Day, StringComparer.Ordinal)
                .ThenBy(r => r.Name, StringComparer.Ordinal);
            foreach (var (mid, d, day, name) in ordered)
            {
                var lids = linesByMeeting.TryGetValue(mid, out var set) ? set.Order().ToList() : [];
                var texts = new List<(long? StartMs, string Text, long Id)>();
                for (var i = 0; i < lids.Count; i += 500)
                {
                    var chunk = lids.Skip(i).Take(500).ToList();
                    var marks = string.Join(",", chunk.Select((_, k) => "$p" + k));
                    using var cmd = SearchIndex.Command(conn,
                        $"SELECT start_ms, text, id FROM lines WHERE id IN ({marks})",
                        chunk.Cast<object?>().ToArray());
                    using var r = cmd.ExecuteReader();
                    while (r.Read())
                        texts.Add((r.IsDBNull(0) ? null : r.GetInt64(0), r.GetString(1), r.GetInt64(2)));
                }
                // name line first, then lines with a query word itself, then near-only lines
                var hits = texts
                    .OrderBy(t => t.StartMs.HasValue)
                    .ThenBy(t => !exactLines.Contains(t.Id))
                    .ThenBy(t => t.StartMs ?? 0)
                    .ThenBy(t => t.Id)
                    .Select(t => new Hit(t.StartMs, t.Text))
                    .ToList();
                var nt = new Dictionary<string, List<string>>();
                var collapsed = false;
                if (tier == "near")
                {
                    nt = NearTermsOf(mid);
                    foreach (var (w, sps) in nt)
                        if (sps.All(sp => Common(spellingMeetings[w][sp])))
                            collapsed = true;
                    if (collapsed)
                    {
                        var kept = new Dictionary<string, List<string>>();
                        foreach (var (w, sps) in nt)
                            if (sps.All(sp => Common(spellingMeetings[w][sp])))
                                kept[w] = sps.Where(sp => Common(spellingMeetings[w][sp])).ToList();
                        nt = kept;
                    }
                }
                results.Add(new Result(tier, day, name, d, hits, hits.Count, collapsed,
                    nt.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value)));
            }
        }

        // heading: spellings that put at least one meeting into the near tier
        var nearUsed = new Dictionary<string, IReadOnlyList<string>>();
        foreach (var w in words)
        {
            if (!spellingMeetings.TryGetValue(w, out var used))
                continue;
            var sps = used.Where(kv => kv.Value.Any(m => near.Contains(m) && !exactByWord[w].Contains(m)))
                .Select(kv => kv.Key).ToList();
            if (sps.Count > 0)
                nearUsed[w] = sps;
        }
        return (results, nearUsed);
    }

    private static HashSet<long> Intersect(List<HashSet<long>> sets)
    {
        var result = new HashSet<long>(sets[0]);
        foreach (var s in sets.Skip(1))
            result.IntersectWith(s);
        return result;
    }
}
