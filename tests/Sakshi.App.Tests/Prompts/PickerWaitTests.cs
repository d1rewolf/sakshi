using Avalonia.Threading;
using Sakshi.App.Prompts;

namespace Sakshi.App.Tests.Prompts;

public sealed class PickerWaitTests
{
    [Fact]
    public Task It_says_the_picker_is_coming_without_taking_the_keyboard_then_goes() => HeadlessHost.Run(() =>
    {
        var wait = new AvaloniaPickerWait();
        wait.Opening();
        Dispatcher.UIThread.RunJobs();
        var w = wait.Window!;
        Assert.True(w.IsVisible);
        Assert.False(w.ShowActivated);
        Assert.True(w.Topmost);
        Assert.Equal(PickerWaitWindow.OpeningHeading, w.Heading);
        Assert.Contains(PickerWaitWindow.OpeningHint, w.GetVisualTexts());

        wait.Opening();   // a second "on its way" keeps the one window
        wait.Slow();
        Dispatcher.UIThread.RunJobs();
        Assert.Same(w, wait.Window);
        Assert.Equal(PickerWaitWindow.SlowHeading, w.Heading);

        wait.Done();
        wait.Done();
        Dispatcher.UIThread.RunJobs();
        Assert.False(w.IsVisible);
        Assert.Null(wait.Window);
        return Task.CompletedTask;
    });
}
