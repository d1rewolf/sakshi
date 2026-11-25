using Sakshi.Transcribe;

namespace Sakshi.Speakers;

public static class MeetingLocator
{
    /// <summary>An existing folder path wins; else the newest folder named <paramref name="arg"/> under root/*/.</summary>
    public static string Find(string arg, IReadOnlyList<string> roots)
    {
        if (Directory.Exists(arg)) return Path.GetFullPath(arg);
        var name = arg.TrimEnd('/', '\\');
        var found = new List<(string Dir, string Day, DateTime Time)>();
        if (name.Length > 0 && name.IndexOfAny(['/', '\\']) < 0)
            foreach (var root in roots)
            {
                if (!Directory.Exists(root)) continue;
                try
                {
                    foreach (var day in Directory.EnumerateDirectories(root))
                    {
                        var dir = Path.Combine(day, name);
                        if (Directory.Exists(dir))
                            found.Add((dir, Path.GetFileName(day), Directory.GetLastWriteTimeUtc(dir)));
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        if (found.Count == 0)
            throw new UsageException($"no meeting named {arg} under {string.Join(", ", roots)}");
        return found.OrderByDescending(f => f.Day, StringComparer.Ordinal).ThenByDescending(f => f.Time).First().Dir;
    }
}
