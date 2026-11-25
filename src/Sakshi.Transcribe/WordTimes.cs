namespace Sakshi.Transcribe;

public static class WordTimes
{
    /// <summary>
    /// Words from Whisper's tokens (times in units of 10 ms). Control tokens such as "[_BEG_]" or "[_TT_150]" are dropped;
    /// a token with no leading space continues the word before it.
    /// </summary>
    public static IReadOnlyList<Word> FromTokens(IEnumerable<(string Text, long Start, long End)> tokens)
    {
        var words = new List<Word>();
        foreach (var (text, start, end) in tokens)
        {
            if (string.IsNullOrEmpty(text) || IsControl(text)) continue;
            var s = TimeSpan.FromMilliseconds(start * 10);
            var e = TimeSpan.FromMilliseconds(end * 10);
            if (words.Count > 0 && !text.StartsWith(' '))
            {
                var last = words[^1];
                words[^1] = last with { End = e, Text = last.Text + text };
            }
            else words.Add(new Word(s, e, text));
        }
        return words;
    }

    private static bool IsControl(string text) => text.Length >= 2 && text[0] == '[' && text[^1] == ']' && text.Contains('_');
}
