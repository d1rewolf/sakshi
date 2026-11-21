using Sakshi.Core.State;

namespace Sakshi.Core.Transcription;

public interface ITranscriber
{
    /// <summary>Runs the transcription for a job; returns the exit code. <paramref name="onStarted"/> gets the child's pid.</summary>
    Task<int> RunAsync(TranscriptionJob job, Action<int> onStarted, CancellationToken ct);
}
