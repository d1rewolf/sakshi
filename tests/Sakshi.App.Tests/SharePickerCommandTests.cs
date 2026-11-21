using Sakshi.Platform;
using Sakshi.TestSupport;

namespace Sakshi.App.Tests;

[Collection("Environment")]
public sealed class SharePickerCommandTests : IDisposable
{
    private readonly TempHome _home = new();
    private readonly string? _savedList = Environment.GetEnvironmentVariable("XDPH_WINDOW_SHARING_LIST");

    public SharePickerCommandTests() => Environment.SetEnvironmentVariable("XDPH_WINDOW_SHARING_LIST", null);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("XDPH_WINDOW_SHARING_LIST", _savedList);
        _home.Dispose();
    }

    private async Task<(int Code, string Out, string Err)> Run(FakeProcessRunner runner, params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var launch = new TrayLaunch(runner, "/opt/sakshi/sakshi", TimeSpan.FromMilliseconds(50), TimeSpan.FromSeconds(1));
        var code = await Commands.RunAsync(args, o, e, _home.Home, isTty: false, TestContext.Current.CancellationToken, launch);
        return (code, o.ToString(), e.ToString());
    }

    [Fact]
    public async Task SharePicker_PassesEverythingAfterTheCommandToThePicker_AndReturnsItsExitCode()
    {
        if (!OperatingSystem.IsLinux()) Assert.Skip("share-picker runs on Linux only");
        var runner = new FakeProcessRunner { OnAttached = (_, _, _) => 4 };
        var (code, o, _) = await Run(runner, "share-picker", "--allow-token", "-x", "a b");
        Assert.Equal(4, code);
        Assert.Equal("", o);
        var call = Assert.Single(runner.Attached);
        Assert.Equal("hyprland-preview-share-picker", call.File);
        Assert.Equal(["--allow-token", "-x", "a b"], call.Args);
        Assert.Empty(runner.Calls);
        Assert.Empty(runner.Detached);
    }

    [Fact]
    public async Task SharePicker_OffLinux_PrintsOneLineAndExits2()
    {
        if (OperatingSystem.IsLinux()) Assert.Skip("checks the non-Linux refusal");
        var runner = new FakeProcessRunner();
        var (code, o, e) = await Run(runner, "share-picker");
        Assert.Equal(2, code);
        Assert.Equal("", o);
        Assert.Single(e.TrimEnd().Split('\n'));
        Assert.Empty(runner.Attached);
    }

    [Fact]
    public async Task SharePicker_BrokenConfig_StillRunsPickerWithDefaults()
    {
        if (!OperatingSystem.IsLinux()) Assert.Skip("share-picker runs on Linux only");
        File.WriteAllText(Path.Combine(_home.ConfigDir, "config.json"), "{ not json");
        var runner = new FakeProcessRunner();
        var (code, _, e) = await Run(runner, "share-picker", "--allow-token");
        Assert.Equal(0, code);
        Assert.NotEmpty(e);
        var call = Assert.Single(runner.Attached);
        Assert.Equal("hyprland-preview-share-picker", call.File);
        Assert.Equal(["--allow-token"], call.Args);
    }
}
