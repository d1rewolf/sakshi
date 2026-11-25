using Sakshi.Speakers;

namespace Sakshi.Speakers.Tests;

public sealed class SherpaIntegrationTests
{
    [Fact]
    public void LabelsARealFile()
    {
        var models = Environment.GetEnvironmentVariable("SAKSHI_SPEAKER_MODELS");
        var wav = Environment.GetEnvironmentVariable("SAKSHI_SPEAKER_WAV");
        if (string.IsNullOrEmpty(models) || string.IsNullOrEmpty(wav))
            Assert.Skip(
            "set SAKSHI_SPEAKER_MODELS and SAKSHI_SPEAKER_WAV to run");
        var paths = new SpeakerModelPaths(
            Path.Combine(models!, "pyannote-segmentation-3-0.onnx"),
            Path.Combine(models!, "wespeaker_en_voxceleb_resnet34_LM.onnx"));
        var (rate, samples) = WavFile.Read(wav!);
        Assert.Equal(16000, rate);

        using var diarizer = new SherpaDiarizer(paths.Segmentation, paths.Embedding, 2);
        var turns = diarizer.Diarize(samples, CancellationToken.None);
        Console.WriteLine($"turns={turns.Count} pieces={turns.Select(t => t.Piece).Distinct().Count()}");
        Assert.NotEmpty(turns);

        using var printer = new SherpaVoiceprinter(paths.Embedding, 2);
        var t0 = turns[0];
        var v = printer.Compute(samples, new[] { (t0.Start, t0.End) });
        Console.WriteLine($"model={printer.ModelName} length={v.Length}");
        Assert.Equal(256, v.Length);
    }

    [Fact]
    public void SpanSamplesIgnoresEmptyAndNonFiniteSpans()
    {
        var samples = new float[32000];
        Assert.Empty(SherpaVoiceprinter.SpanSamples(samples, Array.Empty<(double, double)>()));
        Assert.Empty(SherpaVoiceprinter.SpanSamples(samples, new[] {
            (double.NaN, 1.0), (0.0, double.PositiveInfinity), (3.0, 4.0), (1.0, 1.0), (1.5, 0.5) }));
        var parts = SherpaVoiceprinter.SpanSamples(samples, new[] { (0.0, 0.5), (1.5, 9.0) });
        Assert.Equal(new[] { 8000, 8000 }, parts.Select(p => p.Length).ToArray());
    }
}
