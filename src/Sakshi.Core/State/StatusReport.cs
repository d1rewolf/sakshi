using System.Text.Encodings.Web;
using System.Text.Json;

namespace Sakshi.Core.State;

public sealed record Status(string State, string Tooltip, string Elapsed)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public string ToJson() => JsonSerializer.Serialize(new { state = State, tooltip = Tooltip, elapsed = Elapsed }, JsonOptions);
}

/// <summary>Pure view of the state; it never clears stale entries (the tray does that).</summary>
public static class StatusReport
{
    public static Status Compute(SakshiState s, DateTimeOffset now, bool lockHeld)
    {
        var capture = s.Capture;
        switch (capture.Phase)
        {
            case CapturePhase.Starting:
                return new Status("starting", "Pick the window to record…", Elapsed(capture.Started, now));
            case CapturePhase.Naming:
                return new Status("naming",
                    lockHeld ? "Waiting for the meeting name…" : "Unnamed recording waiting — click to name and store it",
                    Elapsed(capture.Started, now));
            case CapturePhase.Recording:
                var rec = Elapsed(capture.Started, now);
                return new Status("recording", $"Recording meeting ({rec}) — click to stop", rec);
        }

        if (s.Jobs.Count > 0)
        {
            var job = s.Jobs[0];
            var elapsed = Elapsed(job.Started, now);
            var tooltip = $"Transcribing {job.Name} ({elapsed})";
            if (!string.IsNullOrEmpty(job.HandOff)) tooltip += $", then handing it to {job.HandOff}";
            if (s.Jobs.Count > 1) tooltip += $" (+{s.Jobs.Count - 1} queued)";
            return new Status("transcribing", tooltip, elapsed);
        }

        return new Status("idle", "Record a meeting (OBS)", "");
    }

    private static string Elapsed(DateTimeOffset? started, DateTimeOffset now)
    {
        if (started is null) return "";
        var s = Math.Max(0, (long)(now - started.Value).TotalSeconds);
        return $"{s / 60}:{s % 60:D2}";
    }
}
