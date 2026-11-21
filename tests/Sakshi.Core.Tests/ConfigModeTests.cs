using System.Runtime.Versioning;
using Sakshi.Core.Config;
using Sakshi.Core.Paths;

namespace Sakshi.Core.Tests;

public class ConfigModeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sakshi-mode-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private static SakshiConfig Cfg() => SakshiConfig.Defaults(HostOs.Linux, "/home/x");

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public void Save_KeepsExistingMode()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix file modes do not exist on Windows");
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "config.json");
        File.WriteAllText(path, "{}");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        ConfigStore.Save(path, Cfg());
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));

        File.SetUnixFileMode(path, (UnixFileMode)Convert.ToInt32("640", 8));
        ConfigStore.Save(path, Cfg());
        Assert.Equal((UnixFileMode)Convert.ToInt32("640", 8), File.GetUnixFileMode(path));
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public void Save_NewFileIs0600()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix file modes do not exist on Windows");
        var path = Path.Combine(_dir, "config.json");
        ConfigStore.Save(path, Cfg());
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }
}
