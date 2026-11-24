using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Sakshi.App.Tests.Prompts;
using Sakshi.App.Tray;

namespace Sakshi.App.Tests;

public sealed class AboutWindowTests
{
    [Theory]
    [InlineData("0.1.2+17a4472e9b1c", "0.1.2 (17a4472)")]
    [InlineData("0.1.2", "0.1.2")]
    [InlineData("0.1.2+dirty", "0.1.2")]
    public void The_version_names_the_commit_when_the_build_says(string informational, string shown) =>
        Assert.Equal(shown, AppVersion.Describe(informational));

    [Fact]
    public Task It_shows_the_version_and_opens_the_repo_the_guide_and_the_log_folder() => HeadlessHost.Run(() =>
    {
        var opened = new List<string>();
        var w = new AboutWindow("0.1.2 (17a4472)", opened.Add, () => opened.Add("logs"));
        w.Show();
        Assert.Equal("Sakshi 0.1.2 (17a4472)", w.Heading);
        var texts = w.GetVisualTexts();
        Assert.Contains(AboutWindow.Tagline, texts);
        Assert.Contains(AboutWindow.Credits, texts);

        foreach (var b in Buttons(w).Where(b => (string?)b.Content == "Open"))
            b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal([AboutWindow.Repo, AboutWindow.Guide, "logs"], opened);

        w.KeyPress(Avalonia.Input.Key.Escape, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.None, null);
        Assert.False(w.IsVisible);
        return Task.CompletedTask;
    });

    private static IEnumerable<Button> Buttons(Control c) =>
        (c is Button b ? [b] : Enumerable.Empty<Button>()).Concat(c switch
        {
            Panel p => p.Children.SelectMany(Buttons),
            ContentControl { Content: Control inner } => Buttons(inner),
            Decorator { Child: { } child } => Buttons(child),
            _ => Enumerable.Empty<Button>(),
        });
}
