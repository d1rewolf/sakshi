using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Sakshi.Core.Store;

/// <summary>
/// File rename that can neither replace an existing file nor silently copy.
/// File.Move does both on a cross-device move, which would leave a truncated
/// file under its final name after a crash.
/// </summary>
internal static partial class NativeMove
{
    public enum Result { Moved, CrossDevice }

    // errno values; the numbers differ between Linux and macOS.
    private const int Eperm = 1, Eexist = 17, Exdev = 18, Einval = 22;
    private static readonly int Enosys = OperatingSystem.IsMacOS() ? 78 : 38;
    private static readonly int Enotsup = OperatingSystem.IsMacOS() ? 45 : 95;
    private static readonly int Eopnotsupp = OperatingSystem.IsMacOS() ? 102 : 95;
    private const int ErrorNotSameDevice = 17;    // Windows

    // Test seams: each returns 0 on success, else the errno.
    internal static Func<string, string, int> LinkImpl = DefaultLink;
    internal static Func<string, string, int> RenameNoReplaceImpl = DefaultRenameNoReplace;

    [DllImport("libc", SetLastError = true, EntryPoint = "link")]
    private static extern int Link(string oldPath, string newPath);

    [DllImport("libc", SetLastError = true, EntryPoint = "unlink")]
    private static extern int Unlink(string path);

    [DllImport("libc", SetLastError = true, EntryPoint = "rename")]
    private static extern int Rename(string oldPath, string newPath);

    [DllImport("libc", SetLastError = true, EntryPoint = "renameat2")]
    private static extern int RenameAt2(int oldDirFd, string oldPath, int newDirFd, string newPath, uint flags);

    [DllImport("libc", SetLastError = true, EntryPoint = "renamex_np")]
    private static extern int RenameXNp(string oldPath, string newPath, uint flags);

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "MoveFileExW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(string source, string dest, int flags);

    private static int DefaultLink(string source, string dest) =>
        Link(source, dest) == 0 ? 0 : Marshal.GetLastPInvokeError();

    private static int DefaultRenameNoReplace(string source, string dest)
    {
        try
        {
            if (OperatingSystem.IsMacOS())
                return RenameXNp(source, dest, 0x4 /* RENAME_EXCL */) == 0 ? 0 : Marshal.GetLastPInvokeError();
            // AT_FDCWD = -100, RENAME_NOREPLACE = 1
            return RenameAt2(-100, source, -100, dest, 1) == 0 ? 0 : Marshal.GetLastPInvokeError();
        }
        catch (EntryPointNotFoundException)
        {
            return Enosys; // old libc: use the last resort
        }
    }

    /// <summary>Moves a file; throws IOException when the destination exists or on any error other than cross-device.</summary>
    public static Result MoveFile(string source, string dest)
    {
        if (OperatingSystem.IsWindows())
        {
            // Flags 0: no copy, no replace.
            if (MoveFileEx(source, dest, 0)) return Result.Moved;
            var err = Marshal.GetLastPInvokeError();
            if (err == ErrorNotSameDevice) return Result.CrossDevice;
            throw Fail(source, dest, err);
        }

        var e = LinkImpl(source, dest);
        if (e == 0)
        {
            if (Unlink(source) != 0)
            {
                var err = Marshal.GetLastPInvokeError();
                throw new IOException($"{dest} is complete but {source} could not be removed: {Marshal.GetPInvokeErrorMessage(err)}");
            }
            return Result.Moved;
        }
        if (e == Exdev) return Result.CrossDevice;
        // vfat, exFAT, many SMB and FUSE mounts have no hard links.
        if (e == Eperm || e == Enosys || e == Enotsup || e == Eopnotsupp) return RenameWithoutReplace(source, dest);
        throw Fail(source, dest, e);
    }

    private static Result RenameWithoutReplace(string source, string dest)
    {
        var e = RenameNoReplaceImpl(source, dest);
        if (e == 0) return Result.Moved;
        if (e == Exdev) return Result.CrossDevice;
        if (e != Einval && e != Enosys && e != Enotsup && e != Eopnotsupp) throw Fail(source, dest, e);

        // Last resort: this filesystem cannot rename without replacing. The
        // check-then-rename window is acceptable because the caller holds the
        // single-instance lock.
        if (File.Exists(dest) || Directory.Exists(dest))
            throw new IOException($"Destination already exists: {dest}");
        if (Rename(source, dest) == 0) return Result.Moved;
        e = Marshal.GetLastPInvokeError();
        return e == Exdev ? Result.CrossDevice : throw Fail(source, dest, e);
    }

    /// <summary>True for the exceptions Directory.Move raises when source and destination are on different volumes.</summary>
    public static bool IsCrossDevice(IOException e) =>
        OperatingSystem.IsWindows() ? e.HResult == unchecked((int)0x80070011) : e.HResult == Exdev;

    private static IOException Fail(string source, string dest, int err) =>
        new($"Could not move {source} to {dest}: {Marshal.GetPInvokeErrorMessage(err)}", new Win32Exception(err));
}
