using Whisper.net;

namespace Sakshi.Transcribe;

/// <summary>Whisper.net (in-process whisper.cpp). Not exercised in CI: that would need a model download.</summary>
/// <param name="threads">How many CPU threads Whisper may use; null leaves its default.</param>
public sealed class WhisperNet(int? threads = null) : IWhisper
{
    // whisper-cli's defaults (-bs 5), so transcripts match the ones the bash tool produced.
    private const int BeamSize = 5;

    public async Task<IReadOnlyList<Segment>> TranscribeAsync(
        string wavPath, string modelPath, string language, int maxContext, CancellationToken ct, bool words = false)
    {
        using var factory = WhisperFactory.FromPath(modelPath);
        var builder = factory.CreateBuilder()
            .WithBeamSearchSamplingStrategy(s => s.WithBeamSize(BeamSize));
        builder = language == "auto" ? builder.WithLanguageDetection() : builder.WithLanguage(language);
        if (threads is { } n) builder = builder.WithThreads(n);
        // -1 leaves whisper's own default (full context); 0 is today's -mc 0.
        if (maxContext >= 0) builder = builder.WithMaxLastTextTokens(maxContext);

        if (words) builder = builder.WithTokenTimestamps();

        using var processor = builder.Build();
        await using var wav = File.OpenRead(wavPath);
        var segments = new List<Segment>();
        await foreach (var s in processor.ProcessAsync(wav, ct))
            if (!Segment.IsBlank(s.Text)) segments.Add(words
                ? new Segment(s.Start, s.End, s.Text) { Words = WordTimes.FromTokens(s.Tokens.Select(t => (t.Text ?? "", t.Start, t.End))) }
                : new Segment(s.Start, s.End, s.Text));
        return segments;
    }
}
