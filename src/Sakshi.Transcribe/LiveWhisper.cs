using Whisper.net;

namespace Sakshi.Transcribe;

/// <summary>Whisper for the live transcript: one short piece of audio at a time.</summary>
public interface ILiveWhisper
{
    /// <summary>The segments in <paramref name="wav"/> (16 kHz mono 16-bit), with times from its start.</summary>
    Task<IReadOnlyList<Segment>> TranscribeAsync(Stream wav, CancellationToken ct);
}

/// <summary>
/// Loads the model once and keeps it for the whole recording: loading it costs about as much as transcribing a
/// piece. Greedy decoding rather than the after-meeting beam search, for speed; the full transcript after the
/// meeting is unchanged. English, like the after-meeting default.
/// </summary>
public sealed class LiveWhisperNet : ILiveWhisper, IDisposable
{
    private readonly WhisperFactory _factory;
    private readonly WhisperProcessor _processor, _short;

    /// <summary>
    /// Whisper always works on 30 s of sound, however short the piece, unless told otherwise; the live pieces are a few
    /// seconds, so a second processor looks at only <see cref="ShortSeconds"/> (an audio context of 256: 20 ms each).
    /// Measured on the base model: under half the work per second of meeting of 5 s pieces with the full 30 s.
    /// </summary>
    internal const double ShortSeconds = 5.12;

    public LiveWhisperNet(string modelPath, int threads)
    {
        _factory = WhisperFactory.FromPath(modelPath);
        _processor = Builder(threads).Build();
        _short = Builder(threads).WithAudioContextSize(256).Build();
    }

    private WhisperProcessorBuilder Builder(int threads) => _factory.CreateBuilder()
        .WithGreedySamplingStrategy()
        .WithLanguage("en")
        .WithThreads(threads);

    public async Task<IReadOnlyList<Segment>> TranscribeAsync(Stream wav, CancellationToken ct)
    {
        var segments = new List<Segment>();
        var seconds = (wav.Length - 44) / 32000.0;   // 16 kHz mono 16-bit, after the header
        var processor = seconds <= ShortSeconds ? _short : _processor;
        await foreach (var s in processor.ProcessAsync(wav, ct))
            if (!Segment.IsBlank(s.Text)) segments.Add(new Segment(s.Start, s.End, s.Text));
        return segments;
    }

    public void Dispose()
    {
        _processor.Dispose();
        _short.Dispose();
        _factory.Dispose();
    }
}
