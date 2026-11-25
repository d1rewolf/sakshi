namespace Sakshi.Transcribe;

/// <summary>One word with its times. <paramref name="Text"/> keeps Whisper's leading space.</summary>
public sealed record Word(TimeSpan Start, TimeSpan End, string Text);
