using System.Security;
using Avalonia;
using Avalonia.Labs.Notifications;
using Avalonia.Threading;
using Sakshi.Core.Ports;
using Sakshi.Platform;

namespace Sakshi.App.Notifications;

/// <summary>
/// Windows toasts through Avalonia.Labs.Notifications. Used on Windows only: on macOS the package needs a
/// bundle id (the unbundled build has none) and Linux has notify-send. An unpackaged app needs no
/// Start-menu shortcut: Labs registers the AppUserModelId under HKCU\Software\Classes\AppUserModelId.
/// Windows has no urgency here, so it is ignored. Never throws and never blocks the caller.
/// </summary>
internal sealed class LabsNotifier : INotifier
{
    private const string AppName = "Sakshi";
    // What Velopack gives its shortcuts and the process for pack id "sakshi". By default Labs derives an id
    // from the app name, which would register a second, unrelated notification sender.
    internal const string AppUserModelId = "velopack.sakshi";
    private readonly ILog _log;
    private readonly QueuedNotifier _queue;

    public LabsNotifier(ILog log)
    {
        _log = log;
        _queue = new QueuedNotifier(SendAsync, log);
    }

    /// <summary>Registers the toast manager on the tray's AppBuilder. Call on Windows only.</summary>
    public static AppBuilder Register(AppBuilder builder) =>
        builder.WithAppNotifications(new AppNotificationOptions { AppName = AppName, AppUserModelId = AppUserModelId });

    /// <summary>
    /// Labs splices the title and message into the toast XML unescaped, so a meeting called
    /// "Q&amp;A" would make the toast fail to load. Escape them here.
    /// </summary>
    internal static (string Title, string Message) ToastText(string headline, string body) =>
        (SecurityElement.Escape(headline), SecurityElement.Escape(body));

    public void Notify(string headline, string body, Urgency urgency) => _queue.Notify(headline, body, urgency);

    /// <summary>Completes when every notification queued so far has been shown.</summary>
    public Task Drain() => _queue.Drain();

    // The toast manager was set up on the UI thread (AfterSetup); show on it too.
    private async Task SendAsync(string headline, string body, Urgency _) =>
        await Dispatcher.UIThread.InvokeAsync(() => Show(headline, body));

    private void Show(string headline, string body)
    {
        if (NativeNotificationManager.Current?.CreateNotification(null) is not { } toast)
        {
            _log.Info($"notification not shown (Windows toasts are not set up): {headline}: {body}");
            return;
        }
        (toast.Title, toast.Message) = ToastText(headline, body);
        toast.Show();
    }
}
