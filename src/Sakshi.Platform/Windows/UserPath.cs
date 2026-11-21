using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Sakshi.Platform.Windows;

/// <summary>
/// The user's PATH (<c>HKCU\Environment</c> value <c>Path</c>). <see cref="Add"/> and <see cref="Remove"/> are pure
/// over the ';'-separated value so they can be tested anywhere; <see cref="Apply"/> is the Windows-only registry write.
/// </summary>
public static class UserPath
{
    /// <summary>
    /// Appends <paramref name="dir"/> unless an entry already equals it. Nothing else is rewritten: empty entries
    /// and a trailing ';' the user had stay. After a trailing ';' the dir gets one too, so <see cref="Remove"/>
    /// gives back exactly the original value.
    /// </summary>
    public static string Add(string current, string dir)
    {
        if (current.Length == 0) return dir;
        var parts = current.Split(';').ToList();
        if (parts.Any(e => Same(e, dir))) return current;
        parts.Insert(parts[^1].Length == 0 ? parts.Count - 1 : parts.Count, dir);
        return string.Join(';', parts);
    }

    /// <summary>Removes only entries equal to <paramref name="dir"/> (with their separator); every other character stays as written.</summary>
    public static string Remove(string current, string dir) =>
        string.Join(';', current.Split(';').Where(e => !Same(e, dir)));

    private static bool Same(string a, string b) =>
        string.Equals(Trim(a), Trim(b), StringComparison.OrdinalIgnoreCase);

    private static string Trim(string s) => s.TrimEnd('\\', '/');

    /// <summary>
    /// Rewrites the user's PATH through <paramref name="change"/> and tells running programs (Explorer, so new
    /// terminals see it). The value is read unexpanded and written back with its own kind (REG_EXPAND_SZ when
    /// there was none): expanding it would bake the user's %VARIABLES% into literal paths.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static void Apply(Func<string, string> change) =>
        Apply(new RegistryEnvironmentKey(), change, BroadcastEnvironmentChange);

    /// <summary>
    /// Nothing is written, and nothing broadcast, when <paramref name="change"/> leaves the value as it was.
    /// A Path of an unexpected kind is left alone and reported by throwing: treating it
    /// as empty would overwrite the user's PATH.
    /// </summary>
    internal static void Apply(IEnvironmentKey key, Func<string, string> change, Action broadcast)
    {
        var (value, kind) = key.GetPath();
        if (kind is PathKind.Other)
            throw new InvalidOperationException($"the user's Path is not a text value, which is not changed");
        var current = value ?? "";
        var updated = change(current);
        if (updated == current) return;
        key.SetPath(updated, kind ?? PathKind.ExpandString);
        broadcast();
    }

    [SupportedOSPlatform("windows")]
    private static void BroadcastEnvironmentChange()
    {
        const uint WmSettingChange = 0x001A, SmtoAbortIfHung = 0x0002;
        // A hung window must not hang the installer: give each 1 s (Velopack's hook time limit is short), then move on.
        SendMessageTimeout(new IntPtr(0xFFFF), WmSettingChange, UIntPtr.Zero, "Environment", SmtoAbortIfHung, 1000, out _);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, UIntPtr wParam, string lParam,
        uint flags, uint timeout, out UIntPtr result);
}

/// <summary>How the Path value is stored; Other is anything that is not text (REG_MULTI_SZ, a number).</summary>
internal enum PathKind { String, ExpandString, Other }

/// <summary>The registry value behind the user's PATH, so the code that guards it can be tested with a fake.</summary>
internal interface IEnvironmentKey
{
    /// <summary>The value (null when absent or not a string) and its kind (null when absent).</summary>
    (string? Value, PathKind? Kind) GetPath();
    void SetPath(string value, PathKind kind);
}

[SupportedOSPlatform("windows")]
internal sealed class RegistryEnvironmentKey : IEnvironmentKey
{
    public (string? Value, PathKind? Kind) GetPath()
    {
        using var env = Registry.CurrentUser.CreateSubKey("Environment", writable: true);
        var raw = env.GetValue("Path", null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (raw is null) return (null, null);
        return env.GetValueKind("Path") switch
        {
            RegistryValueKind.String => ((string)raw, PathKind.String),
            RegistryValueKind.ExpandString => ((string)raw, PathKind.ExpandString),
            _ => (null, PathKind.Other),
        };
    }

    public void SetPath(string value, PathKind kind)
    {
        using var env = Registry.CurrentUser.CreateSubKey("Environment", writable: true);
        env.SetValue("Path", value, kind == PathKind.String ? RegistryValueKind.String : RegistryValueKind.ExpandString);
    }
}
