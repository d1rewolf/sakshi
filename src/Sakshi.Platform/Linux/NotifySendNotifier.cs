using Sakshi.Core.Ports;

namespace Sakshi.Platform.Linux;

/// <summary>
/// Desktop notifications through omarchy-notification-send, else notify-send. Never throws and never
/// blocks the caller: sends run one after another on a <see cref="QueuedNotifier"/>.
/// </summary>
public sealed class NotifySendNotifier : INotifier
{
    private const string OmarchyGlyph = "\U000F044A"; // bytes f3 b0 91 8a, as bin/sakshi's notify()
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private readonly IProcessRunner _runner;
    private readonly ILog _log;
    private readonly QueuedNotifier _queue;

    public NotifySendNotifier(IProcessRunner runner, ILog log)
    {
        _runner = runner;
        _log = log;
        _queue = new QueuedNotifier(SendAsync, log);
    }

    public void Notify(string headline, string body, Urgency urgency) => _queue.Notify(headline, body, urgency);

    /// <summary>Completes when every notification queued so far has been sent.</summary>
    public Task Drain() => _queue.Drain();

    private async Task SendAsync(string headline, string body, Urgency urgency)
    {
        var u = urgency switch { Urgency.Low => "low", Urgency.Critical => "critical", _ => "normal" };
        string file;
        string[] args;
        if (_runner.Which("omarchy-notification-send") is not null)
        {
            file = "omarchy-notification-send";
            args = ["--app-name", "Sakshi", "-g", OmarchyGlyph, "-u", u, headline, body];
        }
        else if (_runner.Which("notify-send") is not null)
        {
            file = "notify-send";
            args = ["-a", "Sakshi", "-u", u, headline, body];
        }
        else return;
        var r = await _runner.RunAsync(file, args, Timeout, CancellationToken.None).ConfigureAwait(false);
        if (r.ExitCode != 0) _log.Info($"notification failed ({file} exit {r.ExitCode}): {r.StdErr.Trim()}");
    }
}
