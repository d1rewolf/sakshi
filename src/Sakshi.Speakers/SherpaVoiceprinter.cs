using SherpaOnnx;

namespace Sakshi.Speakers;

public sealed class SherpaVoiceprinter : IVoiceprinter
{
    private const int Rate = 16000;
    private readonly SpeakerEmbeddingExtractor _ex;

    public string ModelName { get; }

    public SherpaVoiceprinter(string embeddingModel, int threads)
    {
        // sherpa aborts the whole process on a missing model, so check first.
        foreach (var m in new[] { embeddingModel })
            if (!File.Exists(m)) throw new FileNotFoundException("speaker model not found", m);
        ModelName = Path.GetFileNameWithoutExtension(embeddingModel);
        _ex = new SpeakerEmbeddingExtractor(new SpeakerEmbeddingExtractorConfig { Model = embeddingModel, NumThreads = threads });
    }

    public float[] Compute(float[] samples, IReadOnlyList<(double Start, double End)> spans)
    {
        using var stream = _ex.CreateStream();
        foreach (var (start, end) in spans)
        {
            var a = Math.Clamp((int)(start * Rate), 0, samples.Length);
            var b = Math.Clamp((int)(end * Rate), a, samples.Length);
            if (b > a) stream.AcceptWaveform(Rate, samples[a..b]);
        }
        stream.InputFinished();
        return _ex.Compute(stream);
    }

    public void Dispose() => _ex.Dispose();
}
