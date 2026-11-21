using Sakshi.Core.Ports;

namespace Sakshi.Platform.MacOS;

/// <summary>
/// macOS notifications through osascript's display notification, for the unbundled build (a
/// notification API needs a bundle id). macOS has no urgency, so it is ignored. Never throws and never
/// blocks the caller: sends run one after another on a <see cref="QueuedNotifier"/>.
/// </summary>
public sealed class OsascriptNotifier : INotifier
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private readonly IProcessRunner _runner;
    private readonly ILog _log;
    private readonly QueuedNotifier _queue;

    public OsascriptNotifier(IProcessRunner runner, ILog log)
    {
        _runner = runner;
        _log = log;
        _queue = new QueuedNotifier(SendAsync, log);
    }

    /// <summary>
    /// The osascript command. The text goes in as arguments (argv), never spliced into the script, so
    /// quotes and backslashes in a meeting name cannot break or inject AppleScript.
    /// </summary>
    public static (string File, string[] Args) Command(string headline, string body) =>
        ("osascript", ["-e", "on run argv",
            "-e", "display notification (item 2 of argv) with title \"Sakshi\" subtitle (item 1 of argv)",
            "-e", "end run", headline, body]);

    public void Notify(string headline, string body, Urgency urgency) => _queue.Notify(headline, body, urgency);

    /// <summary>Completes when every notification queued so far has been sent.</summary>
    public Task Drain() => _queue.Drain();

    private async Task SendAsync(string headline, string body, Urgency _)
    {
        var (file, args) = Command(headline, body);
        var r = await _runner.RunAsync(file, args, Timeout, CancellationToken.None).ConfigureAwait(false);
        if (r.ExitCode != 0) _log.Info($"notification failed ({file} exit {r.ExitCode}): {r.StdErr.Trim()}");
    }
}
