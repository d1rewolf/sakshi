namespace Sakshi.Core.HandOff;

/// <summary>
/// Port of resolve_cmd: a command containing a directory separator is used as
/// is; a bare name is looked up on PATH (with PATHEXT on Windows), then in
/// <paramref name="extraDirs"/> (the graphical session's PATH lacks ~/bin).
/// </summary>
public sealed class CommandResolver(IReadOnlyList<string> extraDirs, Func<string, string?> env)
{
    public string? Resolve(string command)
    {
        if (command.Contains(Path.DirectorySeparatorChar) || command.Contains(Path.AltDirectorySeparatorChar))
            return command;

        var pathDirs = (env("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        foreach (var dir in pathDirs.Concat(extraDirs))
        {
            if (string.IsNullOrEmpty(dir)) continue;
            foreach (var candidate in Candidates(Path.Combine(dir, command)))
                if (IsRunnable(candidate)) return candidate;
        }
        return null;
    }

    private IEnumerable<string> Candidates(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            yield return path;
            yield break;
        }
        var pathExt = env("PATHEXT");
        if (string.IsNullOrWhiteSpace(pathExt)) pathExt = ".COM;.EXE;.BAT;.CMD";
        if (Path.HasExtension(path)) yield return path;
        foreach (var ext in pathExt.Split(';', StringSplitOptions.RemoveEmptyEntries))
            yield return path + ext;
    }

    private static bool IsRunnable(string path)
    {
        if (!File.Exists(path)) return false;
        if (OperatingSystem.IsWindows()) return true;
        const UnixFileMode anyExec = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
        return (File.GetUnixFileMode(path) & anyExec) != 0;
    }
}
