using System.Diagnostics;
using Sakshi.Core.Paths;
using Sakshi.Platform.Linux;
using Sakshi.Platform.MacOS;
using Sakshi.Platform.Windows;

namespace Sakshi.Platform.Doctor;

/// <summary>
/// Everything <see cref="Doctor.Run"/> looks at on the machine, as functions, so tests inject fakes and
/// touch nothing. <see cref="ObsLaunch"/> throws <see cref="InvalidOperationException"/> when OBS is not installed.
/// </summary>
public sealed record DoctorProbes(
    Func<string, string?> Which,
    Func<string, bool> FileExists,
    Func<string, string?> ReadFile,
    Func<string, IReadOnlyList<string>> FilesIn,
    Func<string, IReadOnlyList<string>, ProcessResult> Run,
    Func<string, string?> Env,
    Func<ObsLaunch> ObsLaunch,
    Func<bool> ObsRunning,
    Func<string, string?> FileVersion)
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(5);

    /// <summary>The ffmpeg shipped with this copy of Sakshi, or null (a build from source uses the one on PATH).</summary>
    public string? BundledFfmpeg { get; init; }

    /// <summary>How a fix command runs sakshi on this machine (the AppImage's link, or this executable).</summary>
    public string SakshiCommand { get; init; } = "sakshi";

    /// <summary>The probes for this machine. Nothing runs until a probe is called.</summary>
    public static DoctorProbes Production(HostOs os, IProcessRunner runner, string? bundledFfmpeg)
    {
        IObsInstall install = os switch
        {
            HostOs.MacOS => new MacObsInstall(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Directory.Exists),
            HostOs.Windows => new WindowsObsInstall(WindowsObsInstall.ReadRegistryInstallDir, File.Exists, Environment.GetEnvironmentVariable),
            _ => new LinuxObsInstall(runner),
        };
        return new DoctorProbes(
            runner.Which,
            File.Exists,
            f => File.Exists(f) ? File.ReadAllText(f) : null,
            d => Directory.Exists(d) ? Directory.GetFiles(d) : [],
            (file, args) => runner.RunAsync(file, args, CommandTimeout, CancellationToken.None).GetAwaiter().GetResult(),
            Environment.GetEnvironmentVariable,
            () => install.LaunchAsync([], CancellationToken.None).GetAwaiter().GetResult(),
            () => runner.IsRunning(install.ProcessName),
            f => File.Exists(f) ? FileVersionInfo.GetVersionInfo(f).ProductVersion : null)
        {
            BundledFfmpeg = bundledFfmpeg,
            // An AppImage runs from a temporary mount; the integration keeps ~/.local/bin/sakshi pointing at it.
            SakshiCommand = Environment.GetEnvironmentVariable("APPIMAGE") is { Length: > 0 }
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "sakshi")
                // Run as `dotnet sakshi.dll` (development), the process is dotnet itself: fall back to the name.
                : Environment.ProcessPath is { } exe && !Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
                    ? exe : "sakshi",
        };
    }
}
