namespace Sakshi.Platform.Autostart;

/// <summary>Whether the tray starts at login. Enable and Disable are idempotent and throw <see cref="IOException"/> on failure.</summary>
public interface IAutostart { bool IsEnabled { get; } void Enable(string executable); void Disable(); }
