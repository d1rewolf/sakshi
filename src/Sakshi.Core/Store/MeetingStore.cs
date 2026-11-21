using System.Globalization;
using System.Text.RegularExpressions;
using Sakshi.Core.Naming;
using Sakshi.Core.Ports;

namespace Sakshi.Core.Store;

public sealed class StoreFailedException(string message, string dir, Exception? inner = null) : Exception(message, inner)
{
    /// <summary>The meeting folder the move was attempted into.</summary>
    public string Dir { get; } = dir;
}

/// <summary>
/// Owns where meetings live: storing a recording, folding day folders into
/// each other, archiving old days and migrating the flat layout. Nothing here
/// overwrites or deletes anything.
/// </summary>
public sealed partial class MeetingStore(string meetingsDir, string archiveDir, ILog log)
{
    private static readonly string[] VideoExtensions = [".mp4", ".mkv", ".mov", ".ts", ".flv", ".webm"];

    [GeneratedRegex(@"^([0-9]{4})-([0-9]{2})-([0-9]{2})(-[A-Z][a-z]{2})?\z|^([0-9]{2})-([0-9]{2})-([0-9]{4})(-[A-Z][a-z]{2})?\z")]
    private static partial Regex DayLike();

    public string UniqueMeetingDir(string day, string name)
    {
        var dir = Path.Combine(meetingsDir, day, name);
        for (var n = 2; Exists(dir); n++)
            dir = Path.Combine(meetingsDir, day, $"{name}-{n}");
        return dir;
    }

    public string StoreVideo(string video, string day, string name)
    {
        var dir = UniqueMeetingDir(day, name);
        var final = Path.GetFileName(dir);
        var target = Path.Combine(dir, final + Path.GetExtension(video));
        var made = false;
        try
        {
            Directory.CreateDirectory(dir);
            made = true;
            SafeMove.MoveFile(video, target);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            if (made) TryRemoveEmpty(dir);
            throw new StoreFailedException($"Could not move the recording into {dir}: {e.Message}", dir, e);
        }
        log.Info($"stored: {target}");
        return target;
    }

    /// <summary>Renames src to dest, or folds src's entries into an existing dest, suffixing clashes -2, -3, ...</summary>
    public static void MoveMerge(string src, string dest)
    {
        if (!Exists(dest))
        {
            SafeMove.MoveDirectory(src, dest);
            return;
        }
        foreach (var e in Directory.EnumerateFileSystemEntries(src).OrderBy(p => p, StringComparer.Ordinal))
        {
            var baseName = Path.GetFileName(e);
            var target = Path.Combine(dest, baseName);
            for (var n = 2; Exists(target); n++)
                target = Path.Combine(dest, $"{baseName}-{n}");
            if (Directory.Exists(e)) SafeMove.MoveDirectory(e, target);
            else SafeMove.MoveFile(e, target);
        }
        Directory.Delete(src); // empty by now; throws rather than deleting content
    }

