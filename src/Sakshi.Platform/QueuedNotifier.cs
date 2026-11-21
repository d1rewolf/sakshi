using Sakshi.Core.Ports;

namespace Sakshi.Platform;

/// <summary>
/// Runs <paramref name="send"/> for each notification, one after another on a private task chain, so
/// <see cref="Notify"/> never blocks the caller and notifications arrive in order. A send that fails is
/// logged and the chain carries on: a notification is never worth a crash.
/// </summary>
public sealed class QueuedNotifier(Func<string, string, Urgency, Task> send, ILog log) : INotifier
{
    private readonly object _gate = new();
    private Task _chain = Task.CompletedTask;

    public void Notify(string headline, string body, Urgency urgency)
    {
        lock (_gate)
            _chain = _chain.ContinueWith(_ => SendAsync(headline, body, urgency), CancellationToken.None,
                TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
    }

    /// <summary>Completes when every notification queued so far has been sent.</summary>
    public Task Drain()
    {
        lock (_gate) return _chain;
    }

    private async Task SendAsync(string headline, string body, Urgency urgency)
    {
        try { await send(headline, body, urgency).ConfigureAwait(false); }
        catch (Exception e)
        {
            try { log.Info($"notification failed: {e.Message}"); } catch (Exception) { }
        }
    }
}
