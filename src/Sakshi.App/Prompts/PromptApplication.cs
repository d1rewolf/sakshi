using Avalonia;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace Sakshi.App.Prompts;

/// <summary>The Avalonia application behind the prompt windows: Fluent theme, dark variant, no main window.</summary>
public class PromptApplication : Application
{
    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Dark;
        Styles.Add(new FluentTheme());
    }
}
