using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Sakshi.Core.Ports;

namespace Sakshi.Platform.Windows;

/// <summary>
/// The Recycle Bin through SHFileOperationW. Not Microsoft.VisualBasic's FileSystem.DeleteFile: that
/// does a plain File.Delete when Environment.UserInteractive is false. Any failure is an IOException
/// and the item stays where it was: this never deletes permanently without asking.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RecycleBin : ITrash
{
    private const uint FoDelete = 3;
    private const ushort FofSilent = 0x4, FofNoConfirmation = 0x10, FofAllowUndo = 0x40,
        FofNoErrorUi = 0x400, FofWantNukeWarning = 0x4000;

    // FOF_WANTNUKEWARNING overrides FOF_NOCONFIRMATION for an item that would be destroyed rather
    // than recycled (too big for the bin, a network drive): Windows asks, and "No" aborts.
    private const ushort Flags = FofAllowUndo | FofNoConfirmation | FofSilent | FofNoErrorUi | FofWantNukeWarning;

    public void MoveToTrash(string path)
    {
        // pFrom is a NUL-separated list: a NUL would turn one path into two and recycle the other one too.
        if (path.Contains('\0')) throw Fail(path, "the path contains a NUL character");
        // shellapi.h packs this struct to 1 byte on 32-bit only; the layout below is the 64-bit one.
        if (!Environment.Is64BitProcess) throw Fail(path, "only 64-bit processes are supported");
        // pFrom takes wildcards, which would recycle whatever they match.
        if (path.Contains('*') || path.Contains('?')) throw Fail(path, "the path contains a wildcard");
        // A relative path cannot be undone from the bin.
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!File.Exists(full) && !Directory.Exists(full)) throw Fail(path, "it does not exist");

        // pFrom is a list of NUL-terminated paths ended by an empty one; StringToHGlobalUni adds the final NUL.
        var from = Marshal.StringToHGlobalUni(full + "\0");
        try
        {
            var op = new ShFileOpStruct { wFunc = FoDelete, pFrom = from, fFlags = Flags };
            var rc = SHFileOperationW(ref op);
            if (rc != 0) throw Fail(path, $"error 0x{rc:X}");
            if (op.fAnyOperationsAborted) throw Fail(path, "the operation was cancelled");
        }
        finally
        {
            Marshal.FreeHGlobal(from);
        }
        if (File.Exists(full) || Directory.Exists(full)) throw Fail(path, "it is still there afterwards");
    }

    private static IOException Fail(string path, string why) => new($"Could not move {path} to the Recycle Bin: {why}");

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOpStruct
    {
        public IntPtr hwnd;
        public uint wFunc;
        public IntPtr pFrom;
        public IntPtr pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public IntPtr lpszProgressTitle;
    }

    [DllImport("shell32", CharSet = CharSet.Unicode, EntryPoint = "SHFileOperationW")]
    private static extern int SHFileOperationW(ref ShFileOpStruct op);
}
