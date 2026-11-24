using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Sakshi.App.Prompts;

namespace Sakshi.App.Tray;

/// <summary>
/// "About Sakshi": which Sakshi this is (version and commit), where it lives, and the log folder, which is what a
/// bug report needs. Built in code like the other windows.
/// </summary>
internal sealed class AboutWindow : Window
{
    internal const string Repo = "https://github.com/d1rewolf/sakshi";
    internal const string Guide = Repo + "/blob/main/docs/user-guide.md";
    internal const string Tagline = "Records a meeting window with OBS and turns it into meeting.md.";
    internal const string Credits = "MIT License. Records with OBS Studio, transcribes with whisper.cpp, and ships FFmpeg (LGPL).";

    private readonly Button _close = new() { Content = "Close" };

    public AboutWindow(string version, Action<string> openUrl, Action openLogFolder)
    {
        Title = "About Sakshi";
        Width = 480;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Witness.Brand(this);
        Heading = $"Sakshi {version}";

        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        if (Logo() is { } logo) head.Children.Add(new Image { Source = logo, Width = 56, Height = 56, Margin = new Thickness(0, 0, 16, 0), VerticalAlignment = VerticalAlignment.Top });
        var name = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        name.Children.Add(new TextBlock { Text = Heading, FontSize = 20, FontWeight = FontWeight.SemiBold });
        name.Children.Add(PromptWindow.Hint(Tagline));
        Grid.SetColumn(name, 1);
        head.Children.Add(name);

        var links = new StackPanel { Spacing = 8 };
        links.Children.Add(Link("github.com/d1rewolf/sakshi", () => openUrl(Repo)));
        links.Children.Add(Link("User guide", () => openUrl(Guide)));
        links.Children.Add(Link("Sakshi's log folder", openLogFolder));

        _close.Classes.Add("accent");
        _close.Click += (_, _) => Close();
        KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Escape) { e.Handled = true; Close(); } };
        var foot = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        foot.Children.Add(new TextBlock
        {
            Text = Credits, FontSize = 12, Foreground = Witness.Brush(Witness.Muted), TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0),
        });
        Grid.SetColumn(_close, 1);
        foot.Children.Add(_close);

        var root = new StackPanel { Margin = new Thickness(24, 20, 24, 20), Spacing = 18 };
        root.Children.Add(head);
        root.Children.Add(links);
        root.Children.Add(foot);
        Content = root;
    }

    /// <summary>"Sakshi 0.1.2 (17a4472)" (for tests).</summary>
    internal string Heading { get; }

    internal Button CloseButton => _close;

    /// <summary>A row: what it is, and an Open button.</summary>
    private static Control Link(string label, Action open)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Tag = label };
        row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, FontSize = 14 });
        var button = new Button { Content = "Open" };
        button.Click += (_, _) => open();
        Grid.SetColumn(button, 1);
        row.Children.Add(button);
        return row;
    }

    private static Bitmap? Logo()
    {
        try
        {
            using var png = typeof(AboutWindow).Assembly.GetManifestResourceStream("sakshi.png");
            return png is null ? null : new Bitmap(png);
        }
        catch (Exception) { return null; }   // the window opens without its picture
    }
}
