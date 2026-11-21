using Sakshi.Core.Config;
using Sakshi.Core.Naming;
using Sakshi.Core.Ports;
using Sakshi.Core.State;

namespace Sakshi.Core.Tests;

public sealed class ImportTests : IDisposable
{
    private readonly RecorderFixture _f = new();
    private const string Mtime = "2026-01-05 10:00";
    private static readonly string Day = "2026-01-05-Mon";

    public ImportTests() => _f.Config = _f.Config with { TranscribeArgs = "-F changes" };

    public void Dispose() => _f.Dispose();

    private string Src(string file, string mtime = Mtime) => _f.MkVideo(Path.Combine(_f.Root, "src", file), mtime);

    private Task<int> Import(string path, string? name = null) =>
        _f.Recorder().ImportAsync(path, name, CancellationToken.None);

    private string MeetingFile(string day, string name, string ext = "mp4") => Path.Combine(_f.Meetings, day, name, name + "." + ext);

    [Fact]
    public async Task ImportWithName_MovesTheFileAndQueuesIt()
    {
        var src = Src("raw.mp4");

        Assert.Equal(0, await Import(src, "Weekly Sync"));

        Assert.True(File.Exists(MeetingFile(Day, "Weekly_Sync")));
        Assert.False(File.Exists(src));
        var job = Assert.Single(_f.Jobs);
        Assert.Equal(Path.Combine(_f.Meetings, Day, "Weekly_Sync"), job.Dir);
        Assert.Equal(MeetingFile(Day, "Weekly_Sync"), job.Video);
        Assert.Equal("Weekly_Sync", job.Name);
        Assert.Equal("-F changes", job.FrameArgs);
        Assert.Null(job.HandOff);
        Assert.Equal(CapturePhase.Idle, _f.Capture.Phase);
        Assert.Equal("", _f.Prompter.Sequence);
    }

    [Fact]
    public async Task FramePrompt_TheChosenModeBecomesTheArguments()
    {
        _f.Config = _f.Config with { FramesPrompt = true };
        _f.Prompter.Answer("None");

        await Import(Src("frames.mp4"), "framepick");

        Assert.Equal("Frames to keep|", _f.Prompter.Sequence);
        Assert.Equal("-F none", Assert.Single(_f.Jobs).FrameArgs);
    }

    [Fact]
    public async Task FramePrompt_RowsComeFromTheConfiguredModes()
    {
        _f.Config = _f.Config with
        {
            FramesPrompt = true,
            FramesModes = [new FrameMode("G", "Changes to tool", "-F changes", "Slides, then hand off", "handoff-tool requirements new")],
        };
        _f.Prompter.Answer("Changes to tool");

        await Import(Src("rows.mp4"), "rows");

        Assert.Equal([new PromptRow("G", "Changes to tool", "Slides, then hand off")], _f.Prompter.Rows[0]);
        var job = Assert.Single(_f.Jobs);
        Assert.Equal("-F changes", job.FrameArgs);
        Assert.Equal("handoff-tool requirements new", job.HandOff);
    }

    [Fact]
    public async Task FramePrompt_UnknownAnswerFallsBackToTranscribeArgs()
    {
        _f.Config = _f.Config with { FramesPrompt = true };
        _f.Prompter.Answer("Something Else");

        await Import(Src("frames2.mp4"), "framefallback");

        var job = Assert.Single(_f.Jobs);
        Assert.Equal("-F changes", job.FrameArgs);
        Assert.Null(job.HandOff);
    }

    [Fact]
    public async Task FramePrompt_DismissedFallsBackToTranscribeArgs()
    {
        _f.Config = _f.Config with { FramesPrompt = true };

        await Import(Src("frames5.mp4"), "framedismissed");

        Assert.Equal("-F changes", Assert.Single(_f.Jobs).FrameArgs);
    }

    [Fact]
    public async Task FramePrompt_SwitchedOffNeverAsks()
    {
        _f.Prompter.Answer("All");

        await Import(Src("frames3.mp4"), "frameoff");

        Assert.Equal("", _f.Prompter.Sequence);
        Assert.Equal("-F changes", Assert.Single(_f.Jobs).FrameArgs);
    }

