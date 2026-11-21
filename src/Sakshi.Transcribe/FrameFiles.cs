using System.Globalization;
using System.Text.RegularExpressions;

namespace Sakshi.Transcribe;

public sealed record FrameRecord(double Seconds, string FileName);

public static partial class FrameFiles
{
    [GeneratedRegex(@"pts_time:(\d+(?:\.\d+)?)")]
    private static partial Regex PtsTime();

    public static IReadOnlyList<double> ParseMetadata(string text)
    {
        var times = new List<double>();
        foreach (var line in text.Split('\n'))
        {
            var m = PtsTime().Match(line);
            if (m.Success) times.Add(double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture));
        }
        return times;
    }

    /// <summary>
    /// Deletes what an earlier (perhaps interrupted) run derived in <paramref name="framesDir"/>:
    /// raw_*.jpg, frame_*.jpg, .metadata.txt and .hashlist.txt. Nothing else is touched.
    /// </summary>
    public static void ClearDerived(string framesDir)
    {
        if (!Directory.Exists(framesDir)) return;
        foreach (var path in Directory.EnumerateFiles(framesDir).ToList())
        {
            var name = Path.GetFileName(path);
            var derived = name is ".metadata.txt" or ".hashlist.txt"
                || ((name.StartsWith("raw_", StringComparison.Ordinal) || name.StartsWith("frame_", StringComparison.Ordinal))
                    && name.EndsWith(".jpg", StringComparison.Ordinal));
            if (derived) File.Delete(path);
        }
    }

    /// <summary>
    /// Renames raw_NNNN.jpg (sorted by name) to frame_HH-MM-SS.jpg using the i-th time,
    /// suffixing _1, _2 on clashes and deleting raw files without a time. Result is sorted by time.
    /// </summary>
    public static IReadOnlyList<FrameRecord> RenameRaw(string framesDir, IReadOnlyList<double> times)
    {
        var records = new List<FrameRecord>();
        if (!Directory.Exists(framesDir)) return records;

        var raw = Directory.GetFiles(framesDir)
            .Select(Path.GetFileName)
            .OfType<string>()
            .Where(n => n.StartsWith("raw_", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToList();

        for (var i = 0; i < raw.Count; i++)
        {
            var source = Path.Combine(framesDir, raw[i]);
            if (i >= times.Count)
            {
                File.Delete(source);
                continue;
            }
            var t = times[i];
            var stem = $"frame_{Stamp(t)}";
            var target = stem + ".jpg";
            for (var suffix = 1; File.Exists(Path.Combine(framesDir, target)); suffix++)
                target = $"{stem}_{suffix}.jpg";
            File.Move(source, Path.Combine(framesDir, target));
            records.Add(new FrameRecord(t, target));
        }

        records.Sort((a, b) =>
        {
            var c = a.Seconds.CompareTo(b.Seconds);
            return c != 0 ? c : string.CompareOrdinal(a.FileName, b.FileName);
        });
        return records;
    }

    private static string Stamp(double t)
    {
        var s = (long)t;
        return string.Create(CultureInfo.InvariantCulture, $"{s / 3600:00}-{s % 3600 / 60:00}-{s % 60:00}");
    }
}
