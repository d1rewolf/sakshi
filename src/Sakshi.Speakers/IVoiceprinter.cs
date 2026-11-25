namespace Sakshi.Speakers;

public interface IVoiceprinter : IDisposable
{
    string ModelName { get; }

    /// <summary>One voiceprint from the given spans (seconds) of 16 kHz mono samples.</summary>
    float[] Compute(float[] samples, IReadOnlyList<(double Start, double End)> spans);
}
