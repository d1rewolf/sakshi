using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Sakshi.App.Prompts;

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
    internal const string NothingYet = "Nothing said yet.";

    private static readonly IBrush Muted = Witness.Brush(Witness.Muted);
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Foreground = Muted, LineHeight = 20 };
    private readonly Border _statusBox;
    private readonly StackPanel _lines = new() { Spacing = 10 };
    private readonly List<string> _shownText = [];
    private readonly TextBlock _empty = new()
    {
        Text = NothingYet, Foreground = Muted, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 40, 0, 0),
    };
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
        MinWidth = 380;
        MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Witness.Brand(this);

        _lines.Children.Add(_empty);
        _scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = _lines,
            Padding = new Thickness(16, 14),
        };
        // A problem stands out from the routine progress line: a panel with a red edge, in full-strength text.
        _statusBox = new Border { Child = _status, CornerRadius = new CornerRadius(6) };
        _copy.Click += async (_, _) => await CopyAllAsync();
        var close = new Button { Content = "Close" };
        close.Click += (_, _) => Close();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(_copy);
        buttons.Children.Add(close);

        var root = new DockPanel { Margin = new Thickness(20, 18, 20, 18) };
        var head = new StackPanel { Spacing = 6, Margin = new Thickness(0, 0, 0, 14) };
        head.Children.Add(new TextBlock { Text = WindowTitle, FontSize = 20, FontWeight = FontWeight.SemiBold });
        head.Children.Add(_statusBox);
        DockPanel.SetDock(head, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        buttons.Margin = new Thickness(0, 12, 0, 0);
        root.Children.Add(head);
        root.Children.Add(buttons);
        root.Children.Add(new Border
        {
            Background = Witness.Brush(Witness.Surface), CornerRadius = new CornerRadius(8), Child = _scroll,
        });
        Content = root;

        _live.Changed += OnChanged;
        Closed += (_, _) => _live.Changed -= OnChanged;
        Render();
    }

    /// <summary>The recording this window shows.</summary>
    internal LiveTranscript Transcript => _live;

    internal string Status => _status.Text ?? "";

    internal IReadOnlyList<string> ShownLines => _shownText;

    internal Button CopyButton => _copy;

    /// <summary>Whether the "nothing said yet" placeholder is showing.</summary>
    internal bool ShowsNothingYet => _empty.IsVisible;

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
        var failed = view.State == LiveState.Failed;
        _status.Foreground = failed ? Witness.Brush(Witness.Text) : Muted;
        _statusBox.Background = failed ? Witness.Brush(Witness.Surface) : null;
        _statusBox.BorderBrush = failed ? Witness.Brush(Witness.Bad) : null;
        _statusBox.BorderThickness = failed ? new Thickness(3, 0, 0, 0) : new Thickness(0);
        _statusBox.Padding = failed ? new Thickness(12, 10) : new Thickness(0);
        _empty.IsVisible = view.Lines.Count == 0 && !failed;
        _copy.IsEnabled = view.Lines.Count > 0;
        // Follow the newest line only if the reader is already at the bottom.
        var atBottom = _scroll.Offset.Y >= _scroll.Extent.Height - _scroll.Viewport.Height - 24;
        for (; _shown < view.Lines.Count; _shown++)
        {
            var line = view.Lines[_shown];
            _shownText.Add(Line(line));
            _lines.Children.Add(Row(line));
        }
        if (atBottom) _scroll.ScrollToEnd();
    }

    /// <summary>
    /// One line: the time in a fixed column, so the words of every line start at the same place, and the words, which
    /// can be selected. The column fits "1:02:05".
    /// </summary>
    private static Control Row(LiveLine l)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("68,*") };
        grid.Children.Add(new TextBlock
        {
            Text = LiveTranscript.Clock(l.At), FontFamily = Witness.Mono, FontSize = 12, Foreground = Muted,
            Margin = new Thickness(0, 3, 0, 0),
        });
        var text = new SelectableTextBlock { Text = l.Text.Trim(), TextWrapping = TextWrapping.Wrap, LineHeight = 21 };
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        return grid;
    }

    internal static string Line(LiveLine l) => $"[{LiveTranscript.Clock(l.At)}]  {l.Text.Trim()}";

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
