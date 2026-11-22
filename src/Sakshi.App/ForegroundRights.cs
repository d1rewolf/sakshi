using System.Runtime.InteropServices;

namespace Sakshi.App;

/// <summary>
/// Windows lets only the process the user is working in bring a window to the front. A command such as
/// <c>sakshi toggle</c> run from a keyboard shortcut has that right but the tray does not, so the window picker would
/// open behind the active window, without the keyboard. The command hands its right on before asking the tray.
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

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int processId);
}
