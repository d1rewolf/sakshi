using System.Runtime.InteropServices;

namespace Sakshi.App;

/// <summary>
/// Windows lets only the process the user is working in bring a window to the front. A command such as
/// <c>sakshi toggle</c> run from a keyboard shortcut has that right but the tray does not, so the window picker would
/// open behind the active window, without the keyboard. The command hands its right on before asking the tray.
/// macOS has its own rule; see <see cref="ClaimForMenuClick"/>.
/// </summary>
internal static class ForegroundRights
{
    private const int AnyProcess = -1; // ASFW_ANY

    public static void Share()
    {
        if (!OperatingSystem.IsWindows()) return;
        try { AllowSetForegroundWindow(AnyProcess); }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { }   // never stops the command
    }

    /// <summary>
    /// macOS: makes Sakshi the active app while the user is still clicking its menu. A menu-bar app is not activated by
    /// a click on its menu, and macOS 14 and later refuse to activate an app later on its own, so a prompt that opens a
    /// few seconds after "Start recording" would sit behind the active app without the keyboard. Called from the click.
    /// </summary>
    public static void ClaimForMenuClick()
    {
        if (!OperatingSystem.IsMacOS()) return;
        try
        {
            var app = objc_msgSend(objc_getClass("NSApplication"), sel_registerName("sharedApplication"));
            if (app != IntPtr.Zero) objc_msgSend(app, sel_registerName("activateIgnoringOtherApps:"), 1);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { }   // never stops the click
    }

    private const string ObjC = "/usr/lib/libobjc.A.dylib";

    [DllImport(ObjC)]
    private static extern IntPtr objc_getClass(string name);

    [DllImport(ObjC)]
    private static extern IntPtr sel_registerName(string name);

    [DllImport(ObjC)]
    private static extern IntPtr objc_msgSend(IntPtr receiver, IntPtr selector);

    [DllImport(ObjC)]
    private static extern void objc_msgSend(IntPtr receiver, IntPtr selector, byte flag);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int processId);
}
