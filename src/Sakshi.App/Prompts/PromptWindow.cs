using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Sakshi.App.Prompts;

internal static class PromptWindow
{
    /// <summary>
    /// On Linux the title "sakshi" is what a Hyprland window rule matches, so it stays; elsewhere it is only seen in
    /// the title bar, where the product's name is "Sakshi".
    /// </summary>
    public static string Title => OperatingSystem.IsLinux() ? "sakshi" : "Sakshi";

    /// <summary>560 matches bash's --width 560.</summary>
    public static void Configure(Window w)
    {
        w.Title = Title;
        w.Width = 560;
        w.SizeToContent = SizeToContent.Height;
        w.CanResize = false;
        w.Topmost = true;
        w.ShowInTaskbar = false;
        w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Witness.Brand(w);
    }

    /// <summary>The question at the top of a prompt.</summary>
    public static TextBlock Heading(string text) => new()
    {
        Text = text, FontSize = 15, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap,
    };

    /// <summary>The small line under a prompt that says how to answer it.</summary>
    public static TextBlock Hint(string text) => new()
    {
        Text = text, FontSize = 12, Foreground = Witness.Brush(Witness.Muted), TextWrapping = TextWrapping.Wrap,
    };
}
