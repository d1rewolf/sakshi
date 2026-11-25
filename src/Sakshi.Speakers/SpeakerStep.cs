using Sakshi.Transcribe;

namespace Sakshi.Speakers;

/// <summary>Speaker labelling with the sherpa-onnx models and the user's voice library. Any failure throws, and the
/// caller keeps the transcript without labels.</summary>
public sealed class SpeakerStep(string modelsDir, string voicesFile, int threads) : ISpeakerStep
{
    public async Task<SpeakerOutcome> LabelAsync(string audioWav, string outputDir, IReadOnlyList<Segment> segments,
        string source, IReadOnlyList<FrameRecord>? frames, TextWriter stdout, CancellationToken ct)
    {
        if (await SpeakerModels.EnsureAsync(modelsDir, stdout, ct) is { } reason)
            throw new InvalidOperationException(reason);

        VoiceLibrary library;
        var writable = true;
        try
        {
            library = VoiceLibrary.Load(voicesFile);
        }
        catch (VoiceLibraryException e)
        {
            stdout.WriteLine($"Warning: {e.Message}; voices are not saved this time");
            library = VoiceLibrary.Unsaved();
            writable = false;
        }

        var (rate, samples) = WavFile.Read(audioWav);
        if (rate != 16000) throw new InvalidDataException($"{audioWav} is {rate} Hz, not 16000 Hz");

        var models = SpeakerModels.In(modelsDir);
        return await Task.Run(() =>
        {
            using var diarizer = new SherpaDiarizer(models.Segmentation, models.Embedding, threads);
            using var printer = new SherpaVoiceprinter(models.Embedding, threads);
            return new SpeakerLabeller(diarizer, printer, library, writable)
                .Label(samples, outputDir, segments, source, frames, ct);
        }, ct);
    }
}
