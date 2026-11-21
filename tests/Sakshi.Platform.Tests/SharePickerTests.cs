using Sakshi.Core.Config;
using Sakshi.Platform;
using Sakshi.Platform.Linux;
using Sakshi.TestSupport;

namespace Sakshi.Platform.Tests;

/// <summary>Ports test/picker-restores-titles.sh and the wrapper's other behaviour. Never runs hyprctl or a picker.</summary>
[Collection("Environment")]
public sealed class SharePickerTests : IDisposable
{
    private const string Hyprctl = """
        [{"address":"0x559fffb903c0","class":"chrome-app.zoom.us__wc_join-Profile_DefaultPro","title":"SEDemo3's Personal Meeting Room"},
         {"address":"0x559fffb17f90","class":"chromium","title":"Reddit - The heart of the internet - Chromium"},
         {"address":"0x559fffb11111","class":"com.mitchellh.ghostty","title":"vpn"}]
        """;

    // The list exactly as xdph builds it: the apostrophe is already a space, and one entry's address is unknown to hyprctl.
    private const string XdphList =
        "1[HC>]com.mitchellh.ghostty[HT>]vpn[HE>]94145677955345[HA>]" +
        "2[HC>]chromium[HT>]Reddit - The heart of the internet - Chromium[HE>]94145677983632[HA>]" +
        "3[HC>]chrome-app.zoom.us__wc_join-Profile_DefaultPro[HT>]SEDemo3 s Personal Meeting Room[HE>]94145678476224[HA>]" +
        "4[HC>]foo[HT>]it s gone[HE>]12345[HA>]";

    private readonly TempHome _home = new();
    private readonly FakeProcessRunner _runner = new();
    private readonly string _runtime;

    public SharePickerTests()
    {
        _runtime = Path.Combine(_home.Root, "run");
        Directory.CreateDirectory(_runtime);
        _runner.OnRun = (f, _) => f == "hyprctl" ? new ProcessResult(0, Hyprctl, "") : new ProcessResult(1, "", "");
    }

    public void Dispose() => _home.Dispose();

    private Func<string, string?> Env(string? list) => name => name switch
    {
        "XDPH_WINDOW_SHARING_LIST" => list,
        "XDG_RUNTIME_DIR" => _runtime,
        _ => null,
    };

    private static SakshiConfig Cfg(string realBin = "fake-picker", string page = "", string? regex = null) =>
        regex is null
            ? new SakshiConfig { PickerRealBin = realBin, PickerDefaultPage = page }
            : new SakshiConfig { PickerRealBin = realBin, PickerDefaultPage = page, PickerPriorityRegex = regex };

    private async Task<(int Code, string List)> Run(string? list, SakshiConfig? cfg = null, params string[] args)
    {
        var code = await SharePicker.RunAsync(args, cfg ?? Cfg(), _runner, Env(list), _home.Home, TestContext.Current.CancellationToken);
        var call = Assert.Single(_runner.Attached);
        return (code, call.Env.GetValueOrDefault("XDPH_WINDOW_SHARING_LIST") ?? "");
    }

    [Fact]
    public async Task SharePicker_RestoresApostropheFromHyprctl()
    {
        var (_, list) = await Run(XdphList);
        Assert.Contains("[HT>]SEDemo3's Personal Meeting Room[HE>]94145678476224[HA>]", list);
    }

    [Fact]
    public async Task SharePicker_UnknownAddressPassesThroughUntouched()
    {
        var (_, list) = await Run(XdphList);
        Assert.Contains("4[HC>]foo[HT>]it s gone[HE>]12345[HA>]", list);
    }

    [Fact]
    public async Task SharePicker_MeetingWindowIsLast_AndAllFourEntriesKept()
    {
        var (_, list) = await Run(XdphList);
        Assert.EndsWith("Personal Meeting Room[HE>]94145678476224[HA>]", list);
        Assert.Equal(4, list.Split("[HA>]").Length - 1);
    }

    [Fact]
    public async Task SharePicker_ListIsReversedAfterMeetingsFirst()
    {
        var (_, list) = await Run(XdphList);
        // display order: zoom, ghostty, chromium, foo -> handed over reversed
        var ids = list.Split("[HA>]", StringSplitOptions.RemoveEmptyEntries).Select(e => e[..1]).ToArray();
        Assert.Equal(["4", "2", "1", "3"], ids);
    }

    [Fact]
    public async Task SharePicker_PriorityRegexIsCaseInsensitive_AndMatchesTitle()
    {
        var list = "1[HC>]alpha[HT>]one[HE>]1[HA>]2[HC>]beta[HT>]My TEAMS call[HE>]2[HA>]3[HC>]ZOOM.US[HT>]x[HE>]3[HA>]4[HC>]gamma[HT>]four[HE>]4[HA>]";
        var (_, got) = await Run(list);
        var ids = got.Split("[HA>]", StringSplitOptions.RemoveEmptyEntries).Select(e => e[..1]).ToArray();
        Assert.Equal(["4", "1", "3", "2"], ids); // shown: 2, 3, 1, 4
    }

