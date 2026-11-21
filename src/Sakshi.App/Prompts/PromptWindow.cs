using Avalonia.Controls;

namespace Sakshi.App.Prompts;

internal static class PromptWindow
{
    /// <summary>Title "sakshi" is the Hyprland window-rule hook; 560 matches bash's --width 560.</summary>
    public static void Configure(Window w)
    {
        w.Title = "sakshi";
        w.Width = 560;
        w.SizeToContent = SizeToContent.Height;
        w.CanResize = false;
        w.Topmost = true;
        w.ShowInTaskbar = false;
        w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
    }
}
