using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;

namespace Sakshi.App.Prompts;

/// <summary>A label and a text box, focused on open. Enter or OK accepts; Esc or closing dismisses.</summary>
internal sealed class TextPromptWindow : Window
{
    internal const string HintText = "Press Enter when done.";
    private bool _accepted;

    public TextPromptWindow(string label)
    {
        PromptWindow.Configure(this);
        Input = new TextBox { FontSize = 15, Padding = new Thickness(10, 8) };
        Ok = new Button { Content = "OK", Classes = { "accent" }, MinWidth = 72, HorizontalContentAlignment = HorizontalAlignment.Center };
        Ok.Click += (_, _) => Accept();

        var foot = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var hint = PromptWindow.Hint(HintText);
        hint.VerticalAlignment = VerticalAlignment.Center;
        foot.Children.Add(hint);
        Grid.SetColumn(Ok, 1);
        foot.Children.Add(Ok);

        var root = new StackPanel { Margin = new Thickness(18, 16, 18, 16), Spacing = 12 };
        root.Children.Add(PromptWindow.Heading(label));
        root.Children.Add(Input);
        root.Children.Add(foot);
        Content = root;

        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        Opened += (_, _) => Input.Focus();
    }

    public TextBox Input { get; }

    internal Button Ok { get; }

    /// <summary>The typed text, or null when dismissed. Read after the window closed.</summary>
    public string? Result => _accepted ? Input.Text ?? "" : null;

    private void Accept()
    {
        _accepted = true;
        Close();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; Accept(); }
        else if (e.Key == Key.Escape) { e.Handled = true; Close(); }
    }
}
