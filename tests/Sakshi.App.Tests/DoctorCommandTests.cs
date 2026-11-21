using Sakshi.App.Tray;
using Sakshi.Platform;
using Sakshi.Platform.Doctor;

namespace Sakshi.App.Tests;

[Collection("Environment")]
public sealed class DoctorCommandTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "sakshi-doctor-" + Guid.NewGuid().ToString("N"));

    public DoctorCommandTests() => Directory.CreateDirectory(_home);
    public void Dispose() => Directory.Delete(_home, true);

    private static DoctorProbes Probes(bool installed) => new(
        Which: n => installed ? "/usr/bin/" + n : null,
        FileExists: _ => false,
        ReadFile: f => f == "/etc/os-release" ? "ID=arch" : null,
        FilesIn: _ => [],
        Run: (_, _) => new ProcessResult(0, "OBS Studio - 32.0.2 (linux)", ""),
        Env: _ => null,
        ObsLaunch: () => installed ? new ObsLaunch("obs", []) : throw new InvalidOperationException("OBS is not installed"),
        ObsRunning: () => false,
        FileVersion: _ => null);

    [Fact]
    public void Prints_a_line_per_check_and_exits_1_with_the_fix_when_something_is_missing()
    {
        if (!OperatingSystem.IsLinux()) return;
        var o = new StringWriter();
        var code = Program.Run(["doctor"], o, new StringWriter(), _home, Probes(installed: false));
        var lines = o.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(1, code);
        Assert.Contains("✗ OBS installed: OBS is not installed", lines);
        Assert.Contains("  fix: sudo pacman -S obs-studio", lines);
        Assert.Contains("  fix: sudo pacman -S ffmpeg", lines);
        Assert.Contains(lines, l => l.StartsWith("✓ Speech model:", StringComparison.Ordinal));
    }

    [Fact]
    public void Exits_0_when_everything_is_ok()
    {
        if (!OperatingSystem.IsLinux()) return;
        var o = new StringWriter();
        var code = Program.Run(["doctor"], o, new StringWriter(), _home, Probes(installed: true));
        Assert.Equal(0, code);
        Assert.DoesNotContain("✗", o.ToString());
        Assert.Contains("✓ OBS version: 32.0.2", o.ToString());
    }

    [Fact]
    public void Extra_arguments_are_a_usage_error()
    {
        var e = new StringWriter();
        Assert.Equal(2, Program.Run(["doctor", "x"], new StringWriter(), e, _home, Probes(true)));
        Assert.Contains("usage: sakshi doctor", e.ToString());
    }
}
