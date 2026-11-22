using Sakshi.App.Notifications;
using Sakshi.Core.Ports;

namespace Sakshi.App.Tests;

/// <summary>No test here shows a toast: only the text and the construction are checked.</summary>
public sealed class LabsNotifierTests
{
    private sealed class ListLog : ILog
    {
        public List<string> Lines { get; } = [];
        public void Info(string message) { lock (Lines) Lines.Add(message); }
    }

    [Fact]
    public void Toasts_come_from_the_id_velopack_gives_the_shortcuts() =>
        Assert.Equal("velopack.sakshi", LabsNotifier.AppUserModelId);

    [Fact]
    public void Toast_text_is_escaped_for_the_toast_xml()
    {
        // Labs puts the title and message straight into the toast XML; a meeting called "Q&A <1:1>" must not break it.
        var (title, message) = LabsNotifier.ToastText("Q&A <1:1>", "it's \"done\"");
        Assert.Equal("Q&amp;A &lt;1:1&gt;", title);
        Assert.Equal("it&apos;s &quot;done&quot;", message);
    }

    [Fact]
    public void Plain_text_is_unchanged() =>
        Assert.Equal(("Recording", "Zoom Meeting"), LabsNotifier.ToastText("Recording", "Zoom Meeting"));

    [Fact]
    public void Constructing_does_not_touch_the_UI()
    {
        var log = new ListLog();
        var n = new LabsNotifier(log);
        Assert.True(n.Drain().IsCompleted);
        Assert.Empty(log.Lines);
    }

    [Fact]
    public void The_toast_icon_is_the_png_beside_the_exe_when_shipped()
    {
        var dir = Path.Combine("x", "app");
        Assert.Equal(Path.Combine(dir, "sakshi.png"), LabsNotifier.IconPath(dir, _ => true));
        Assert.Null(LabsNotifier.IconPath(dir, _ => false));
    }
}
