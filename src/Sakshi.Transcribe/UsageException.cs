namespace Sakshi.Transcribe;

/// <summary>Bad command-line usage; the message is what gets printed.</summary>
public sealed class UsageException(string message) : Exception(message);
