using System.Security.Cryptography;
using System.Text.Json;

namespace Sakshi.Search;

public sealed record Meeting(string Dir, string Day, string Name, long Signature);

public sealed record Line(long StartMs, string Text);

public sealed class TranscriptException : Exception
{
    public TranscriptException(string message) : base(message) { }
    public TranscriptException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>Finds meeting folders and reads their whisper transcripts.</summary>
public static class Corpus
{
    public const string Transcript = "transcript.json";

    private static bool IsDayName(string name) =>
        name.Length >= 10
        && IsDigits(name, 0, 4) && name[4] == '-'
        && IsDigits(name, 5, 2) && name[7] == '-'
        && IsDigits(name, 8, 2);

    private static bool IsDigits(string s, int start, int count)
    {
        for (var i = start; i < start + count; i++)
            if (s[i] < '0' || s[i] > '9')
                return false;
        return true;
    }

    public static IReadOnlyList<Meeting> Discover(IEnumerable<string> roots)
    {
        var found = new List<Meeting>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var root in roots)
        {
            if (!Directory.Exists(root))
                continue;
            var dayDirs = Directory.GetDirectories(root)
                .OrderBy(p => Path.GetFileName(p), StringComparer.Ordinal);
            foreach (var dayDir in dayDirs)
            {
                var dayName = Path.GetFileName(dayDir);
                if (!IsDayName(dayName))
                    continue;
                var resolvedDay = Resolve(dayDir);
                Walk(dayDir, resolvedDay, dayDir, dayName, found, seen);
            }
        }
        return found;
    }

    /// <summary>Like Python's Path.resolve(): follows links in every component, not just the last.</summary>
    private static string Resolve(string path, int depth = 0)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? "";
        var current = root;
        foreach (var part in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if (depth > 32)
                continue;
            try
            {
                var target = new DirectoryInfo(current).ResolveLinkTarget(returnFinalTarget: true);
                if (target != null)
                    current = Resolve(target.FullName, depth + 1);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return current;
    }

    private static void Walk(string cur, string resolvedCur, string dayDir, string dayName,
        List<Meeting> found, HashSet<string> seen)
    {
        var transcript = Path.Combine(cur, Transcript);
        if (cur != dayDir && File.Exists(transcript) && seen.Add(resolvedCur))
        {
            var rel = Path.GetRelativePath(dayDir, cur).Split(Path.DirectorySeparatorChar);
            found.Add(new Meeting(cur, dayName, string.Join("/", rel), Signature(transcript)));
        }
        DirectoryInfo[] subdirs;
        try
        {
            subdirs = new DirectoryInfo(cur).GetDirectories()
                .Where(d => d.Name != "frames" && d.LinkTarget == null) // os.walk does not follow links
                .OrderBy(d => d.Name, StringComparer.Ordinal)
                .ToArray();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return; // os.walk skips unreadable directories
        }
        foreach (var d in subdirs)
            Walk(d.FullName, Path.Combine(resolvedCur, d.Name), dayDir, dayName, found, seen);
    }

    /// <summary>
    /// First 8 bytes of SHA-256 over mtime ticks, length and the first and last 4 KB of
    /// transcript.json. The content bytes catch a file restored with an old mtime.
    /// </summary>
    private static long Signature(string path)
    {
        var info = new FileInfo(path);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha.AppendData(BitConverter.GetBytes(info.LastWriteTimeUtc.Ticks));
        sha.AppendData(BitConverter.GetBytes(info.Length));
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var buf = new byte[4096];
        var n = fs.ReadAtLeast(buf, buf.Length, throwOnEndOfStream: false);
        sha.AppendData(buf.AsSpan(0, n));
        if (fs.Length > buf.Length)
        {
            fs.Seek(Math.Max(buf.Length, fs.Length - buf.Length), SeekOrigin.Begin);
            n = fs.ReadAtLeast(buf, buf.Length, throwOnEndOfStream: false);
            sha.AppendData(buf.AsSpan(0, n));
        }
        return BitConverter.ToInt64(sha.GetHashAndReset(), 0);
    }

    public static IReadOnlyList<Line> ReadLines(string meetingDir)
    {
        var path = Path.Combine(meetingDir, Transcript);
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            var lines = new List<Line>();
            foreach (var seg in doc.RootElement.GetProperty("transcription").EnumerateArray())
            {
                var text = seg.GetProperty("text").GetString()?.Trim()
                    ?? throw new InvalidOperationException("segment text is null");
                if (text.Length == 0)
                    continue;
                var from = seg.GetProperty("offsets").GetProperty("from");
                lines.Add(new Line((long)from.GetDouble(), text));
            }
            return lines;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException
            or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new TranscriptException($"{path}: {e.Message}", e);
        }
    }
}
