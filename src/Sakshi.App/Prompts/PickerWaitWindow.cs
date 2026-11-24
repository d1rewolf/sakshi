using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Sakshi.App.Prompts;

/// <summary>
/// "Opening the window picker…": a small notice while the system's picker takes its time. Nothing to click and it
/// never takes the keyboard (the picker must get it), so it cannot be answered by mistake.
/// </summary>
internal sealed class PickerWaitWindow : Window
{
    internal const string OpeningHeading = "Opening the window picker…";
    internal const string OpeningHint = "It takes a few seconds. Choose the meeting window when it opens.";
    internal const string SlowHeading = "The window picker is slow to open…";
    internal const string SlowHint = "Still waiting for it. If nothing comes, stop and start the recording again.";

    private readonly TextBlock _heading = PromptWindow.Heading(OpeningHeading);
    private readonly TextBlock _hint = PromptWindow.Hint(OpeningHint);

    public PickerWaitWindow()
    {
        PromptWindow.Configure(this);
        Width = 420;
        ShowActivated = false;
        Focusable = false;
        var bar = new ProgressBar
        {
            IsIndeterminate = true, Height = 3, MinHeight = 3, Margin = new Thickness(0, 12, 0, 0),
            Foreground = Witness.Brush(Witness.Amber), Background = Witness.Brush(Witness.Line),
        };
        var stack = new StackPanel { Margin = new Thickness(18, 16, 18, 16), Spacing = 6 };
        stack.Children.Add(_heading);
        stack.Children.Add(_hint);
        stack.Children.Add(bar);
        Content = stack;
    }

    internal string Heading => _heading.Text ?? "";

    internal void ShowSlow()
    {
        _heading.Text = SlowHeading;
        _hint.Text = SlowHint;
    }
}
