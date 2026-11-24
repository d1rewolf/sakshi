using Avalonia.Controls;
using Avalonia.Headless;
using Sakshi.App.Tests.Prompts;
using Sakshi.App.Tray;
using Sakshi.Platform.Doctor;

namespace Sakshi.App.Tests;

public sealed class SetupWindowTests
{
    private static readonly DoctorCheck Obs = new("OBS installed", false, "OBS is not installed", "brew install --cask obs");
    private static readonly DoctorCheck Ffmpeg = new("ffmpeg", true, "/opt/homebrew/bin/ffmpeg", null);
    private static readonly DoctorCheck Permissions = new("Permissions", true, "OBS needs Screen Recording", null, "x-apple.systempreferences:x");

    private static StackPanel RowFor(SetupWindow w, string name) => (StackPanel)w.Rows.Single(r => (string?)r.Tag == name);

    private static IEnumerable<T> All<T>(Control c) where T : Control =>
        (c is T t ? new[] { t } : Enumerable.Empty<T>()).Concat(c switch
        {
            Panel p => p.Children.SelectMany(All<T>),
            ContentControl { Content: Control inner } => All<T>(inner),
            Decorator { Child: { } child } => All<T>(child),
            _ => Enumerable.Empty<T>(),
        });

    [Fact]
    public Task A_failed_check_shows_its_command_with_a_copy_button() => HeadlessHost.Run(() =>
    {
        var w = new SetupWindow([Obs, Ffmpeg], () => Task.FromResult<IReadOnlyList<DoctorCheck>>([]), _ => { }, _ => { });
        Assert.StartsWith("1 thing needs fixing before Sakshi can record.", w.Summary);
        var obs = RowFor(w, "OBS installed");
        Assert.Equal("brew install --cask obs", Assert.Single(All<TextBox>(obs)).Text);
        Assert.Contains(All<Button>(obs), b => (string?)b.Content == "Copy");
        Assert.Empty(All<TextBox>(RowFor(w, "ffmpeg")));
        return Task.CompletedTask;
    });

    [Fact]
    public Task Escape_closes_it() => HeadlessHost.Run(() =>
    {
        var w = new SetupWindow([Ffmpeg], () => Task.FromResult<IReadOnlyList<DoctorCheck>>([]), _ => { }, _ => { });
        w.Show();
        w.KeyPress(Avalonia.Input.Key.Escape, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.None, null);
        Assert.False(w.IsVisible);
        return Task.CompletedTask;
    });

    [Fact]
    public Task Open_settings_opens_the_checks_link() => HeadlessHost.Run(() =>
    {
        string? opened = null;
        var w = new SetupWindow([Permissions], () => Task.FromResult<IReadOnlyList<DoctorCheck>>([]), u => opened = u, _ => { });
        var button = All<Button>(RowFor(w, "Permissions")).Single(b => (string?)b.Content == "Open settings");
        button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.Equal("x-apple.systempreferences:x", opened);
        Assert.Equal(SetupWindow.AllSet, w.Summary);
        return Task.CompletedTask;
    });

    [Fact]
    public Task Optional_advice_alone_does_not_say_sakshi_cannot_record() => HeadlessHost.Run(() =>
    {
        var picker = new DoctorCheck("Share picker", false, "optional: …", "mkdir -p x", Required: false);
        var w = new SetupWindow([Ffmpeg, picker], () => Task.FromResult<IReadOnlyList<DoctorCheck>>([]), _ => { }, _ => { });
        Assert.StartsWith(SetupWindow.OnlyOptional, w.Summary);
        Assert.Equal("mkdir -p x", Assert.Single(All<TextBox>(RowFor(w, "Share picker"))).Text);
        return Task.CompletedTask;
    });

    [Fact]
    public Task Check_again_shows_the_new_results() => HeadlessHost.Run(async () =>
    {
        var fixedObs = Obs with { Ok = true, Detail = "/usr/bin/obs" };
        var w = new SetupWindow([Obs], () => Task.FromResult<IReadOnlyList<DoctorCheck>>([fixedObs]), _ => { }, _ => { });
        await w.CheckAgainAsync();
        Assert.Equal(SetupWindow.AllSet, w.Summary);
        Assert.Empty(All<TextBox>(RowFor(w, "OBS installed")));
        Assert.True(w.CheckAgainButton.IsEnabled);
        Assert.Equal("Check again", w.CheckAgainButton.Content);
    });

    [Fact]
    public Task The_main_button_is_Check_again_until_everything_is_in_place() => HeadlessHost.Run(async () =>
    {
        var fixedObs = Obs with { Ok = true, Detail = "/usr/bin/obs" };
        var w = new SetupWindow([Obs], () => Task.FromResult<IReadOnlyList<DoctorCheck>>([fixedObs]), _ => { }, _ => { });
        Assert.Contains("accent", w.CheckAgainButton.Classes);
        Assert.DoesNotContain("accent", w.CloseButton.Classes);
        await w.CheckAgainAsync();
        Assert.DoesNotContain("accent", w.CheckAgainButton.Classes);
        Assert.Contains("accent", w.CloseButton.Classes);
    });

    [Fact]
    public void The_fix_command_names_this_systems_terminal() =>
        Assert.Contains(OperatingSystem.IsWindows() ? "PowerShell" : OperatingSystem.IsMacOS() ? "in Terminal" : "a terminal", SetupWindow.HowTo);
}
