using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Sakshi.App.Tray;

/// <summary>
/// Holds a system-wide shortcut for the tray on Windows. Windows sends the key press to a hidden message-only window
/// of ours, which Avalonia's message loop delivers on the UI thread. The process that gets the key press may bring
/// its windows to the front, so prompts opened by the shortcut get the keyboard.
/// Create on the UI thread; dispose there too.
/// </summary>
internal sealed class WindowsHotkey : IDisposable
{
    private const int Id = 1;
    private const uint NoRepeat = 0x4000;     // MOD_NOREPEAT: holding the keys down presses once
    private const uint WmHotkey = 0x0312;
    private static readonly IntPtr MessageOnly = new(-3);   // HWND_MESSAGE
    private const string ClassName = "SakshiHotkey";

    private static readonly WndProc Proc = OnMessage;      // kept alive: Windows calls it through a pointer
    private static readonly Dictionary<IntPtr, Action> Pressed = [];
    private static bool _classRegistered;

    private IntPtr _window;

    private WindowsHotkey(IntPtr window) => _window = window;

    /// <summary>Registers the shortcut. Throws <see cref="Win32Exception"/> when Windows refuses it, usually because
    /// another program already holds that shortcut.</summary>
    public static WindowsHotkey Register(Hotkey key, Action pressed)
    {
        var module = GetModuleHandleW(null);
        if (!_classRegistered)
        {
            var wc = new WndClassEx
            {
                cbSize = (uint)Marshal.SizeOf<WndClassEx>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(Proc),
                hInstance = module,
                lpszClassName = ClassName,
            };
            if (RegisterClassExW(ref wc) == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
            _classRegistered = true;
        }
        var window = CreateWindowExW(0, ClassName, null, 0, 0, 0, 0, 0, MessageOnly, IntPtr.Zero, module, IntPtr.Zero);
        if (window == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastPInvokeError());
        if (!RegisterHotKey(window, Id, key.Modifiers | NoRepeat, key.Key))
        {
            var error = Marshal.GetLastPInvokeError();
            DestroyWindow(window);
            throw new Win32Exception(error);
        }
        Pressed[window] = pressed;
        return new WindowsHotkey(window);
    }

    public void Dispose()
    {
        if (_window == IntPtr.Zero) return;
        UnregisterHotKey(_window, Id);
        Pressed.Remove(_window);
        DestroyWindow(_window);
        _window = IntPtr.Zero;
    }

    private static IntPtr OnMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == WmHotkey && wParam == Id && Pressed.TryGetValue(window, out var pressed))
        {
            try { pressed(); }
            catch (Exception) { }   // an exception must never unwind into Windows' message dispatch
            return IntPtr.Zero;
        }
        return DefWindowProcW(window, message, wParam, lParam);
    }

    private delegate IntPtr WndProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassExW(ref WndClassEx wc);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string className, string? windowName, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr window, int id);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? moduleName);
}
