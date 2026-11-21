using System.Collections.Immutable;
using Sakshi.Core.State;

namespace Sakshi.Core.Tests;

public sealed class StatusTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 19, 10, 0, 0, TimeSpan.Zero);

    private static SakshiState WithCapture(Capture c) => SakshiState.Empty with { Capture = c };

    private static TranscriptionJob Job(string name, string? handOff, DateTimeOffset? started) =>
        new(Guid.NewGuid(), "/d", "/d/v.mkv", name, "", handOff, null, started);

    [Fact]
    public void Status_Idle()
    {
        var s = StatusReport.Compute(SakshiState.Empty, Now, false);
        Assert.Equal(new Status("idle", "Record a meeting (OBS)", ""), s);
        Assert.Equal("{\"state\":\"idle\",\"tooltip\":\"Record a meeting (OBS)\",\"elapsed\":\"\"}", s.ToJson());
    }

    [Fact]
    public void Status_Starting()
    {
        var s = StatusReport.Compute(WithCapture(new Capture(CapturePhase.Starting)), Now, false);
        Assert.Equal(new Status("starting", "Pick the window to record…", ""), s);
    }

    [Fact]
    public void Status_Recording_ShowsElapsed()
    {
        var s = StatusReport.Compute(WithCapture(new Capture(CapturePhase.Recording, Now.AddSeconds(-125))), Now, false);
        Assert.Equal(new Status("recording", "Recording meeting (2:05) — click to stop", "2:05"), s);
    }

    [Fact]
    public void Status_Recording_OverTenMinutes()
    {
        var s = StatusReport.Compute(WithCapture(new Capture(CapturePhase.Recording, Now.AddSeconds(-3601))), Now, false);
        Assert.Equal("60:01", s.Elapsed);
    }

    [Fact]
    public void Status_NamingTooltipDependsOnLock()
    {
        var state = WithCapture(new Capture(CapturePhase.Naming, Now.AddSeconds(-5)));
        var held = StatusReport.Compute(state, Now, true);
        var free = StatusReport.Compute(state, Now, false);
        Assert.Equal("naming", held.State);
        Assert.Equal("Waiting for the meeting name…", held.Tooltip);
        Assert.Equal("Unnamed recording waiting — click to name and store it", free.Tooltip);
        Assert.Equal("0:05", free.Elapsed);
    }

    [Fact]
    public void Status_Transcribing()
    {
        var state = SakshiState.Empty with { Jobs = [Job("x", null, Now.AddSeconds(-5))] };
        Assert.Equal(new Status("transcribing", "Transcribing x (0:05)", "0:05"), StatusReport.Compute(state, Now, false));
    }

    [Fact]
    public void Status_TranscribingNamesHandOff()
    {
        var state = SakshiState.Empty with { Jobs = [Job("x", "file-meeting", Now.AddSeconds(-5))] };
        Assert.Equal("Transcribing x (0:05), then handing it to file-meeting",
            StatusReport.Compute(state, Now, false).Tooltip);
    }

    [Fact]
    public void Status_TranscribingShowsQueued()
    {
        var state = SakshiState.Empty with
        {
            Jobs = ImmutableList.Create(Job("x", null, Now.AddSeconds(-5)), Job("y", null, null), Job("z", null, null)),
        };
        Assert.Equal("Transcribing x (0:05) (+2 queued)", StatusReport.Compute(state, Now, false).Tooltip);
    }

    [Fact]
    public void Status_TranscribingWithoutStart_HasEmptyElapsed()
    {
        var state = SakshiState.Empty with { Jobs = [Job("x", null, null)] };
        Assert.Equal("", StatusReport.Compute(state, Now, false).Elapsed);
    }

    [Fact]
    public void Status_RecordingWinsOverTranscribing()
    {
        var state = new SakshiState(new Capture(CapturePhase.Recording, Now.AddSeconds(-10)),
            [Job("x", null, Now.AddSeconds(-5))]);
        Assert.Equal("recording", StatusReport.Compute(state, Now, false).State);
    }

    [Fact]
    public void Status_NamingWinsOverTranscribing()
    {
        var state = new SakshiState(new Capture(CapturePhase.Naming), [Job("x", null, Now)]);
        Assert.Equal("naming", StatusReport.Compute(state, Now, false).State);
    }

    [Fact]
    public void Status_NamingJson_KeepsNonAsciiRaw()
    {
        var state = WithCapture(new Capture(CapturePhase.Naming));
        Assert.Equal(
            "{\"state\":\"naming\",\"tooltip\":\"Unnamed recording waiting — click to name and store it\",\"elapsed\":\"\"}",
            StatusReport.Compute(state, Now, false).ToJson());
        Assert.Equal("{\"state\":\"starting\",\"tooltip\":\"Pick the window to record…\",\"elapsed\":\"\"}",
            StatusReport.Compute(WithCapture(new Capture(CapturePhase.Starting)), Now, false).ToJson());
    }
}
