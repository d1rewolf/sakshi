namespace Sakshi.Platform.Linux;

/// <summary>Finds running programs by the file name of argv[0], read from /proc/*/cmdline (Linux only).</summary>
internal static class ProcCmdline
{
    /// <summary>True when any process under <paramref name="procRoot"/> has an argv[0] whose file name is <paramref name="name"/>.</summary>
    public static bool AnyArgv0Named(string procRoot, string name)
    {
        IEnumerable<string> dirs;
        try { dirs = Directory.EnumerateDirectories(procRoot).ToList(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }

        foreach (var dir in dirs)
        {
            if (!Path.GetFileName(dir).All(char.IsAsciiDigit)) continue;
            try
            {
                using var fs = new FileStream(Path.Combine(dir, "cmdline"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var buf = new byte[4096];
                var n = fs.Read(buf, 0, buf.Length);
                var end = Array.IndexOf(buf, (byte)0, 0, n);
                var argv0 = System.Text.Encoding.UTF8.GetString(buf, 0, end < 0 ? n : end);
                if (argv0.Length > 0 && string.Equals(Path.GetFileName(argv0), name, StringComparison.Ordinal)) return true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return false;
    }
}
