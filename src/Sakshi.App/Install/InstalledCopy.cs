using Velopack.Locators;

namespace Sakshi.App.Install;

/// <summary>Whether this process is an installed copy (as opposed to a source build or a dev run).</summary>
internal static class InstalledCopy
{
    public static bool Detect() => Detect(OperatingSystem.IsLinux(), Environment.GetEnvironmentVariable, VelopackInstalled);

    /// <summary>
    /// Linux: running from the AppImage, which is the only way the package is shipped there. Elsewhere: Velopack
    /// found its install metadata next to this executable.
    /// </summary>
    internal static bool Detect(bool linux, Func<string, string?> env, Func<bool> velopackInstalled) =>
        linux ? env("APPIMAGE") is { Length: > 0 } : velopackInstalled();

    internal static bool VelopackInstalled() => Locator() is not null;

    /// <summary>Velopack's locator for an installed copy, or null when not installed or Velopack has not started.</summary>
    internal static IVelopackLocator? Locator()
    {
        try
        {
            var locator = VelopackLocator.Current;
            return locator.CurrentlyInstalledVersion is null ? null : locator;
        }
        catch (InvalidOperationException)
        {
            return null;   // Current throws until VelopackApp has run (in tests, or in a call before Main's hook)
        }
    }
}
