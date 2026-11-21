namespace Sakshi.App.Install;

/// <summary>The executable that start-at-login launches.</summary>
internal static class TrayExecutable
{
    /// <summary>
    /// Velopack names the root stub after the pack title, not the main exe. Must equal vpk --packTitle plus
    /// ".exe" in .github/workflows/release.yml.
    /// </summary>
    internal const string WindowsRootStub = "Sakshi.exe";

    /// <summary>
    /// Windows installed: Velopack's root stub (<see cref="WindowsRootStub"/>), which survives updates (the
    /// versioned folder does not). Linux: the AppImage, whose path is not <c>ProcessPath</c> (that is inside the mount).
    /// Otherwise this process's own executable.
    /// </summary>
    public static string Resolve() => Resolve(OperatingSystem.IsWindows(), Environment.GetEnvironmentVariable,
        OperatingSystem.IsWindows() ? InstalledRoot() : null, Environment.ProcessPath, OperatingSystem.IsLinux());

    internal static string Resolve(bool windows, Func<string, string?> env, string? velopackRoot, string? processPath,
        bool linux = false)
    {
        if (windows && velopackRoot is { Length: > 0 }) return Path.Combine(velopackRoot, WindowsRootStub);
        if (linux && env("APPIMAGE") is { Length: > 0 } image) return image;
        return processPath ?? "sakshi";
    }

    /// <summary>The Velopack root folder, or null when this copy was not installed by Velopack or Velopack has not started.</summary>
    private static string? InstalledRoot() => InstalledCopy.Locator()?.RootAppDir;
}
