using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Sakshi.App.Tray;

/// <summary>
/// "Live transcript": what is being said, a few seconds behind, newest at the bottom. Follows the newest line
/// unless the reader has scrolled up. Copy all puts the text so far on the clipboard.
/// </summary>
internal sealed class LiveTranscriptWindow : Window
{
    internal const string WindowTitle = "Live transcript";

    private readonly LiveTranscript _live;
    private readonly Action<string> _log;
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.75 };
    private readonly StackPanel _lines = new() { Spacing = 6 };
    private readonly ScrollViewer _scroll;
    private readonly Button _copy = new() { Content = "Copy all" };
    private int _shown;
    private bool _renderQueued;

    public LiveTranscriptWindow(LiveTranscript live, Action<string> log)
    {
        _live = live;
        _log = log;
        Title = WindowTitle;
        Width = 560;
        Height = 640;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = _lines,
        };
        _copy.Click += async (_, _) => await CopyAllAsync();
        var close = new Button { Content = "Close" };
        close.Click += (_, _) => Close();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(_copy);
        buttons.Children.Add(close);

        var root = new DockPanel { Margin = new Thickness(16) };
        var head = new StackPanel { Spacing = 4, Margin = new Thickness(0, 0, 0, 12) };
        head.Children.Add(new TextBlock { Text = WindowTitle, FontSize = 18, FontWeight = FontWeight.SemiBold });
        head.Children.Add(_status);
        DockPanel.SetDock(head, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        buttons.Margin = new Thickness(0, 12, 0, 0);
        root.Children.Add(head);
        root.Children.Add(buttons);
        root.Children.Add(_scroll);
        Content = root;

        _live.Changed += OnChanged;
        Closed += (_, _) => _live.Changed -= OnChanged;
        Render();
    }

    /// <summary>The recording this window shows.</summary>
    internal LiveTranscript Transcript => _live;

    internal string Status => _status.Text ?? "";

    internal IReadOnlyList<string> ShownLines => [.. _lines.Children.OfType<SelectableTextBlock>().Select(t => t.Text ?? "")];

    internal Button CopyButton => _copy;

    // From the reader thread: coalesce into one render on the UI thread.
    private void OnChanged()
    {
        lock (_lines)
        {
            if (_renderQueued) return;
            _renderQueued = true;
        }
        Dispatcher.UIThread.Post(() =>
        {
            lock (_lines) _renderQueued = false;
            Render();
        });
    }

    internal void Render()
    {
        var view = _live.Snapshot();
        _status.Text = view.Status;
        // Follow the newest line only if the reader is already at the bottom.
        var atBottom = _scroll.Offset.Y >= _scroll.Extent.Height - _scroll.Viewport.Height - 24;
        for (; _shown < view.Lines.Count; _shown++)
            _lines.Children.Add(new SelectableTextBlock { Text = Line(view.Lines[_shown]), TextWrapping = TextWrapping.Wrap });
        if (atBottom) _scroll.ScrollToEnd();
    }

    internal static string Line(LiveLine l) => $"[{LiveTranscript.Clock(l.At)}]  {l.Text}";

    private async Task CopyAllAsync()
    {
        // An event handler: nothing may escape it.
        try
        {
            if (Clipboard is not { } clipboard) throw new InvalidOperationException("no clipboard");
            await clipboard.SetTextAsync(string.Join(Environment.NewLine, _live.Snapshot().Lines.Select(Line)));
            _copy.Content = "Copied";
            DispatcherTimer.RunOnce(() => _copy.Content = "Copy all", TimeSpan.FromSeconds(2));
        }
        catch (Exception e)
        {
            _log($"live transcript: copy failed: {e.Message}");
            _copy.Content = "Copy failed";
        }
    }
}
