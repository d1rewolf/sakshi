using System.Globalization;
using System.Text;

namespace Sakshi.Transcribe;

/// <summary>Renders meeting.md exactly as bin/transcribe-meeting's Python step does.</summary>
public static class MeetingMarkdown
{
    public static string Render(
        string sourceFileName,
        IReadOnlyList<FrameRecord> frames,
        IReadOnlyList<Segment> segments,
        bool noFrames)
    {
        var segs = SpokenOnly(segments);
        var lines = new List<string>
        {
            $"# Meeting: {Path.GetFileNameWithoutExtension(sourceFileName)}",
            "",
            $"- Source: `{sourceFileName}`",
            noFrames
                ? $"- Segments: {segs.Count}"
                : $"- Frames: {frames.Count}  ·  Segments: {segs.Count}",
            "",
        };

        if (noFrames)
        {
            foreach (var s in segs) lines.Add(Quote(s));
            lines.Add("");
            return Join(lines);
        }

        var buckets = frames.Select(_ => new List<Spoken>()).ToList();
        var preface = new List<Spoken>();
        foreach (var s in segs)
        {
            var b = BucketFor(frames, s.Start);
            (b < 0 ? preface : buckets[b]).Add(s);
        }

        if (preface.Count > 0)
        {
            lines.Add("## Pre-frame transcript");
            lines.Add("");
            foreach (var s in preface) lines.Add(Quote(s));
            lines.Add("");
        }
        for (var i = 0; i < frames.Count; i++)
        {
            var ts = Hms(frames[i].Seconds);
            lines.Add($"## [{ts}]");
            lines.Add("");
            lines.Add($"![frame at {ts}](frames/{frames[i].FileName})");
            lines.Add("");
            foreach (var s in buckets[i]) lines.Add(Quote(s));
            lines.Add("");
        }
        return Join(lines);
    }

    /// <summary>Number of segments that carry text (what meeting.md counts).</summary>
    public static int CountSpoken(IReadOnlyList<Segment> segments) => SpokenOnly(segments).Count;

    private readonly record struct Spoken(double Start, double End, string Text);

    private static List<Spoken> SpokenOnly(IReadOnlyList<Segment> segments) =>
        segments
            .Select(s => new Spoken(s.Start.TotalMilliseconds / 1000.0, s.End.TotalMilliseconds / 1000.0, s.Text.Trim()))
            .Where(s => s.Text.Length > 0)
            .ToList();

    // Last frame whose time is <= t; -1 when t precedes the first frame (or there are none).
    private static int BucketFor(IReadOnlyList<FrameRecord> frames, double t)
    {
        if (frames.Count == 0 || t < frames[0].Seconds) return -1;
        int lo = 0, hi = frames.Count - 1, ans = 0;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (frames[mid].Seconds <= t) { ans = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return ans;
    }

    private static string Quote(Spoken s) => $"> [{Hms(s.Start)} → {Hms(s.End)}] {s.Text}";

    private static string Hms(double t)
    {
        var s = (long)t;
        return string.Create(CultureInfo.InvariantCulture, $"{s / 3600:00}:{s % 3600 / 60:00}:{s % 60:00}");
    }

    private static string Join(List<string> lines) => string.Join("\n", lines);
}
