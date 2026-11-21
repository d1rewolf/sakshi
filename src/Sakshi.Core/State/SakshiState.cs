using System.Collections.Immutable;

namespace Sakshi.Core.State;

public enum CapturePhase { Idle, Starting, Recording, Naming }

public sealed record FrameChoice(string Args, string? HandOff);

public sealed record Capture(
    CapturePhase Phase,
    DateTimeOffset? Started = null,
    string? Video = null,
    DateTimeOffset? Stopped = null,
    string? Name = null,
    FrameChoice? Frames = null,
    string? SourceSize = null)
{
    public static Capture Idle { get; } = new(CapturePhase.Idle);
}

public sealed record TranscriptionJob(
    Guid Id,
    string Dir,
    string Video,
    string Name,
    string FrameArgs,
    string? HandOff,
    int? Pid = null,
    DateTimeOffset? Started = null);

public sealed record SakshiState(Capture Capture, ImmutableList<TranscriptionJob> Jobs)
{
    public static SakshiState Empty { get; } = new(Capture.Idle, ImmutableList<TranscriptionJob>.Empty);
}
