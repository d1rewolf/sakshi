using Microsoft.Win32;

namespace Sakshi.Platform.Windows;

public sealed class WindowsObsInstall(
    Func<string?> registryInstallDir, Func<string, bool> fileExists, Func<string, string?> env) : IObsInstall
{
    public string ProcessName => "obs64";

    /// <summary>The production registry lookup: OBS's installer records its folder as the default value of this key.</summary>
    public static string? ReadRegistryInstallDir()
    {
        if (!OperatingSystem.IsWindows()) return null;
        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
        using var key = baseKey.OpenSubKey(@"SOFTWARE\OBS Studio");
        return key?.GetValue("") as string;
    }

    public Task<ObsLaunch> LaunchAsync(IReadOnlyList<string> extraArgs, CancellationToken ct)
    {
        var dirs = new List<string>();
        if (registryInstallDir() is { Length: > 0 } registry) dirs.Add(registry);
        if (env("ProgramFiles") is { Length: > 0 } programFiles) dirs.Add(Path.Combine(programFiles, "obs-studio"));
        foreach (var dir in dirs)
        {
            // OBS looks for its data relative to the working directory, so it must start in bin\64bit.
            var bin = Path.Combine(dir, "bin", "64bit");
            var exe = Path.Combine(bin, "obs64.exe");
            if (fileExists(exe)) return Task.FromResult(new ObsLaunch(exe, extraArgs, bin));
        }
        throw new InvalidOperationException("OBS is not installed (obs64.exe not found). Get it from https://obsproject.com");
    }
}
