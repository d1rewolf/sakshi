using Sakshi.Platform;
using Sakshi.Platform.MacOS;

namespace Sakshi.Platform.Tests;

public sealed class MacObsInstallTests
{
    private static readonly string[] Extra = ["--minimize-to-tray"];

    [Fact]
    public async Task Mac_prefers_applications_then_home_applications()
    {
        var home = Path.Combine("/Users", "jo");
        var bundle = Path.Combine(home, "Applications", "OBS.app");
        var install = new MacObsInstall(home, p => p == bundle);
        Assert.Equal("OBS", install.ProcessName);
        var l = await install.LaunchAsync(Extra, TestContext.Current.CancellationToken);
        Assert.Equal("open", l.File);
        Assert.Equal(["-g", "-a", bundle, "--args", "--minimize-to-tray"], l.Args);
        Assert.Null(l.WorkingDirectory);

        var system = new MacObsInstall(home, p => p == "/Applications/OBS.app" || p == bundle);
        Assert.Equal("/Applications/OBS.app", (await system.LaunchAsync(Extra, TestContext.Current.CancellationToken)).Args[2]);
    }

    [Fact]
    public async Task Mac_without_obs_says_where_to_get_it()
    {
        var install = new MacObsInstall("/Users/jo", _ => false);
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => install.LaunchAsync(Extra, TestContext.Current.CancellationToken));
        Assert.Contains("https://obsproject.com", e.Message);
    }
}
