namespace Sakshi.Transcribe;

public sealed record Segment(TimeSpan Start, TimeSpan End, string Text);
