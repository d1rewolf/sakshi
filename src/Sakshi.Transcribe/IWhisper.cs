namespace Sakshi.Transcribe;

public interface IWhisper
{
    /// <param name="words">Also return each segment's words with their times.</param>
    /// <param name="maxContext">Tokens of the previous window carried into the next: 0 = none, -1 = whisper's full context.</param>
    Task<IReadOnlyList<Segment>> TranscribeAsync(
        string wavPath, string modelPath, string language, int maxContext, CancellationToken ct, bool words = false);
}
