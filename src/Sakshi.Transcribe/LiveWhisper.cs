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
    private readonly WhisperProcessor _processor;

    public LiveWhisperNet(string modelPath, int threads)
    {
        _factory = WhisperFactory.FromPath(modelPath);
        _processor = _factory.CreateBuilder()
            .WithGreedySamplingStrategy()
            .WithLanguage("en")
            .WithThreads(threads)
            .Build();
    }

    public async Task<IReadOnlyList<Segment>> TranscribeAsync(Stream wav, CancellationToken ct)
    {
        var segments = new List<Segment>();
        await foreach (var s in _processor.ProcessAsync(wav, ct))
            segments.Add(new Segment(s.Start, s.End, s.Text));
        return segments;
    }

    public void Dispose()
    {
        _processor.Dispose();
        _factory.Dispose();
    }
}
