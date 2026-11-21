using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Sakshi.App.Prompts;

/// <summary>A label and a text box, focused on open. Enter accepts, Esc or closing dismisses.</summary>
internal sealed class TextPromptWindow : Window
{
    private bool _accepted;

    public TextPromptWindow(string label)
    {
        PromptWindow.Configure(this);
        Input = new TextBox { FontSize = 16 };
        var root = new StackPanel { Margin = new Thickness(16), Spacing = 10 };
        root.Children.Add(new TextBlock { Text = label, FontSize = 14, Opacity = 0.8 });
        root.Children.Add(Input);
        Content = root;

        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        Opened += (_, _) => Input.Focus();
    }

    public TextBox Input { get; }

    /// <summary>The typed text, or null when dismissed. Read after the window closed.</summary>
    public string? Result => _accepted ? Input.Text ?? "" : null;

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { _accepted = true; e.Handled = true; Close(); }
        else if (e.Key == Key.Escape) { e.Handled = true; Close(); }
    }
}
