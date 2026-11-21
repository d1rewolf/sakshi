namespace Sakshi.App.Tests;

public sealed class TrayLaunchTests
{
    private static readonly string Exe = Path.Combine("app", "current", "sakshi.exe");
    private static readonly string Tray = Path.Combine("app", "current", "sakshiw.exe");

    [Fact]
    public void Windows_prefers_sakshiw_beside_the_running_exe_when_it_exists() =>
        Assert.Equal(Tray, TrayLaunch.TrayPath(true, Exe, p => p == Tray));

    [Fact]
    public void Windows_falls_back_to_the_running_exe_when_sakshiw_is_missing() =>
        Assert.Equal(Exe, TrayLaunch.TrayPath(true, Exe, _ => false));

    [Fact]
    public void Other_platforms_use_the_running_exe_without_looking() =>
        Assert.Equal("/opt/sakshi/sakshi", TrayLaunch.TrayPath(false, "/opt/sakshi/sakshi", _ => throw new InvalidOperationException()));

    [Fact]
    public void Without_a_process_path_it_is_sakshi() =>
        Assert.Equal("sakshi", TrayLaunch.TrayPath(false, null, _ => false));

    [Fact]
    public void Windows_without_a_process_path_is_sakshi() =>
        Assert.Equal("sakshi", TrayLaunch.TrayPath(true, null, _ => throw new InvalidOperationException()));
}