    [Fact]
    public async Task FramePrompt_ARetryReusesTheAnswerAlreadyInTheState()
    {
        _f.Config = _f.Config with { FramesPrompt = true };
        var stopped = RecorderFixture.Local(2026, 2, 4, 9, 0);
        _f.Stranded(stopped, "resumed", new FrameChoice("-F all", null), "frames4.mp4");
        _f.Prompter.Answer("None");

        Assert.Equal(0, await _f.Recorder().StopAsync(null, CancellationToken.None));

        Assert.Equal("", _f.Prompter.Sequence);
        Assert.Equal("-F all", Assert.Single(_f.Jobs).FrameArgs);
        Assert.True(File.Exists(MeetingFile(DayFolder.For(stopped), "resumed")));
    }

    [Fact]
    public async Task NameFromThePrompt()
    {
        _f.Prompter.Answer("From Prompt");

        await Import(Src("a.mp4"));

        Assert.Equal("Meeting name|", _f.Prompter.Sequence);
        Assert.True(File.Exists(MeetingFile(Day, "From_Prompt")));
    }

    [Fact]
    public async Task DismissedPrompt_DefaultsToTheSanitizedFileName()
    {
        await Import(Src("2026-09-23 13-06-16.mp4"));

        Assert.Equal("Meeting name|Meeting name (Esc again keeps 2026-09-23_13-06-16)|", _f.Prompter.Sequence);
        Assert.True(File.Exists(MeetingFile(Day, "2026-09-23_13-06-16")));
    }

    [Fact]
    public async Task RelativePathWorks()
    {
        var src = Src("rel.mkv");
        var relative = Path.GetRelativePath(Environment.CurrentDirectory, src);

        Assert.Equal(0, await Import(relative, "relmeeting"));

        Assert.True(File.Exists(MeetingFile(Day, "relmeeting", "mkv")));
    }

    [Fact]
    public async Task ExistingFolderGetsASuffix()
    {
        await Import(Src("rel.mkv"), "relmeeting");

        await Import(Src("again.mp4"), "relmeeting");

        Assert.True(File.Exists(MeetingFile(Day, "relmeeting-2")));
        Assert.True(File.Exists(MeetingFile(Day, "relmeeting", "mkv")));
    }

    [Fact]
    public async Task DayFolderComesFromTheFilesMtime()
    {
        await Import(Src("old.mp4", "2025-12-31 23:59"), "oldone");

        Assert.True(File.Exists(MeetingFile("2025-12-31-Wed", "oldone")));
    }

    [Fact]
    public async Task MissingPath_IsRefused()
    {
        var missing = Path.Combine(_f.Root, "src", "nope.mp4");

        Assert.Equal(1, await Import(missing, "x"));

        Assert.Empty(_f.Files(_f.Meetings));
        Assert.Contains((Urgency.Critical, "Sakshi", $"No such file: {missing}"), _f.Notifier.Sent);
    }

    [Theory]
    [InlineData(CapturePhase.Recording, "A recording is in progress — stop it before importing.")]
    [InlineData(CapturePhase.Starting, "A recording is in progress — stop it before importing.")]
    [InlineData(CapturePhase.Naming, "An unnamed recording is waiting — run 'sakshi stop' to store it first.")]
    public async Task RefusedWhileACaptureIsActive(CapturePhase phase, string message)
    {
        var src = Src("busy.mp4");
        var other = _f.MkVideo(Path.Combine(_f.Rec, "waiting.mp4"));
        _f.Set(new Capture(phase, Started: _f.Clock.Now, Video: phase == CapturePhase.Naming ? other : null));

        Assert.Equal(1, await Import(src, "busy"));

        Assert.True(File.Exists(src));
        Assert.Equal(phase, _f.Capture.Phase);
        Assert.Contains((Urgency.Critical, "Sakshi", message), _f.Notifier.Sent);
        Assert.Empty(_f.Files(_f.Meetings));
    }

    [Fact]
    public async Task Import_WhileTranscribing_IsAllowedAndQueued()
    {
        var running = new TranscriptionJob(Guid.NewGuid(), Path.Combine(_f.Meetings, Day, "first"), "first.mp4", "first", "-F none", null, Pid: 4242, Started: _f.Clock.Now);
        _f.Store.Update(s => s with { Jobs = s.Jobs.Add(running) });

        Assert.Equal(0, await Import(Src("second.mp4"), "second"));

        Assert.Equal(["first", "second"], _f.Jobs.Select(j => j.Name));
        Assert.True(File.Exists(MeetingFile(Day, "second")));
    }
}
