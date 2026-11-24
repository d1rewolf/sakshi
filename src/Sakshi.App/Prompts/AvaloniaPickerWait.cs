using Avalonia.Threading;
using Sakshi.Core.Ports;

namespace Sakshi.App.Prompts;

/// <summary>Shows <see cref="PickerWaitWindow"/>; called from any thread, so everything runs on the UI thread.</summary>
public sealed class AvaloniaPickerWait : IPickerWait
{
    private PickerWaitWindow? _window;

    /// <summary>The notice now showing, if any (for tests).</summary>
    internal PickerWaitWindow? Window => _window;

    public void Opening() => Dispatcher.UIThread.Post(() =>
    {
        if (_window is not null) return;
        _window = new PickerWaitWindow();
        _window.Show();
    });

    public void Slow() => Dispatcher.UIThread.Post(() => _window?.ShowSlow());

    public void Done() => Dispatcher.UIThread.Post(() =>
    {
        _window?.Close();
        _window = null;
    });
}
