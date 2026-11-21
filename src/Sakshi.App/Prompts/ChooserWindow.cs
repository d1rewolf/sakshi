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
    private static readonly IBrush Selected = new SolidColorBrush(Color.FromRgb(0x3a, 0x3f, 0x4b));
    // avares host is the assembly name (sakshi), not the project name. Bundled because macOS and Windows have no Nerd Font; keeps the frame-mode glyphs from showing as boxes.
    private static readonly FontFamily GlyphFont =
        new("avares://sakshi/Assets#Symbols Nerd Font Mono, $Default");
    private readonly ChooserModel _model;
    private readonly List<Border> _rows = [];

    public ChooserWindow(string title, IReadOnlyList<PromptRow> rows)
    {
        _model = new ChooserModel(rows);
        PromptWindow.Configure(this);

        var list = new StackPanel { Spacing = 4 };
        for (var i = 0; i < rows.Count; i++)
        {
            var index = i;
            var row = rows[i];
            var text = new StackPanel { Spacing = 2 };
            text.Children.Add(new TextBlock { Text = row.Label, FontSize = 16 });
            text.Children.Add(new TextBlock
            {
                Text = row.Description, FontSize = 12, Opacity = 0.65,
                IsVisible = !string.IsNullOrEmpty(row.Description),
            });
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("36,*") };
            grid.Children.Add(new TextBlock { Text = row.Glyph, FontSize = 20, FontFamily = GlyphFont, VerticalAlignment = VerticalAlignment.Center });
            Grid.SetColumn(text, 1);
            grid.Children.Add(text);
            var border = new Border { Padding = new Thickness(10, 8), CornerRadius = new CornerRadius(6), Child = grid };
            border.PointerPressed += (_, e) =>
            {
                if (!e.GetCurrentPoint(border).Properties.IsLeftButtonPressed) return;
                _model.Pick(index);
                Finish();
                e.Handled = true;
            };
            _rows.Add(border);
            list.Children.Add(border);
        }

        var root = new StackPanel { Margin = new Thickness(16), Spacing = 10 };
        root.Children.Add(new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeight.SemiBold, Opacity = 0.8 });
        // Rows scroll once the list outgrows the screen; the title stays put above.
        var scroll = new ScrollViewer { MaxHeight = 520, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, Content = list };
        root.Children.Add(scroll);
        Content = root;

        _model.SelectionChanged += Paint;
        Paint();
        AddHandler(KeyDownEvent, OnKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    /// <summary>The chosen label, or null when dismissed. Read after the window closed.</summary>
    public string? Result => _model.Result;

    internal Control RowControl(int index) => _rows[index];

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
            _rows[i].Background = i == _model.Index ? Selected : Brushes.Transparent;
        if (_rows.Count > 0) _rows[_model.Index].BringIntoView();
    }
}
