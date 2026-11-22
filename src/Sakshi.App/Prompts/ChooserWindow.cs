using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Sakshi.Core.Ports;

namespace Sakshi.App.Prompts;

/// <summary>A titled list of glyph/label/description rows. Up/Down move, Enter accepts, Esc dismisses, click picks.</summary>
internal sealed class ChooserWindow : Window
{
    internal const string HintText = "Click one, or use ↑ ↓ and Enter.";

    // avares host is the assembly name (sakshi), not the project name. Bundled because macOS and Windows have no Nerd Font; keeps the frame-mode glyphs from showing as boxes.
    private static readonly FontFamily GlyphFont =
        new("avares://sakshi/Assets#Symbols Nerd Font Mono, $Default");
    private static readonly IBrush Marker = Witness.Brush(Witness.Amber);
    private static readonly IBrush Raised = Witness.Brush(Witness.Surface);
    private static readonly IBrush Hovered = Witness.Brush(Witness.RowHover);
    private static readonly IBrush MutedBrush = Witness.Brush(Witness.Muted);
    private readonly ChooserModel _model;
    private readonly List<(Border Row, TextBlock Glyph)> _rows = [];
    private int _hover = -1;

    public ChooserWindow(string title, IReadOnlyList<PromptRow> rows)
    {
        _model = new ChooserModel(rows);
        PromptWindow.Configure(this);

        var list = new StackPanel { Spacing = 2 };
        // Rows without glyphs (the window picker) start at the left edge instead of after an empty column.
        var glyphColumn = rows.Any(r => !string.IsNullOrEmpty(r.Glyph)) ? "40" : "4";
        for (var i = 0; i < rows.Count; i++)
        {
            var index = i;
            var row = rows[i];
            var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            // A long window title ends in "…" on one line; the full title is in its tooltip.
            var label = new TextBlock { Text = row.Label, FontSize = 15, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
            ToolTip.SetTip(label, row.Label);
            text.Children.Add(label);
            text.Children.Add(new TextBlock
            {
                Text = row.Description, FontSize = 12, Foreground = MutedBrush, TextWrapping = TextWrapping.Wrap,
                IsVisible = !string.IsNullOrEmpty(row.Description),
            });
            var glyph = new TextBlock
            {
                Text = row.Glyph, FontSize = 20, FontFamily = GlyphFont, VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions($"{glyphColumn},*") };
            grid.Children.Add(glyph);
            Grid.SetColumn(text, 1);
            grid.Children.Add(text);
            // The left edge is always 3 px wide, so marking the chosen row moves nothing.
            var border = new Border
            {
                Padding = new Thickness(8, 10, 12, 10), CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(3, 0, 0, 0),
                Child = grid, Cursor = new Cursor(StandardCursorType.Hand),
            };
            border.PointerPressed += (_, e) =>
            {
                if (!e.GetCurrentPoint(border).Properties.IsLeftButtonPressed) return;
                _model.Pick(index);
                Finish();
                e.Handled = true;
            };
            border.PointerEntered += (_, _) => { _hover = index; Paint(); };
            border.PointerExited += (_, _) => { if (_hover == index) _hover = -1; Paint(); };
            _rows.Add((border, glyph));
            list.Children.Add(border);
        }

        var root = new StackPanel { Margin = new Thickness(18, 16, 18, 14), Spacing = 12 };
        root.Children.Add(PromptWindow.Heading(title));
        // Rows scroll once the list outgrows the screen; the title stays put above.
        var scroll = new ScrollViewer { MaxHeight = 480, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, Content = list };
        root.Children.Add(scroll);
        root.Children.Add(PromptWindow.Hint(HintText));
        Content = root;

        _model.SelectionChanged += Paint;
        Paint();
        AddHandler(KeyDownEvent, OnKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    /// <summary>The chosen label, or null when dismissed. Read after the window closed.</summary>
    public string? Result => _model.Result;

    internal Control RowControl(int index) => _rows[index].Row;

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (!_model.Handle(e.Key)) return;
        e.Handled = true;
        if (_model.Done) Finish();
    }

    private void Finish() => Close();

    private void Paint()
    {
        for (var i = 0; i < _rows.Count; i++)
        {
            var (row, glyph) = _rows[i];
            var chosen = i == _model.Index;
            row.Background = chosen ? Raised : i == _hover ? Hovered : Brushes.Transparent;
            row.BorderBrush = chosen ? Marker : Brushes.Transparent;
            glyph.Foreground = chosen ? Marker : MutedBrush;
        }
        if (_rows.Count > 0) _rows[_model.Index].Row.BringIntoView();
    }
}