    [Fact]
    public async Task SharePicker_CustomPriorityRegexFromConfig()
    {
        var list = "1[HC>]alpha[HT>]one[HE>]1[HA>]2[HC>]beta[HT>]two[HE>]2[HA>]";
        var (_, got) = await Run(list, Cfg(regex: "BETA"));
        Assert.Equal(["1", "2"], got.Split("[HA>]", StringSplitOptions.RemoveEmptyEntries).Select(e => e[..1]).ToArray());
    }

    [Fact]
    public async Task SharePicker_InvalidPriorityRegex_PassesTheRawListUntouched()
    {
        // Bash parity: when the ordering fails the list stays as xdph built it (not restored, not reversed).
        var (_, list) = await Run(XdphList, Cfg(regex: "("));
        Assert.Equal(XdphList, list);
    }

    [Fact]
    public async Task SharePicker_ChangedTitleIsNotRestored()
    {
        var list = "3[HC>]chrome-app.zoom.us__wc_join-Profile_DefaultPro[HT>]Another title[HE>]94145678476224[HA>]";
        var (_, got) = await Run(list);
        Assert.Equal(list, got);
    }

    [Fact]
    public async Task SharePicker_RestoresTypographicApostropheAndClosingBracket()
    {
        _runner.OnRun = (_, _) => new ProcessResult(0,
            """[{"address":"0xa","class":"c","title":"Bob’s <b>] list"}]""", "");
        // portal: ’ -> space and the ] after > -> space
        var list = "1[HC>]c[HT>]Bob s <b>  list[HE>]10[HA>]";
        var (_, got) = await Run(list);
        Assert.Equal("1[HC>]c[HT>]Bob’s <b>] list[HE>]10[HA>]", got);
    }

    [Fact]
    public async Task SharePicker_HyprctlFailure_StillReorders()
    {
        _runner.OnRun = (_, _) => throw new TimeoutException("hyprctl");
        var (_, got) = await Run(XdphList);
        Assert.Contains("SEDemo3 s Personal Meeting Room", got);
        Assert.EndsWith("94145678476224[HA>]", got);
    }

    [Fact]
    public async Task SharePicker_HyprctlCalledWithJsonClients()
    {
        await Run(XdphList);
        var call = Assert.Single(_runner.Calls);
        Assert.Equal("hyprctl", call.File);
        Assert.Equal(["-j", "clients"], call.Args);
    }

    [Fact]
    public async Task SharePicker_WritesDebugTrailInDisplayOrder()
    {
        await Run(XdphList);
        var lines = File.ReadAllLines(Path.Combine(_runtime, "sakshi-share-picker.last"));
        // Trail lists the (reversed) handed-over list tac'd, i.e. display order.
        Assert.Equal([
            "chrome-app.zoom.us__wc_join-Profile_DefaultPro | SEDemo3's Personal Meeting Room",
            "com.mitchellh.ghostty | vpn",
            "chromium | Reddit - The heart of the internet - Chromium",
            "foo | it s gone"], lines);
    }

    [Fact]
    public async Task SharePicker_UnsetList_RunsRealPickerUnchanged()
    {
        var code = await SharePicker.RunAsync(["--allow-token"], Cfg(), _runner, Env(null), _home.Home, TestContext.Current.CancellationToken);
        Assert.Equal(0, code);
        Assert.Empty(_runner.Calls);
        var call = Assert.Single(_runner.Attached);
        Assert.Equal("fake-picker", call.File);
        Assert.Equal(["--allow-token"], call.Args);
        Assert.Equal(["SAKSHI_SHARE_PICKER_ACTIVE"], call.Env.Keys);   // only the self-guard marker
        Assert.False(File.Exists(Path.Combine(_runtime, "sakshi-share-picker.last")));
    }

    [Fact]
    public async Task SharePicker_ReturnsPickerExitCode_AndPassesArgs()
    {
        _runner.OnAttached = (_, _, _) => 7;
        var code = await SharePicker.RunAsync(["--allow-token", "x y"], Cfg(), _runner, Env(XdphList), _home.Home, TestContext.Current.CancellationToken);
        Assert.Equal(7, code);
        Assert.Equal(["--allow-token", "x y"], _runner.Attached[0].Args);
    }

    private string PickerDir => Path.Combine(_home.Home, ".config", "hyprland-preview-share-picker");

    private void WritePickerConfig(string text)
    {
        Directory.CreateDirectory(PickerDir);
        File.WriteAllText(Path.Combine(PickerDir, "config.yaml"), text);
    }

