using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Sakshi.Core.Ports;

namespace Sakshi.Platform.MacOS;

/// <summary>
/// The Finder's Trash through -[NSFileManager trashItemAtURL:resultingItemURL:error:], so "Put Back"
/// works. Any failure is an IOException and the item stays where it was: this never deletes.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class MacTrash : ITrash
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";

    public void MoveToTrash(string path) => Trash(path);

    /// <summary>Moves the item to the Trash and returns its path there.</summary>
    internal static string Trash(string path)
    {
        // The path is marshalled as a C string: a NUL would cut it short and trash a different item.
        if (path.Contains('\0')) throw new IOException($"Could not move {path} to the Trash: the path contains a NUL character");
        // This runs on a pool thread, which has no autorelease pool; without one every
        // autoreleased object below would leak.
        var pool = Send(Send(Class("NSAutoreleasePool"), Sel("alloc")), Sel("init"));
        try
        {
            var nsPath = SendUtf8(Class("NSString"), Sel("stringWithUTF8String:"), path);
            if (nsPath == IntPtr.Zero) throw new IOException($"Could not move {path} to the Trash: the path is not valid UTF-8");
            var url = Send(Class("NSURL"), Sel("fileURLWithPath:"), nsPath);
            var manager = Send(Class("NSFileManager"), Sel("defaultManager"));

            if (!SendTrash(manager, Sel("trashItemAtURL:resultingItemURL:error:"), url, out var resultingUrl, out var error))
                throw new IOException($"Could not move {path} to the Trash: {Describe(error)}");

            // Copied into a managed string before the pool releases the URL.
            return Utf8(Send(resultingUrl, Sel("path"))) ?? "";
        }
        finally
        {
            Send(pool, Sel("drain"));
        }
    }

    private static string Describe(IntPtr error) =>
        error == IntPtr.Zero ? "unknown error" : Utf8(Send(error, Sel("localizedDescription"))) ?? "unknown error";

    private static string? Utf8(IntPtr nsString) =>
        nsString == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(Send(nsString, Sel("UTF8String")));

    private static IntPtr Class(string name) => objc_getClass(name);

    private static IntPtr Sel(string name) => sel_registerName(name);

    [DllImport(ObjC)]
    private static extern IntPtr objc_getClass([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(ObjC)]
    private static extern IntPtr sel_registerName([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    // One objc_msgSend import per method signature: the call must match the method's real
    // argument and return types exactly.
    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr Send(IntPtr receiver, IntPtr selector);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr arg);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendUtf8(IntPtr receiver, IntPtr selector, [MarshalAs(UnmanagedType.LPUTF8Str)] string arg);

    // Objective-C BOOL is one byte.
    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool SendTrash(IntPtr receiver, IntPtr selector, IntPtr url, out IntPtr resultingUrl, out IntPtr error);
}
