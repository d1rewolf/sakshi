using SherpaOnnx;

namespace Sakshi.Speakers;

/// <summary>One instance serves one caller at a time.</summary>
public sealed class SherpaDiarizer : IDiarizer
{
    private readonly OfflineSpeakerDiarization _sd;

    public SherpaDiarizer(string segmentationModel, string embeddingModel, int threads)
    {
        // sherpa aborts the whole process on a missing model, so check first.
        if (!File.Exists(segmentationModel)) throw new FileNotFoundException("speaker model not found", segmentationModel);
        if (!File.Exists(embeddingModel)) throw new FileNotFoundException("speaker model not found", embeddingModel);
        var cfg = new OfflineSpeakerDiarizationConfig();
        cfg.Segmentation.Pyannote.Model = segmentationModel;
        cfg.Segmentation.NumThreads = threads;
        cfg.Embedding.Model = embeddingModel;
        cfg.Embedding.NumThreads = threads;
        cfg.Clustering.Threshold = (float)SpeakerSettings.ClusterThreshold;
        _sd = new OfflineSpeakerDiarization(cfg);
        var rate = _sd.SampleRate;
        if (rate != 16000)
        {
            _sd.Dispose();
            throw new InvalidOperationException($"speaker model expects {rate} Hz audio, not 16000");
        }
    }

    public IReadOnlyList<PieceTurn> Diarize(float[] samples, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // sherpa-onnx 2.0.3 ignores a non-zero progress-callback return (the run finishes), so cancellation
        // is only honoured before and after Process.
        var segs = _sd.Process(samples);
        ct.ThrowIfCancellationRequested();
        return segs.Select(s => new PieceTurn(s.Start, s.End, s.Speaker)).ToList();
    }

    public void Dispose() => _sd.Dispose();
}
