using SherpaOnnx;

namespace Sakshi.Speakers;

/// <summary>One instance serves one caller at a time.</summary>
public sealed class SherpaVoiceprinter : IVoiceprinter
{
    private const int Rate = 16000;
    private readonly SpeakerEmbeddingExtractor _ex;

    public string ModelName { get; }

    public SherpaVoiceprinter(string embeddingModel, int threads)
    {
        // sherpa aborts the whole process on a missing model, so check first.
        if (!File.Exists(embeddingModel)) throw new FileNotFoundException("speaker model not found", embeddingModel);
        ModelName = Path.GetFileNameWithoutExtension(embeddingModel);
        _ex = new SpeakerEmbeddingExtractor(new SpeakerEmbeddingExtractorConfig { Model = embeddingModel, NumThreads = threads });
    }

    /// <summary>The samples inside the spans, in order. Spans that are NaN, infinite, reversed or off the end add nothing.</summary>
    internal static List<float[]> SpanSamples(float[] samples, IReadOnlyList<(double Start, double End)> spans)
    {
        var parts = new List<float[]>();
        foreach (var (start, end) in spans)
        {
            if (!double.IsFinite(start) || !double.IsFinite(end)) continue;
            var a = (int)Math.Clamp(start * Rate, 0, samples.Length);
            var b = (int)Math.Clamp(end * Rate, a, samples.Length);
            if (b > a) parts.Add(samples[a..b]);
        }
        return parts;
    }

    public float[] Compute(float[] samples, IReadOnlyList<(double Start, double End)> spans)
    {
        var parts = SpanSamples(samples, spans);
        if (parts.Count == 0) throw new ArgumentException("no audio inside the given spans", nameof(spans));
        using var stream = _ex.CreateStream();
        foreach (var part in parts) stream.AcceptWaveform(Rate, part);
        stream.InputFinished();
        return _ex.Compute(stream);
    }

    public void Dispose() => _ex.Dispose();
}