    /// <summary>
    /// Waits for OBS to finish the container: two identical non-zero sizes
    /// 0.5 s apart, at most 120 polls. Never throws; cancellation just returns.
    /// </summary>
    public static async Task WaitForSettledAsync(string path, Func<TimeSpan, CancellationToken, Task> delay, CancellationToken ct)
    {
        long last = -1;
        for (var i = 0; i < 120; i++)
        {
            var size = SizeOrMinusOne(path);
            if (size > 0 && size == last) return;
            last = size;
            try { await delay(TimeSpan.FromMilliseconds(500), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Archives day folders older than keepDays; returns what `housekeep -n` prints.</summary>
    public IReadOnlyList<string> Housekeep(int keepDays, DateOnly today, IReadOnlyCollection<string> busyDirs, bool dryRun)
    {
        var lines = new List<string>();
        if (keepDays <= 0 || !Directory.Exists(meetingsDir)) return lines;
        var cutoff = today.AddDays(-keepDays);
        var moved = 0;

        void Say(string s)
        {
            lines.Add(s);
            if (!dryRun) log.Info("housekeep: " + s);
        }

        foreach (var d in TopLevelDirs())
        {
            var name = Path.GetFileName(d);
            if (!DayFolder.TryParse(name, out var day) || day >= cutoff) continue;
            var busy = busyDirs.FirstOrDefault(b => IsUnder(b, d));
            if (busy is not null)
            {
                Say($"{name}: skipped, still transcribing {Path.GetFileName(Trim(busy))}");
                continue;
            }
            var dest = Path.Combine(archiveDir, name);
            Say($"{name} -> {Rel(dest)}{(Exists(dest) ? " (merge)" : "")}");
            if (dryRun) continue;
            try
            {
                Directory.CreateDirectory(archiveDir);
                MoveMerge(d, dest);
                moved++;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Say($"could not move {d} to {dest}");
                if (!dryRun) log.Info($"housekeep: {e.Message}");
            }
        }
        if (!dryRun && moved > 0)
            log.Info($"housekeep: archived {moved} day folder(s) older than {cutoff.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}");
        return lines;
    }

    /// <summary>Moves flat meeting folders under their day and spells day folders YYYY-MM-DD-Dow.</summary>
    public IReadOnlyList<string> Migrate(bool dryRun)
    {
        if (!Directory.Exists(meetingsDir)) throw new DirectoryNotFoundException($"No meetings directory: {meetingsDir}");
        var lines = new List<string>();
        int moved = 0, renamed = 0;

        foreach (var d in TopLevelDirs())
        {
            var name = Path.GetFileName(d);
            if (DayLike().IsMatch(name)) continue;
            var day = DayFolder.For(new DateTimeOffset(ReferenceTime(d).ToLocalTime()));
            var dest = UniqueMeetingDir(day, name);
            lines.Add($"{name} -> {Rel(dest)}");
            if (dryRun) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            SafeMove.MoveDirectory(d, dest);
            moved++;
        }

        foreach (var d in TopLevelDirs())
        {
            var name = Path.GetFileName(d);
            if (!DayLike().IsMatch(name)) continue;
            if (!DayFolder.TryParse(name, out var date))
            {
                log.Info($"skipping {name}: not a date");
                continue;
            }
            var dest = Path.Combine(meetingsDir, DayFolder.For(new DateTimeOffset(date.ToDateTime(new TimeOnly(12, 0)), TimeSpan.Zero)));
            if (dest == d) continue;
            lines.Add($"{name} -> {Rel(dest)}{(Exists(dest) ? " (merge)" : "")}");
            if (dryRun) continue;
            MoveMerge(d, dest);
            renamed++;
        }
        if (!dryRun)
            log.Info($"migrate: moved {moved} folder(s) into day directories, renamed {renamed} day folder(s)");
        return lines;
    }

    // Newest top-level video, else newest entry anywhere below, else the folder itself.
    private static DateTime ReferenceTime(string dir)
    {
        var videos = SafeEnumerate(dir, SearchOption.TopDirectoryOnly, files: true)
            .Where(f => VideoExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)).ToList();
        if (videos.Count > 0) return videos.Max(v => File.GetLastWriteTimeUtc(v));
        var all = SafeEnumerate(dir, SearchOption.AllDirectories, files: false).ToList();
        if (all.Count > 0) return all.Max(Mtime);
        return Directory.GetLastWriteTimeUtc(dir);
    }

    private static IEnumerable<string> SafeEnumerate(string dir, SearchOption option, bool files)
    {
        try
        {
            return (files ? Directory.EnumerateFiles(dir, "*", option) : Directory.EnumerateFileSystemEntries(dir, "*", option)).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static long SizeOrMinusOne(string path)
    {
        try { return new FileInfo(path) is { Exists: true } f ? f.Length : -1; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return -1; }
    }

    private static DateTime Mtime(string p) =>
        Directory.Exists(p) ? Directory.GetLastWriteTimeUtc(p) : File.GetLastWriteTimeUtc(p);

    // Visible, non-archive top-level folders in a stable order (bash glob order).
    private List<string> TopLevelDirs() =>
        Directory.EnumerateDirectories(meetingsDir)
            .Where(d => !Path.GetFileName(d).StartsWith('.') && !SamePath(d, archiveDir))
            .OrderBy(d => d, StringComparer.Ordinal)
            .ToList();

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    private static bool Exists(string p) => File.Exists(p) || Directory.Exists(p);

    private static string Trim(string p) => p.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static bool SamePath(string a, string b) =>
        string.Equals(Trim(Path.GetFullPath(a)), Trim(Path.GetFullPath(b)), PathComparison);

    private static bool IsUnder(string path, string dir) =>
        Trim(Path.GetFullPath(path)).StartsWith(Trim(Path.GetFullPath(dir)) + Path.DirectorySeparatorChar, PathComparison);

    private string Rel(string path)
    {
        var root = Trim(meetingsDir) + Path.DirectorySeparatorChar;
        var s = path.StartsWith(root, PathComparison) ? path[root.Length..] : path;
        return s.Replace('\\', '/');
    }

    private static void TryRemoveEmpty(string dir)
    {
        try { Directory.Delete(dir); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
