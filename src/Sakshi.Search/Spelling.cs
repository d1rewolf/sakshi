using System.Globalization;
using System.Text;

namespace Sakshi.Search;

/// <summary>Near-spelling expansion for query words (whisper misspells names).</summary>
public static class Spelling
{
    /// <summary>Lowercase and strip accents (matches FTS5 unicode61 terms).</summary>
    public static string Normalize(string word)
    {
        var decomposed = word.ToLowerInvariant().Normalize(NormalizationForm.FormKD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        return sb.ToString();
    }

    public static string SoundKey(string word)
    {
        var w = Normalize(word).Replace("ph", "f", StringComparison.Ordinal).Replace("ck", "k", StringComparison.Ordinal);
        var outp = new List<char>();
        for (var i = 0; i < w.Length; i++)
        {
            var ch = w[i];
            var nxt = i + 1 < w.Length ? w[i + 1] : '\0';
            if (ch == 'c')
                ch = nxt is 'e' or 'i' or 'y' ? 's' : 'k';
            else if (ch == 'q')
                ch = 'k';
            else if (ch == 'z')
                ch = 's';
            else if (ch == 'x')
            {
                outp.Add('k');
                ch = 's';
            }
            if (i > 0 && ch == 'h')
                continue;
            if (i > 0 && ch is 'a' or 'e' or 'i' or 'o' or 'u')
                continue;
            outp.Add(ch);
        }
        var sb = new StringBuilder();
        foreach (var ch in outp)
            if (sb.Length == 0 || sb[^1] != ch)
                sb.Append(ch);
        return sb.ToString();
    }

    /// <summary>Optimal string alignment distance; returns limit + 1 early-ish when over the limit.</summary>
    public static int Distance(string a, string b, int limit)
    {
        if (Math.Abs(a.Length - b.Length) > limit)
            return limit + 1;
        var prev2 = new int[b.Length + 1];
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
            prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            var min = cur[0];
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1), prev[j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                    cur[j] = Math.Min(cur[j], prev2[j - 2] + 1);
                if (cur[j] < min)
                    min = cur[j];
            }
            if (min > limit)
                return limit + 1;
            (prev2, prev, cur) = (prev, cur, prev2);
        }
        return prev[b.Length];
    }

    /// <summary>Vocabulary terms (other than the word itself) that look or sound like it, sorted ordinally.</summary>
    public static IReadOnlyList<string> NearSpellings(string word, IEnumerable<string> vocab)
    {
        var w = Normalize(word);
        if (w.Length < 4)
            return [];
        var limit = w.Length <= 6 ? 1 : 2;
        var key = SoundKey(w);
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var term in vocab)
        {
            if (term == w || found.Contains(term))
                continue;
            if (Distance(w, term, limit) <= limit
                || (key.Length >= 4 && Math.Abs(term.Length - w.Length) <= 2 && SoundKey(term) == key))
                found.Add(term);
        }
        var result = found.ToList();
        result.Sort(StringComparer.Ordinal);
        return result;
    }
}
