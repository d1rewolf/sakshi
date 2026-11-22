using Sakshi.App.Tests.Prompts;
using Sakshi.App.Tray;

namespace Sakshi.App.Tests;

public sealed class LiveTranscriptTests
{
    private readonly List<string> _log = [];
    private LiveTranscript Live() => new(l => { lock (_log) _log.Add(l); });

    [Fact]
    public void Lines_and_progress_arrive_in_order()
    {
        var live = Live();
        var changes = 0;
        live.Changed += () => changes++;
        live.Accept("""{"start":3.5,"end":6.0,"text":"Hello everyone."}""");
        live.Accept("""{"start":61.2,"end":64.0,"text":"Next item."}""");
        live.Accept("""{"readTo":64.8}""");

        var v = live.Snapshot();
        Assert.Equal([new LiveLine(TimeSpan.FromSeconds(3.5), "Hello everyone."), new LiveLine(TimeSpan.FromSeconds(61.2), "Next item.")], v.Lines);
        Assert.Equal(LiveState.Running, v.State);
        Assert.Equal("Transcribed up to 01:04.", v.Status);
        Assert.Equal(3, changes);
    }

    [Fact]
    public void Before_the_first_line_it_says_it_is_starting_or_downloading()
    {
        var live = Live();
        Assert.Equal(LiveTranscript.StartingText, live.Snapshot().Status);
        live.Accept("""{"status":"downloading-model"}""");
        Assert.Equal(LiveTranscript.DownloadingText, live.Snapshot().Status);
    }

    [Theory]
    [InlineData("unreadable", LiveTranscript.UnreadableText)]
    [InlineData("no-model", LiveTranscript.NoModelText)]
    [InlineData("something new", LiveTranscript.CrashedText)]
    public void Errors_become_a_message_that_says_what_to_do(string error, string message)
    {
        var live = Live();
        live.Accept($$"""{"error":"{{error}}"}""");
        live.Exited(3);
        Assert.Equal(LiveState.Failed, live.Snapshot().State);
        Assert.Equal(message, live.Snapshot().Status);
    }

    [Fact]
    public void A_clean_exit_says_where_the_full_transcript_will_be()
    {
        var live = Live();
        live.Accept("""{"start":1,"end":2,"text":"Bye."}""");
        live.Exited(0);
        Assert.Equal(LiveTranscript.EndedText, live.Snapshot().Status);
        Assert.Single(live.Snapshot().Lines);
    }

    [Fact]
    public void A_crash_without_a_message_still_explains()
    {
        var live = Live();
        live.Exited(134);
        Assert.Equal(LiveTranscript.CrashedText, live.Snapshot().Status);
    }

    [Fact]
    public void Lines_that_are_not_the_childs_json_are_ignored()
    {
        var live = Live();
        live.Accept("==> something else");
        live.Accept("""{"other":1}""");
        Assert.Empty(live.Snapshot().Lines);
        Assert.Contains(_log, l => l.StartsWith("live transcript: ignored a line"));
    }

    [Theory]
    [InlineData(65, "01:05")]
    [InlineData(3725, "1:02:05")]
    public void Times_read_like_a_clock(int seconds, string text) =>
        Assert.Equal(text, LiveTranscript.Clock(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public Task The_window_shows_lines_as_they_arrive() => HeadlessHost.Run(async () =>
    {
        var live = Live();
        live.Accept("""{"start":3.5,"end":6.0,"text":"Hello everyone."}""");
        var w = new LiveTranscriptWindow(live, _ => { });
        Assert.Equal(["[00:03]  Hello everyone."], w.ShownLines);

        live.Accept("""{"start":7,"end":9,"text":"Shall we start?"}""");
        await Task.Delay(50);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.Equal(["[00:03]  Hello everyone.", "[00:07]  Shall we start?"], w.ShownLines);
        Assert.Equal("Transcribed up to 00:00.", w.Status);

        live.Exited(0);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.Equal(LiveTranscript.EndedText, w.Status);
    });

    [Fact]
    public Task Before_anyone_speaks_the_window_says_so_and_has_nothing_to_copy() => HeadlessHost.Run(() =>
    {
        var live = Live();
        var w = new LiveTranscriptWindow(live, _ => { });
        Assert.True(w.ShowsNothingYet);
        Assert.False(w.CopyButton.IsEnabled);

        live.Accept("""{"start":1,"end":2,"text":" Hello."}""");
        w.Render();
        Assert.False(w.ShowsNothingYet);
        Assert.True(w.CopyButton.IsEnabled);
        Assert.Equal(["[00:01]  Hello."], w.ShownLines);   // whisper's leading space is dropped
        return Task.CompletedTask;
    });
}
