using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Sakshi.App.Prompts;
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

    /// <summary>How to use a fix command, naming the terminal this system has.</summary>
    internal static string HowTo => $"Where a command is shown, run it in {Terminal} (Copy puts it on the clipboard), then choose Check again.";

    private static string Terminal => OperatingSystem.IsWindows() ? "PowerShell" : OperatingSystem.IsMacOS() ? "Terminal" : "a terminal";

    private static readonly IBrush Good = Witness.Brush(Witness.Good);
    private static readonly IBrush Bad = Witness.Brush(Witness.Bad);
    private static readonly IBrush Info = Witness.Brush(Witness.Info);
    private static readonly IBrush Muted = Witness.Brush(Witness.Muted);

    /// <summary>Width of the mark column; the detail, command and buttons of a row start after it.</summary>
    private const double MarkWidth = 24;

    private readonly Func<Task<IReadOnlyList<DoctorCheck>>> _check;
    private readonly Action<string> _openUrl;
    private readonly Action<string> _log;
    private readonly TextBlock _summary = new() { TextWrapping = TextWrapping.Wrap, FontSize = 14, LineHeight = 20 };
    private readonly StackPanel _rows = new() { Spacing = 16 };
    private readonly Button _again = new() { Content = "Check again" };
    private readonly Button _close = new() { Content = "Close" };

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
        Witness.Brand(this);

        _again.Click += async (_, _) =>
        {
            try { await CheckAgainAsync(); }
            catch (Exception e) { _log($"setup: check again failed: {e.Message}"); }
        };
        _close.Click += (_, _) => Close();
        KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Escape) { e.Handled = true; Close(); } };   // like the prompts
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(_again);
        buttons.Children.Add(_close);

        var root = new StackPanel { Margin = new Thickness(24, 20, 24, 20), Spacing = 18 };
        root.Children.Add(new TextBlock { Text = WindowTitle, FontSize = 22, FontWeight = FontWeight.SemiBold });
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

    internal Button CloseButton => _close;

    private void Render(IReadOnlyList<DoctorCheck> checks)
    {
        var missing = checks.Count(c => !c.Ok && c.Required);
        var anyFix = checks.Any(c => !c.Ok && c.Fix is not null);
        _summary.Text = missing > 0
            ? $"{missing} {(missing == 1 ? "thing needs" : "things need")} fixing before Sakshi can record. {HowTo}"
            : checks.Any(c => !c.Ok) ? $"{OnlyOptional}{(anyFix ? " " + HowTo : "")}" : AllSet;
        _rows.Children.Clear();
        foreach (var c in checks) _rows.Children.Add(Row(c));
        // The main button: Check again while something is left to do, Close once everything is in place.
        var done = checks.All(c => c.Ok);
        _again.Classes.Set("accent", !done);
        _close.Classes.Set("accent", done);
    }

    private Control Row(DoctorCheck c)
    {
        // ℹ: advice (optional, or a setting Sakshi cannot check), not a pass or a failure.
        var (mark, brush) = !c.Ok ? (c.Required ? ("✗", Bad) : ("ℹ", Info))
            : c.SettingsUrl is not null || !c.Required ? ("ℹ", Info) : ("✓", Good);
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions($"{MarkWidth},*") };
        head.Children.Add(new TextBlock { Text = mark, Foreground = brush, FontWeight = FontWeight.Bold, FontSize = 15 });
        var name = new TextBlock { Text = c.Name, FontWeight = FontWeight.SemiBold, FontSize = 15 };
        Grid.SetColumn(name, 1);
        head.Children.Add(name);

        var indent = new Thickness(MarkWidth, 0, 0, 0);
        var row = new StackPanel { Spacing = 6, Tag = c.Name };
        row.Children.Add(head);
        row.Children.Add(new TextBlock { Text = c.Detail, TextWrapping = TextWrapping.Wrap, Foreground = Muted, Margin = indent });
        if (!c.Ok && c.Fix is { } fix) row.Children.Add(FixLine(fix, indent));
        if (c.SettingsUrl is { } url)
        {
            var open = new Button { Content = "Open settings", Margin = indent };
            open.Click += (_, _) => _openUrl(url);
            row.Children.Add(open);
        }
        return row;
    }

    private Control FixLine(string fix, Thickness indent)
    {
        var text = new TextBox
        {
            Text = fix, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, FontFamily = Witness.Mono, FontSize = 12,
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
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = indent };
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
