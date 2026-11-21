using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Sakshi.App.Prompts;
using Sakshi.Core.Ports;

namespace Sakshi.App.Tests.Prompts;

public class AvaloniaPrompterTests
{
    private static readonly TimeSpan Long = TimeSpan.FromMinutes(5);

    private static readonly IReadOnlyList<PromptRow> Rows =
    [
        new("S", "Save it", "Name it and transcribe"),
        new("D", "Don't save", "Throw this recording away"),
        new("X", "Third", "Another row"),
    ];

    private static async Task<T> Next<T>(AvaloniaPrompter p) where T : Window
    {
        for (var i = 0; i < 500; i++)
        {
            if (p.OpenWindow is T w && w.IsVisible) return w;
            await Task.Delay(10);
        }
        throw new TimeoutException("window never opened");
    }

    private static async Task<string?> Await(Task<string?> t)
    {
        for (var i = 0; i < 500 && !t.IsCompleted; i++)
        {
            await Task.Delay(10);
        }
        return await t;
    }

    private static void Press(Window w, Key k) =>
        w.KeyPress(k, RawInputModifiers.None, PhysicalKey.None, null);

    [Fact]
    public Task Rows_render_glyph_label_and_description() => HeadlessHost.Run(async () =>
    {
        var p = new AvaloniaPrompter();
        var task = p.ChooseAsync("Recording stopped", Rows, Long, CancellationToken.None);
        var w = await Next<ChooserWindow>(p);
        Assert.Equal("sakshi", w.Title);
        Assert.Equal(560, w.Width);
        Assert.True(w.Topmost);
        Assert.False(w.CanResize);
        Assert.False(w.ShowInTaskbar);
        Assert.Equal(SizeToContent.Height, w.SizeToContent);
        var texts = w.GetVisualTexts();
        Assert.Contains("Recording stopped", texts);
        foreach (var r in Rows)
        {
            Assert.Contains(r.Glyph, texts);
            Assert.Contains(r.Label, texts);
            Assert.Contains(r.Description, texts);
        }
        Press(w, Key.Escape);
        Assert.Null(await Await(task));
    });

    [Fact]
    public Task Empty_rows_return_null_without_a_window() => HeadlessHost.Run(async () =>
    {
        var p = new AvaloniaPrompter();
        Window? w = null;
        p.Opened += win => w = win;
        Assert.Null(await p.ChooseAsync("t", [], Long, CancellationToken.None));
        Assert.Null(w);
    });

