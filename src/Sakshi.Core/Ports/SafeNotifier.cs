namespace Sakshi.Core.Ports;

/// <summary>
/// Makes any notifier honour the <see cref="INotifier"/> contract: a failure to show a
/// notification is logged and swallowed, never thrown into the code that reported something.
/// </summary>
public sealed class SafeNotifier(INotifier inner, ILog log) : INotifier
{
    public void Notify(string headline, string body, Urgency urgency)
    {
        try
        {
            inner.Notify(headline, body, urgency);
        }
        catch (Exception e)
        {
            log.Info($"notification failed ({headline}): {e.Message}");
        }
    }
}
