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
    string? SourceSize = null,
    bool AudioOnly = false)
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

/// <summary>The user's own OBS profile and scene collection, to switch back to after Sakshi's recording.</summary>
public sealed record ObsReturn(string Profile, string Collection);

/// <param name="ObsLaunched">When the OBS that Sakshi started itself began (its process start time), so a later
/// recording knows that OBS is Sakshi's and not one the user opened.</param>
public sealed record SakshiState(Capture Capture, ImmutableList<TranscriptionJob> Jobs, ObsReturn? ObsReturn = null,
    DateTimeOffset? ObsLaunched = null)
{
    public static SakshiState Empty { get; } = new(Capture.Idle, ImmutableList<TranscriptionJob>.Empty);
}
