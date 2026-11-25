namespace Sakshi.Transcribe;

/// <summary>What speaker labelling made of a transcript. Speakers == 0 means no labels: Segments is the input unchanged.</summary>
public sealed record SpeakerOutcome(IReadOnlyList<Segment> Segments, int Speakers, int Suggested, int Unknown);

/// <summary>Labels a meeting's segments with speakers and writes speakers.json and the speaker clips into outputDir.</summary>
public interface ISpeakerStep
{
    Task<SpeakerOutcome> LabelAsync(string audioWav, string outputDir, IReadOnlyList<Segment> segments, string source,
        IReadOnlyList<FrameRecord>? frames, TextWriter stdout, CancellationToken ct);
}
