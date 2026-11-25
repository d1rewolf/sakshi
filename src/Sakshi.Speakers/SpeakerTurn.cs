namespace Sakshi.Speakers;

/// <summary>One stretch of one speaker, in seconds from the start of the recording.</summary>
public sealed record SpeakerTurn(double Start, double End, string Speaker);
