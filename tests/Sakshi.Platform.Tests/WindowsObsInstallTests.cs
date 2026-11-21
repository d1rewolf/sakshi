using Sakshi.Platform;
using Sakshi.Platform.Windows;

namespace Sakshi.Platform.Tests;

public sealed class WindowsObsInstallTests
{
    private static readonly string[] Extra = ["--minimize-to-tray"];

    [Fact]
    public async Task Windows_uses_the_registry_dir_and_its_bin_folder_as_working_directory()
    {
        var dir = Path.Combine("C:", "Apps", "OBS");
        var bin = Path.Combine(dir, "bin", "64bit");
        var exe = Path.Combine(bin, "obs64.exe");
        var install = new WindowsObsInstall(() => dir, p => p == exe, _ => null);
        Assert.Equal("obs64", install.ProcessName);
        var l = await install.LaunchAsync(Extra, TestContext.Current.CancellationToken);
        Assert.Equal(exe, l.File);
        Assert.Equal(Extra, l.Args);
        Assert.Equal(bin, l.WorkingDirectory);
    }

    [Fact]
    public async Task Windows_falls_back_to_program_files()
    {
        var pf = Path.Combine("C:", "Program Files");
        var exe = Path.Combine(pf, "obs-studio", "bin", "64bit", "obs64.exe");
        var install = new WindowsObsInstall(() => null, p => p == exe, n => n == "ProgramFiles" ? pf : null);
        var l = await install.LaunchAsync(Extra, TestContext.Current.CancellationToken);
        Assert.Equal(exe, l.File);
        Assert.Equal(Path.GetDirectoryName(exe), l.WorkingDirectory);
    }

    [Fact]
    public async Task Windows_without_obs_says_where_to_get_it()
    {
        var install = new WindowsObsInstall(() => null, _ => false, _ => null);
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => install.LaunchAsync(Extra, TestContext.Current.CancellationToken));
        Assert.Contains("https://obsproject.com", e.Message);
    }
}
