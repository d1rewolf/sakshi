using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Controls.Primitives;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace Sakshi.App.Prompts;

/// <summary>
/// Sakshi's look: dark only, in the logo's colours (packaging/sakshi.svg). Surfaces are the logo's slate ink instead of
/// black; the logo's amber marks the one thing that matters in each window (the chosen row, the main button, focus).
/// Every window takes its colours from here, never ad hoc.
/// </summary>
internal static class Witness
{
    public static readonly Color Ground = Color.Parse("#141D22");     // window background
    public static readonly Color Surface = Color.Parse("#1D2B33");    // the logo's ink: raised rows, fields, buttons
    public static readonly Color SurfaceHover = Color.Parse("#26363F");
    public static readonly Color RowHover = Color.Parse("#19252B");   // a row under the pointer: quieter than the chosen one
    public static readonly Color Line = Color.Parse("#30434D");       // borders
    public static readonly Color Text = Color.Parse("#ECEEEA");
    public static readonly Color Muted = Color.Parse("#9DABB2");      // secondary text: 7:1 on Ground
    public static readonly Color Amber = Color.Parse("#D98A1E");      // the logo's pupil
    public static readonly Color AmberHover = Color.Parse("#E59C38");
    public static readonly Color Good = Color.Parse("#7CC69A");
    public static readonly Color Bad = Color.Parse("#EE7D6B");
    public static readonly Color Info = Color.Parse("#8EB8D6");

    public static IBrush Brush(Color c) => new SolidColorBrush(c);

    public static readonly FontFamily Mono = new("Cascadia Mono, Consolas, Menlo, DejaVu Sans Mono, monospace");

    /// <summary>The Fluent theme in Sakshi's colours, plus the brushes its controls look up.</summary>
    public static void Apply(Application app)
    {
        app.RequestedThemeVariant = ThemeVariant.Dark;
        app.Styles.Add(new FluentTheme
        {
            Palettes =
            {
                [ThemeVariant.Dark] = new ColorPaletteResources
                {
                    Accent = Amber,
                    RegionColor = Ground,
                    AltHigh = Ground,
                    BaseHigh = Text,
                    ChromeMediumLow = Surface,
                },
            },
        });

        var r = app.Resources;
        void Set(string key, Color c) => r[key] = Brush(c);
        Set("ButtonBackground", Surface);
        Set("ButtonBackgroundPointerOver", SurfaceHover);
        Set("ButtonBackgroundPressed", Line);
        Set("ButtonForeground", Text);
        Set("ButtonForegroundPointerOver", Text);
        Set("ButtonForegroundPressed", Text);
        Set("ButtonBorderBrush", Line);
        Set("ButtonBorderBrushPointerOver", Line);
        Set("AccentButtonBackground", Amber);
        Set("AccentButtonBackgroundPointerOver", AmberHover);
        Set("AccentButtonBackgroundPressed", Amber);
        Set("AccentButtonForeground", Surface);           // ink on amber: 5:1
        Set("AccentButtonForegroundPointerOver", Surface);
        Set("AccentButtonForegroundPressed", Surface);
        Set("TextControlBackground", Surface);
        Set("TextControlBackgroundPointerOver", Surface);
        Set("TextControlBackgroundFocused", Surface);
        Set("TextControlBorderBrush", Line);
        Set("TextControlBorderBrushPointerOver", Muted);
        Set("TextControlForeground", Text);
        Set("TextControlForegroundFocused", Text);
        Set("TextControlForegroundPointerOver", Text);

        app.Styles.Add(new Style(x => x.Is<Window>())
        {
            Setters =
            {
                new Setter(TemplatedControl.BackgroundProperty, Brush(Ground)),
                new Setter(TemplatedControl.ForegroundProperty, Brush(Text)),
            },
        });
    }

    /// <summary>The app icon (packaging/sakshi.png) for title bars and the taskbar; null if it cannot be loaded.</summary>
    public static WindowIcon? Icon => _icon.Value;

    private static readonly Lazy<WindowIcon?> _icon = new(() =>
    {
        try
        {
            using var png = typeof(Witness).Assembly.GetManifestResourceStream("sakshi.png");
            return png is null ? null : new WindowIcon(png);
        }
        catch (Exception) { return null; }   // a missing icon must never stop a window from opening
    });

    /// <summary>Gives a window the app icon.</summary>
    public static void Brand(Window w)
    {
        if (Icon is { } icon) w.Icon = icon;
    }
}
