namespace Sakshi.Transcribe;

public sealed record Segment(TimeSpan Start, TimeSpan End, string Text)
{
    /// <summary>
    /// Whisper's marker for a stretch with no sound ("[BLANK_AUDIO]"). It says nothing, so transcripts leave it out.
    /// </summary>
    public static bool IsBlank(string text) => text.Trim() == "[BLANK_AUDIO]";
}
