namespace Sakshi.Core.Ports;

public enum Urgency { Low, Normal, Critical }

public interface INotifier
{
    /// <summary>
    /// Shows a notification. Must not throw: callers report and carry on (a hand-off, a state
    /// change) and do not guard each call. Wrap an implementation that can fail in
    /// <see cref="SafeNotifier"/>.
    /// </summary>
    void Notify(string headline, string body, Urgency urgency);
}
