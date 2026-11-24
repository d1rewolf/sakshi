namespace Sakshi.Transcribe;

public sealed record Segment(TimeSpan Start, TimeSpan End, string Text)
{
    /// <summary>
    /// A segment with no words in it: Whisper's marker for silence ("[BLANK_AUDIO]") or only notes about sounds, such as
    /// "(door closes)", "[Music]", "*sighs*" or "♪", which it mostly invents from room noise. Transcripts leave these
    /// out; a note inside spoken words stays. One pass over the text (no backtracking, whatever Whisper writes).
    /// </summary>
    public static bool IsBlank(string text)
    {
        var any = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c) || c is '.' or ',' or '!' or '?' || c is '♪' or '♫') { any |= c is '♪' or '♫'; continue; }
            var close = c switch { '[' => ']', '(' => ')', '*' => '*', _ => '\0' };
            if (close == '\0') return false;                 // a word outside any note
            var end = text.IndexOf(close, i + 1);
            if (end < 0) return false;                       // an unclosed note is not a note
            any = true;
            i = end;
        }
        return any;
    }
}
