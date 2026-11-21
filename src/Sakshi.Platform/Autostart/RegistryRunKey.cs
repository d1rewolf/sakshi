using System.Runtime.Versioning;
using System.Security;
using Microsoft.Win32;

namespace Sakshi.Platform.Autostart;

/// <summary>HKCU\Software\Microsoft\Windows\CurrentVersion\Run. Registry failures surface as <see cref="IOException"/>.</summary>
[SupportedOSPlatform("windows")]
public sealed class RegistryRunKey : IRunKey
{
    private const string Path = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public string? Get(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(Path);
        return key?.GetValue(name) as string;
    }

    public void Set(string name, string value) => Write(key => key.SetValue(name, value, RegistryValueKind.String));

    public void Delete(string name) => Write(key => key.DeleteValue(name, throwOnMissingValue: false));

    private static void Write(Action<RegistryKey> action)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(Path, writable: true);
            action(key);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or SecurityException)
        {
            throw new IOException(e.Message, e);
        }
    }
}
