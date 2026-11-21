namespace Sakshi.Core.Store;

/// <summary>
/// Moves that never overwrite and never lose the source: a rename when
/// possible, otherwise copy to "&lt;dest&gt;.sakshi-partial", verify, rename,
/// and only then remove the source. On any failure the partial is removed and
/// the exception rethrown with the source untouched.
/// </summary>
public static class SafeMove
{
    private const string PartialSuffix = ".sakshi-partial";

    // Test seams.
    internal static Func<string, string, bool> SameVolume = static (_, _) => true;
    internal static Action<string, string> CopyFile = static (from, to) => File.Copy(from, to, overwrite: false);

    // Test seam: runs after a directory copy is verified, before the source is removed.
    internal static Action<string>? AfterVerify;

    public static void MoveFile(string source, string dest)
    {
        if (File.Exists(dest) || Directory.Exists(dest))
            throw new IOException($"Destination already exists: {dest}");
        if (SameVolume(source, dest) && NativeMove.MoveFile(source, dest) == NativeMove.Result.Moved)
            return;
        CopyVerifyRenameFile(source, dest);
    }

    public static void MoveDirectory(string source, string dest)
    {
        if (File.Exists(dest) || Directory.Exists(dest))
            throw new IOException($"Destination already exists: {dest}");
        if (SameVolume(source, dest) && !DifferentRoots(source, dest))
        {
            try
            {
                Directory.Move(source, dest);
                return;
            }
            catch (IOException e) when (NativeMove.IsCrossDevice(e) && Directory.Exists(source) && !Directory.Exists(dest))
            {
            }
        }
        CopyVerifyRenameDirectory(source, dest);
    }

    private static bool DifferentRoots(string a, string b) =>
        OperatingSystem.IsWindows() &&
        !string.Equals(Path.GetPathRoot(Path.GetFullPath(a)), Path.GetPathRoot(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);

    private static void CopyVerifyRenameFile(string source, string dest)
    {
        var partial = dest + PartialSuffix;
        RefuseStalePartial(partial);
        try
        {
            CopyFile(source, partial);
            if (new FileInfo(partial).Length != new FileInfo(source).Length)
                throw new IOException($"Copy of {source} is incomplete");
            if (NativeMove.MoveFile(partial, dest) != NativeMove.Result.Moved)
                throw new IOException($"Could not rename {partial} to {dest}");
        }
        catch
        {
            TryDeleteFile(partial);
            throw;
        }
        File.Delete(source);
    }

    private static void CopyVerifyRenameDirectory(string source, string dest)
    {
        var partial = dest + PartialSuffix;
        RefuseStalePartial(partial);
        List<(string Path, long Length)> files;
        List<string> dirs;
        try
        {
            CopyTree(source, partial);
            (files, dirs) = VerifyTree(source, partial);
            Directory.Move(partial, dest);
        }
        catch
        {
            TryDeleteDirectory(partial);
            throw;
        }
        AfterVerify?.Invoke(source);
        // Remove exactly what was copied and verified. Anything added to the
        // source since makes the directory delete throw, and stays put.
        foreach (var (f, len) in files)
        {
            if (new FileInfo(f).Length != len)
                throw new IOException($"{f} changed while it was being moved; it stays in place");
            File.Delete(f);
        }
        foreach (var d in dirs.OrderByDescending(d => d.Length)) Directory.Delete(d);
        Directory.Delete(source);
    }

    private static void CopyTree(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var f in Directory.EnumerateFiles(source))
            CopyFile(f, Path.Combine(dest, Path.GetFileName(f)));
        foreach (var d in Directory.EnumerateDirectories(source))
            CopyTree(d, Path.Combine(dest, Path.GetFileName(d)));
    }

    private static (List<(string Path, long Length)> Files, List<string> Dirs) VerifyTree(string source, string copy)
    {
        var files = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)
            .Select(f => (Path: f, Length: new FileInfo(f).Length)).ToList();
        var dirs = Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories).ToList();
        foreach (var (f, len) in files)
        {
            var c = Path.Combine(copy, Path.GetRelativePath(source, f));
            if (!File.Exists(c) || new FileInfo(c).Length != len)
                throw new IOException($"Copy of {f} is incomplete");
        }
        foreach (var d in dirs)
            if (!Directory.Exists(Path.Combine(copy, Path.GetRelativePath(source, d))))
                throw new IOException($"Copy of {d} is incomplete");
        return (files, dirs);
    }

    private static void RefuseStalePartial(string partial)
    {
        if (File.Exists(partial) || Directory.Exists(partial))
            throw new IOException($"A partial copy from an earlier attempt is in the way, move it aside: {partial}");
    }

    private static void TryDeleteFile(string path)
    {
        try { File.Delete(path); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