    [Fact]
    public async Task SharePicker_DefaultPage_ReplacesKeyInSiblingConfig()
    {
        WritePickerConfig("stylesheet: style.css\ndefault_page: outputs\nother: 1\n");
        await SharePicker.RunAsync(["--allow-token"], Cfg(page: "region"), _runner, Env(null), _home.Home, TestContext.Current.CancellationToken);
        var sib = Path.Combine(PickerDir, "sakshi.yaml");
        Assert.Equal("stylesheet: style.css\ndefault_page: region\nother: 1\n", File.ReadAllText(sib));
        Assert.Equal(["--config", sib, "--allow-token"], _runner.Attached[0].Args);
        Assert.Equal("default_page: outputs", File.ReadAllLines(Path.Combine(PickerDir, "config.yaml"))[1]);
    }

    [Fact]
    public async Task SharePicker_DefaultPage_AppendsWhenMissing()
    {
        WritePickerConfig("stylesheet: style.css\n");
        await SharePicker.RunAsync([], Cfg(page: "windows"), _runner, Env(null), _home.Home, TestContext.Current.CancellationToken);
        Assert.Equal("stylesheet: style.css\ndefault_page: windows\n", File.ReadAllText(Path.Combine(PickerDir, "sakshi.yaml")));
    }

    [Theory]
    [InlineData("-c")]
    [InlineData("--config")]
    [InlineData("--config=/x.yaml")]
    public async Task SharePicker_DefaultPage_SkippedWhenArgsHaveConfig(string flag)
    {
        WritePickerConfig("a: 1\n");
        await SharePicker.RunAsync([flag, "/x.yaml"], Cfg(page: "windows"), _runner, Env(null), _home.Home, TestContext.Current.CancellationToken);
        Assert.False(File.Exists(Path.Combine(PickerDir, "sakshi.yaml")));
        Assert.Equal([flag, "/x.yaml"], _runner.Attached[0].Args);
    }

    [Fact]
    public async Task SharePicker_DefaultPage_SkippedWithoutPickerConfigOrEmptyPage()
    {
        await SharePicker.RunAsync([], Cfg(page: "windows"), _runner, Env(null), _home.Home, TestContext.Current.CancellationToken);
        Assert.Empty(_runner.Attached[0].Args);
        WritePickerConfig("a: 1\n");
        await SharePicker.RunAsync([], Cfg(page: ""), _runner, Env(null), _home.Home, TestContext.Current.CancellationToken);
        Assert.Empty(_runner.Attached[1].Args);
        Assert.False(File.Exists(Path.Combine(PickerDir, "sakshi.yaml")));
    }

    [Theory]
    [InlineData("sakshi-share-picker")]
    [InlineData("/home/u/bin/sakshi-share-picker")]
    public async Task SharePicker_RefusesToExecItself(string realBin)
    {
        await SharePicker.RunAsync([], Cfg(realBin), _runner, Env(null), _home.Home, TestContext.Current.CancellationToken);
        Assert.Equal("hyprland-preview-share-picker", _runner.Attached[0].File);
    }

    [Fact]
    public async Task SharePicker_MarksItsChild_SoAWrapperLoopIsCaught()
    {
        await Run(XdphList);
        Assert.Equal("1", _runner.Attached[0].Env["SAKSHI_SHARE_PICKER_ACTIVE"]);
    }

    [Fact]
    public async Task SharePicker_ReachedAgainThroughAWrapper_RunsTheDefaultPickerWithTheListAsItIs()
    {
        // pickerRealBin is some wrapper (any name) that ends up running sakshi share-picker again.
        var err = new StringWriter();
        Func<string, string?> env = n => n == "SAKSHI_SHARE_PICKER_ACTIVE" ? "1" : Env(XdphList)(n);
        var code = await SharePicker.RunAsync(["--x"], Cfg("my-picker-wrapper"), _runner, env, _home.Home,
            TestContext.Current.CancellationToken, err);

        Assert.Equal(0, code);
        Assert.Equal("sakshi share-picker: pickerRealBin points back at sakshi; using hyprland-preview-share-picker"
            + Environment.NewLine, err.ToString());
        var call = Assert.Single(_runner.Attached);
        Assert.Equal("hyprland-preview-share-picker", call.File);
        Assert.Equal(["--x"], call.Args);
        Assert.Empty(call.Env);                         // the list it inherits is already reordered: not again
        Assert.Empty(_runner.Calls);                    // no second hyprctl lookup
    }

    [Fact]
    public async Task SharePicker_MissingPicker_Exits127WithMessage()
    {
        _runner.OnAttached = (_, _, _) => throw new System.ComponentModel.Win32Exception(2);
        var err = new StringWriter();
        var code = await SharePicker.RunAsync([], Cfg("nope"), _runner, Env(null), _home.Home, TestContext.Current.CancellationToken, err);
        Assert.Equal(127, code);
        Assert.Equal("sakshi share-picker: nope: not found" + Environment.NewLine, err.ToString());
    }
}
