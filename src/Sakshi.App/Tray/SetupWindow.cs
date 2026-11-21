using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Sakshi.Platform.Doctor;

namespace Sakshi.App.Tray;

/// <summary>
/// "Set up Sakshi": doctor's checks, each failed one with its fix command and a Copy button, a settings link where
/// a check has one, and Check again. Built in code like the prompt windows.
/// </summary>
internal sealed class SetupWindow : Window
{
    internal const string WindowTitle = "Set up Sakshi";
    internal const string AllSet = "Everything Sakshi needs is in place. You can close this window.";
    internal const string OnlyOptional = "Sakshi has what it needs to record. The optional items below make it nicer.";
    internal const string HowTo = "Where a command is shown, run it in a terminal (Copy puts it on the clipboard), then choose Check again.";

    private static readonly IBrush Good = new SolidColorBrush(Color.FromRgb(0x7f, 0xc8, 0x7f));
    private static readonly IBrush Bad = new SolidColorBrush(Color.FromRgb(0xf0, 0x80, 0x70));
    private static readonly IBrush Info = new SolidColorBrush(Color.FromRgb(0x8a, 0xb4, 0xf8));
    private static readonly FontFamily Mono = new("Cascadia Mono, Menlo, DejaVu Sans Mono, monospace");

    private readonly Func<Task<IReadOnlyList<DoctorCheck>>> _check;
    private readonly Action<string> _openUrl;
    private readonly Action<string> _log;
    private readonly TextBlock _summary = new() { TextWrapping = TextWrapping.Wrap, FontSize = 14 };
    private readonly StackPanel _rows = new() { Spacing = 14 };
    private readonly Button _again = new() { Content = "Check again" };

    public SetupWindow(IReadOnlyList<DoctorCheck> checks, Func<Task<IReadOnlyList<DoctorCheck>>> check, Action<string> openUrl,
        Action<string> log)
    {
        _check = check;
        _openUrl = openUrl;
        _log = log;
        Title = WindowTitle;
        Width = 640;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _again.Click += async (_, _) =>
        {
            try { await CheckAgainAsync(); }
            catch (Exception e) { _log($"setup: check again failed: {e.Message}"); }
        };
        var close = new Button { Content = "Close" };
        close.Click += (_, _) => Close();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(_again);
        buttons.Children.Add(close);

        var root = new StackPanel { Margin = new Thickness(20), Spacing = 16 };
        root.Children.Add(new TextBlock { Text = WindowTitle, FontSize = 20, FontWeight = FontWeight.SemiBold });
        root.Children.Add(_summary);
        root.Children.Add(new ScrollViewer
        {
            MaxHeight = 560,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = _rows,
        });
        root.Children.Add(buttons);
        Content = root;
        Render(checks);
    }

    /// <summary>The text of the summary line and of every row, for tests.</summary>
    internal string Summary => _summary.Text ?? "";

    internal IReadOnlyList<Control> Rows => [.. _rows.Children];

    internal Button CheckAgainButton => _again;

    private void Render(IReadOnlyList<DoctorCheck> checks)
    {
        var missing = checks.Count(c => !c.Ok && c.Required);
        var anyFix = checks.Any(c => !c.Ok && c.Fix is not null);
        _summary.Text = missing > 0
            ? $"{missing} {(missing == 1 ? "thing needs" : "things need")} fixing before Sakshi can record. {HowTo}"
            : checks.Any(c => !c.Ok) ? $"{OnlyOptional}{(anyFix ? " " + HowTo : "")}" : AllSet;
        _rows.Children.Clear();
        foreach (var c in checks) _rows.Children.Add(Row(c));
    }

    private Control Row(DoctorCheck c)
    {
        // ℹ: advice (optional, or a setting Sakshi cannot check), not a pass or a failure.
        var (mark, brush) = !c.Ok ? (c.Required ? ("✗", Bad) : ("ℹ", Info))
            : c.SettingsUrl is not null || !c.Required ? ("ℹ", Info) : ("✓", Good);
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        head.Children.Add(new TextBlock { Text = mark, Foreground = brush, FontWeight = FontWeight.Bold, FontSize = 15 });
        head.Children.Add(new TextBlock { Text = c.Name, FontWeight = FontWeight.SemiBold, FontSize = 15 });

        var row = new StackPanel { Spacing = 6, Tag = c.Name };
        row.Children.Add(head);
        row.Children.Add(new TextBlock { Text = c.Detail, TextWrapping = TextWrapping.Wrap, Opacity = 0.8, Margin = new Thickness(23, 0, 0, 0) });
        if (!c.Ok && c.Fix is { } fix) row.Children.Add(FixLine(fix));
        if (c.SettingsUrl is { } url)
        {
            var open = new Button { Content = "Open settings", Margin = new Thickness(23, 0, 0, 0) };
            open.Click += (_, _) => _openUrl(url);
            row.Children.Add(open);
        }
        return row;
    }

    private Control FixLine(string fix)
    {
        var text = new TextBox
        {
            Text = fix, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, FontFamily = Mono, FontSize = 12,
        };
        var copy = new Button { Content = "Copy", VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(8, 0, 0, 0) };
        copy.Click += async (_, _) =>
        {
            // An event handler: nothing may escape it, or it would take the tray down mid-recording.
            try
            {
                if (Clipboard is not { } clipboard) throw new InvalidOperationException("no clipboard");
                await clipboard.SetTextAsync(fix);
                copy.Content = "Copied";
                Avalonia.Threading.DispatcherTimer.RunOnce(() => copy.Content = "Copy", TimeSpan.FromSeconds(2));
            }
            catch (Exception e)
            {
                _log($"setup: copy failed: {e.Message}");
                copy.Content = "Copy failed — select the text";
            }
        };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(23, 0, 0, 0) };
        grid.Children.Add(text);
        Grid.SetColumn(copy, 1);
        grid.Children.Add(copy);
        return grid;
    }

    internal async Task CheckAgainAsync()
    {
        _again.IsEnabled = false;
        _again.Content = "Checking…";
        try
        {
            Render(await _check());
        }
        finally
        {
            _again.Content = "Check again";
            _again.IsEnabled = true;
        }
    }
}