    [Fact]
    public Task Glyph_uses_the_bundled_font_with_a_default_fallback_and_empty_description_is_hidden() => HeadlessHost.Run(async () =>
    {
        var p = new AvaloniaPrompter();
        var task = p.ChooseAsync("t", [new("G", "Label", ""), new("H", "Other", "desc")], Long, CancellationToken.None);
        var w = await Next<ChooserWindow>(p);
        var glyph = w.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == "G");
        Assert.Equal(["Symbols Nerd Font Mono", "$Default"], glyph.FontFamily.FamilyNames.ToArray());
        var blocks = w.GetVisualDescendants().OfType<TextBlock>().ToList();
        Assert.All(blocks.Where(t => t.Text == ""), t => Assert.False(t.IsVisible));
        Assert.True(blocks.First(t => t.Text == "desc").IsVisible);
        Press(w, Key.Escape);
        await Await(task);
    });

    [Fact]
    public Task Long_lists_scroll() => HeadlessHost.Run(async () =>
    {
        var rows = Enumerable.Range(0, 40).Select(i => new PromptRow("W", $"Window {i}", $"desc {i}")).ToList();
        var p = new AvaloniaPrompter();
        var task = p.ChooseAsync("t", rows, Long, CancellationToken.None);
        var w = await Next<ChooserWindow>(p);
        Assert.InRange(w.Bounds.Height, 1, 600);
        // Not the window template's own scroller: the rows must sit in one of ours.
        var scroll = ((Visual)w.Content!).GetVisualDescendants().OfType<ScrollViewer>().Single();
        for (var i = 0; i < 39; i++) { Press(w, Key.Down); }
        Dispatcher.UIThread.RunJobs();
        var last = w.RowControl(39);
        var top = last.TranslatePoint(new Point(0, 0), scroll)!.Value.Y;
        Assert.True(top >= 0 && top + last.Bounds.Height <= scroll.Viewport.Height + 0.5,
            $"last row at {top}, viewport {scroll.Viewport.Height}");
        Press(w, Key.Escape);
        await Await(task);
    });

    [Fact]
    public Task Glyph_font_is_bundled() => HeadlessHost.Run(async () =>
    {
        // The headless font manager is a stub that cannot load font files, so check the pieces a real
        // one needs: the ttf is embedded in the assembly and the glyphs point at it by family name.
        Assert.True(AssetLoader.Exists(new Uri("avares://sakshi/Assets/SymbolsNerdFontMono-Regular.ttf")));
        var p = new AvaloniaPrompter();
        var task = p.ChooseAsync("t", [new("G", "Label", "")], Long, CancellationToken.None);
        var w = await Next<ChooserWindow>(p);
        var glyph = w.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == "G");
        Assert.Equal("avares://sakshi/Assets#Symbols Nerd Font Mono", glyph.FontFamily.ToString()!.Split(',')[0].Replace("compositefont:", ""));
        Press(w, Key.Escape);
        await Await(task);
    });

    [Fact]
    public Task Enter_returns_first_label_by_default() => HeadlessHost.Run(async () =>
    {
        var p = new AvaloniaPrompter();
        var task = p.ChooseAsync("t", Rows, Long, CancellationToken.None);
        var w = await Next<ChooserWindow>(p);
        Press(w, Key.Enter);
        Assert.Equal("Save it", await Await(task));
    });

    [Fact]
    public Task Down_then_Enter_returns_second_label_and_Up_goes_back() => HeadlessHost.Run(async () =>
    {
        var p = new AvaloniaPrompter();
        var task = p.ChooseAsync("t", Rows, Long, CancellationToken.None);
        var w = await Next<ChooserWindow>(p);
        Press(w, Key.Down);
        Press(w, Key.Down);
        Press(w, Key.Up);
        Press(w, Key.Enter);
        Assert.Equal("Don't save", await Await(task));
    });

    [Fact]
    public Task Escape_returns_null_and_closes() => HeadlessHost.Run(async () =>
    {
        var p = new AvaloniaPrompter();
        var task = p.ChooseAsync("t", Rows, Long, CancellationToken.None);
        var w = await Next<ChooserWindow>(p);
        Press(w, Key.Escape);
        Assert.Null(await Await(task));
        Assert.False(w.IsVisible);
    });

    [Fact]
    public Task Closing_the_window_returns_null() => HeadlessHost.Run(async () =>
    {
        var p = new AvaloniaPrompter();
        var task = p.ChooseAsync("t", Rows, Long, CancellationToken.None);
        var w = await Next<ChooserWindow>(p);
        w.Close();
        Assert.Null(await Await(task));
    });

    [Fact]
    public Task Clicking_a_row_picks_it() => HeadlessHost.Run(async () =>
    {
        var p = new AvaloniaPrompter();
        var task = p.ChooseAsync("t", Rows, Long, CancellationToken.None);
        var w = await Next<ChooserWindow>(p);
        var row = w.RowControl(2);
        var centre = row.TranslatePoint(new Point(row.Bounds.Width / 2, row.Bounds.Height / 2), w)!.Value;
        // The press alone picks the row and closes the window, so there is no release to send.
        w.MouseDown(centre, MouseButton.Left);
        Assert.Equal("Third", await Await(task));
    });

    [Fact]
    public Task Timeout_closes_the_window_and_returns_null() => HeadlessHost.Run(async () =>
    {
        var p = new AvaloniaPrompter();
        Window? w = null;
        p.Opened += win => w = win;
        var task = p.ChooseAsync("t", Rows, TimeSpan.FromMilliseconds(150), CancellationToken.None);
        Assert.Null(await Await(task));
        Assert.NotNull(w);
        Assert.False(w!.IsVisible);
    });

    [Fact]
    public Task Caller_cancellation_closes_the_window_and_throws_OperationCanceled() => HeadlessHost.Run(async () =>
    {
        var p = new AvaloniaPrompter();
        using var cts = new CancellationTokenSource();
        var task = p.ChooseAsync("t", Rows, Long, cts.Token);
        var w = await Next<ChooserWindow>(p);
        cts.Cancel();
        for (var i = 0; i < 500 && !task.IsCompleted; i++) { await Task.Delay(10); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.False(w.IsVisible);
    });

    [Fact]
    public Task Text_prompt_returns_typed_text_on_Enter() => HeadlessHost.Run(async () =>
    {
        var p = new AvaloniaPrompter();
        var task = p.AskTextAsync("Meeting name", Long, CancellationToken.None);
        var w = await Next<TextPromptWindow>(p);
        Assert.Equal("sakshi", w.Title);
        Assert.Equal(560, w.Width);
        Assert.Contains("Meeting name", w.GetVisualTexts());
        Assert.True(w.Input.IsFocused);
        w.KeyTextInput("Weekly sync");
        Press(w, Key.Enter);
        Assert.Equal("Weekly sync", await Await(task));
    });

    [Fact]
    public Task Text_prompt_Escape_returns_null() => HeadlessHost.Run(async () =>
    {
        var p = new AvaloniaPrompter();
        var task = p.AskTextAsync("Meeting name", Long, CancellationToken.None);
        var w = await Next<TextPromptWindow>(p);
        w.KeyTextInput("ignored");
        Press(w, Key.Escape);
        Assert.Null(await Await(task));
    });

    [Fact]
    public Task Text_prompt_close_and_timeout_return_null() => HeadlessHost.Run(async () =>
    {
        var p = new AvaloniaPrompter();
        var t1 = p.AskTextAsync("a", Long, CancellationToken.None);
        (await Next<TextPromptWindow>(p)).Close();
        Assert.Null(await Await(t1));

        Window? w = null;
        p.Opened += win => w = win;
        var t2 = p.AskTextAsync("b", TimeSpan.FromMilliseconds(150), CancellationToken.None);
        Assert.Null(await Await(t2));
        Assert.NotNull(w);
        Assert.False(w!.IsVisible);
    });

    [Fact]
    public Task Prompts_queue_one_window_at_a_time() => HeadlessHost.Run(async () =>
    {
        var p = new AvaloniaPrompter();
        var first = p.ChooseAsync("one", Rows, Long, CancellationToken.None);
        var w1 = await Next<ChooserWindow>(p);
        var second = p.AskTextAsync("two", Long, CancellationToken.None);
        for (var i = 0; i < 20; i++) { await Task.Delay(10); }
        Assert.Same(w1, p.OpenWindow);
        Press(w1, Key.Enter);
        Assert.Equal("Save it", await Await(first));
        var w2 = await Next<TextPromptWindow>(p);
        Press(w2, Key.Escape);
        Assert.Null(await Await(second));
    });

    [Fact]
    public async Task Choice_model_clamps_and_dismisses()
    {
        var m = new ChooserModel(Rows);
        m.Handle(Key.Up);
        Assert.Equal(0, m.Index);
        m.Handle(Key.Down); m.Handle(Key.Down); m.Handle(Key.Down);
        Assert.Equal(2, m.Index);
        Assert.False(m.Handle(Key.A));
        Assert.True(m.Handle(Key.Escape));
        Assert.True(m.Done);
        Assert.Null(m.Result);
        await Task.CompletedTask;
    }
}

internal static class WindowTestExtensions
{
    public static List<string> GetVisualTexts(this Window w) =>
        w.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").ToList();
}
