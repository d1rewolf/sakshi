using Sakshi.Core.Config;
using Sakshi.Core.Paths;
using Sakshi.Platform.Doctor;

namespace Sakshi.Platform.Tests;

public sealed class DoctorTests
{
    private const string Home = "/h";
    private static readonly SakshiPaths Paths = SakshiPaths.Resolve(HostOs.Linux, Home, _ => null);

    private static DoctorProbes Nothing(Dictionary<string, string>? files = null, Dictionary<string, string?>? env = null) => new(
        Which: _ => null,
        // Path.Combine uses backslashes on Windows, where the macOS and Linux cases run too: compare with slashes.
        FileExists: f => files?.ContainsKey(Slashes(f)) ?? false,
        ReadFile: f => files is not null && files.TryGetValue(Slashes(f), out var t) ? t : null,
        FilesIn: _ => [],
        Run: (f, _) => throw new System.ComponentModel.Win32Exception(2, $"{f} not found"),
        Env: n => env is not null && env.TryGetValue(n, out var v) ? v : null,
        ObsLaunch: () => throw new InvalidOperationException("OBS is not installed"),
        ObsRunning: () => false,
        FileVersion: _ => null);

    private static string Slashes(string path) => path.Replace('\\', '/');

    private static IReadOnlyList<DoctorCheck> Run(HostOs os, DoctorProbes p) =>
        Doctor.Doctor.Run(os, SakshiConfig.Defaults(os, Home), Paths, p);

    private static DoctorCheck Get(IReadOnlyList<DoctorCheck> checks, string name) => checks.Single(c => c.Name == name);

    [Fact]
    public void Windows_with_nothing_installed_reports_every_check_with_its_fix()
    {
        var checks = Run(HostOs.Windows, Nothing());
        Assert.Equal(["OBS installed", "OBS version", "OBS WebSocket", "ffmpeg", "Speech model", "VC++ runtime"],
            checks.Select(c => c.Name));
        Assert.Equal("winget install OBSProject.OBSStudio", Get(checks, "OBS installed").Fix);
        Assert.Equal("winget install Gyan.FFmpeg", Get(checks, "ffmpeg").Fix);
        Assert.Equal("not found on PATH; after installing it, quit and restart Sakshi", Get(checks, "ffmpeg").Detail);
        Assert.Equal("winget install Microsoft.VCRedist.2015+.x64", Get(checks, "VC++ runtime").Fix);
        Assert.False(Get(checks, "OBS version").Ok);
    }

    [Fact]
    public void Mac_with_nothing_installed()
    {
        var checks = Run(HostOs.MacOS, Nothing());
        Assert.Equal("brew install --cask obs", Get(checks, "OBS installed").Fix);
        Assert.Equal("brew install ffmpeg", Get(checks, "ffmpeg").Fix);
        Assert.True(Get(checks, "Screen Recording").Ok);
        Assert.Contains("Screen & System Audio Recording", Get(checks, "Screen Recording").Detail);
        Assert.True(Get(checks, "Microphone").Ok);
    }

    [Fact]
    public void Mac_finds_ffmpeg_in_the_homebrew_dirs()
    {
        var checks = Run(HostOs.MacOS, Nothing(new() { ["/opt/homebrew/bin/ffmpeg"] = "" }));
        Assert.True(Get(checks, "ffmpeg").Ok);
    }

    [Theory]
    [InlineData("ID=ubuntu\nID_LIKE=debian", "sudo add-apt-repository ppa:obsproject/obs-studio && sudo apt install obs-studio", "sudo apt install ffmpeg")]
    [InlineData("ID=linuxmint\nID_LIKE=\"ubuntu debian\"", "sudo add-apt-repository ppa:obsproject/obs-studio && sudo apt install obs-studio", "sudo apt install ffmpeg")]
    [InlineData("ID=arch", "sudo pacman -S obs-studio", "sudo pacman -S ffmpeg")]
    [InlineData("ID=omarchy\nID_LIKE=arch", "sudo pacman -S obs-studio", "sudo pacman -S ffmpeg")]
    public void Linux_fix_lines_follow_the_distro(string osRelease, string obs, string ffmpeg)
    {
        var checks = Run(HostOs.Linux, Nothing(new() { ["/etc/os-release"] = osRelease }));
        Assert.Equal(obs, Get(checks, "OBS installed").Fix);
        Assert.Equal(ffmpeg, Get(checks, "ffmpeg").Fix);
    }

    [Fact]
    public void Linux_of_an_unknown_distro_names_both_in_the_detail()
    {
        var ffmpeg = Get(Run(HostOs.Linux, Nothing(new() { ["/etc/os-release"] = "ID=fedora" })), "ffmpeg");
        Assert.Null(ffmpeg.Fix);
        Assert.EndsWith("(Ubuntu: sudo apt install ffmpeg; Arch: sudo pacman -S ffmpeg)", ffmpeg.Detail);
    }

    [Fact]
    public void Gnome_adds_the_tray_icon_check_and_fails_without_the_extension()
    {
        var checks = Run(HostOs.Linux, Nothing(new() { ["/etc/os-release"] = "ID=ubuntu" }, new() { ["XDG_CURRENT_DESKTOP"] = "ubuntu:GNOME" }));
        var tray = Get(checks, "Tray icon support");
        Assert.False(tray.Ok);
        Assert.Equal("gnome-extensions enable ubuntu-appindicators@ubuntu.com", tray.Fix);
    }

    [Fact]
    public void Other_desktops_have_no_tray_icon_check() =>
        Assert.DoesNotContain(Run(HostOs.Linux, Nothing()), c => c.Name == "Tray icon support");

    [Fact]
    public void An_active_appindicator_passes()
    {
        var p = Nothing(env: new() { ["XDG_CURRENT_DESKTOP"] = "GNOME" }) with
        {
            Run = (_, _) => new ProcessResult(0, "Name: AppIndicator\n  State: ACTIVE\n", ""),
        };
        Assert.True(Get(Run(HostOs.Linux, p), "Tray icon support").Ok);
    }

    [Fact]
    public void A_throwing_probe_becomes_could_not_check_and_the_rest_still_run()
    {
        var p = Nothing() with { Which = _ => throw new IOException("disk on fire") };
        var checks = Run(HostOs.Linux, p);
        Assert.Equal("could not check (disk on fire)", Get(checks, "ffmpeg").Detail);
        Assert.False(Get(checks, "ffmpeg").Ok);
        Assert.Equal(5, checks.Count);
    }

    private static DoctorProbes WithObs(ObsLaunch launch, Func<string, IReadOnlyList<string>, ProcessResult>? run = null,
        Dictionary<string, string>? files = null, string? fileVersion = null) =>
        Nothing(files) with
        {
            ObsLaunch = () => launch,
            Run = run ?? ((f, _) => throw new System.ComponentModel.Win32Exception(2, f)),
            FileVersion = _ => fileVersion,
        };

    [Theory]
    [InlineData("OBS Studio - 32.0.2 (linux)", true)]
    [InlineData("OBS Studio - 31.1.0 (linux)", false)]
    public void Linux_version_comes_from_obs_dash_dash_version(string output, bool ok)
    {
        var p = WithObs(new ObsLaunch("obs", []), (f, a) => f == "obs" && a is ["--version"] ? new ProcessResult(0, output, "") : throw new InvalidOperationException());
        var v = Get(Run(HostOs.Linux, p), "OBS version");
        Assert.Equal(ok, v.Ok);
        if (!ok) Assert.Contains("OBS 32 or newer is needed", v.Detail);
    }

    [Fact]
    public void Flatpak_version_comes_from_the_Version_line_not_the_commit_hash()
    {
        var info = "OBS Studio - Live streaming\n\n          ID: com.obsproject.Studio\n      Commit: 1234567890abcdef\n     Version: 32.0.4\n";
        var p = WithObs(new ObsLaunch("flatpak", ["run", "com.obsproject.Studio"]), (_, _) => new ProcessResult(0, info, ""));
        Assert.Equal("32.0.4", Get(Run(HostOs.Linux, p), "OBS version").Detail);
    }

    [Fact]
    public void Mac_version_comes_from_the_bundle_plist()
    {
        var plist = "<dict><key>CFBundleShortVersionString</key>\n\t<string>30.2.3</string></dict>";
        var p = WithObs(new ObsLaunch("open", ["-g", "-a", "/Applications/OBS.app", "--args"]),
            files: new() { ["/Applications/OBS.app/Contents/Info.plist"] = plist });
        var v = Get(Run(HostOs.MacOS, p), "OBS version");
        Assert.False(v.Ok);
        Assert.Equal("brew upgrade --cask obs", v.Fix);
    }

    [Fact]
    public void Windows_version_comes_from_the_exe()
    {
        var p = WithObs(new ObsLaunch(@"C:\obs\bin\64bit\obs64.exe", []), fileVersion: "33.0.1");
        var v = Get(Run(HostOs.Windows, p), "OBS version");
        Assert.True(v.Ok);
        Assert.Equal("33.0.1", v.Detail);
    }

    private static string WsFile => Path.Combine(Paths.ObsConfigDir, "plugin_config", "obs-websocket", "config.json");

    [Fact]
    public void WebSocket_off_while_obs_runs_fails_with_todays_message()
    {
        var p = Nothing(new() { [Slashes(WsFile)] = "{\"server_enabled\": false}" }) with { ObsRunning = () => true };
        var ws = Get(Run(HostOs.Linux, p), "OBS WebSocket");
        Assert.False(ws.Ok);
        Assert.Contains("Tools → WebSocket Server Settings → Enable", ws.Detail);
    }

    [Theory]
    [InlineData("{\"server_enabled\": false}", false)]   // OBS closed: sakshi turns it on
    [InlineData("{\"server_enabled\": true}", true)]
    public void WebSocket_is_fine_when_enabled_or_obs_is_closed(string json, bool running)
    {
        var p = Nothing(new() { [Slashes(WsFile)] = json }) with { ObsRunning = () => running };
        Assert.True(Get(Run(HostOs.Linux, p), "OBS WebSocket").Ok);
    }

    [Fact]
    public void Speech_model_is_always_ok_and_says_what_will_happen()
    {
        var none = Get(Run(HostOs.Linux, Nothing()), "Speech model");
        Assert.True(none.Ok);
        Assert.Equal("downloads about 150 MB on first transcription", none.Detail);

        var have = Get(Run(HostOs.Linux, Nothing() with { FilesIn = _ => ["/m/ggml-small.bin"] }), "Speech model");
        Assert.Equal("/m/ggml-small.bin", have.Detail);
    }

    [Fact]
    public void Windows_vc_runtime_needs_both_dlls()
    {
        var one = Nothing(new() { [Slashes(Path.Combine(@"C:\W", "System32", "vcruntime140.dll"))] = "" },
            new() { ["SystemRoot"] = @"C:\W" });
        var v = Get(Run(HostOs.Windows, one), "VC++ runtime");
        Assert.False(v.Ok);
        Assert.Contains("msvcp140.dll", v.Detail);
    }

    private static readonly Dictionary<string, string?> Hyprland = new() { ["HYPRLAND_INSTANCE_SIGNATURE"] = "sig", ["HOME"] = Home };

    [Fact]
    public void Fixes_are_commands_that_paste_as_they_are()
    {
        // The setup window copies Fix to the clipboard: advice must not ride along.
        foreach (var os in new[] { HostOs.Windows, HostOs.MacOS, HostOs.Linux })
            foreach (var c in Run(os, Nothing(env: Hyprland)).Where(c => c.Fix is not null))
                Assert.DoesNotContain(", then", c.Fix);
    }

    [Fact]
    public void Mac_permissions_each_link_to_their_own_privacy_settings()
    {
        var checks = Run(HostOs.MacOS, Nothing());
        Assert.Equal("x-apple.systempreferences:com.apple.preference.security?Privacy_ScreenCapture", Get(checks, "Screen Recording").SettingsUrl);
        Assert.Equal("x-apple.systempreferences:com.apple.preference.security?Privacy_Microphone", Get(checks, "Microphone").SettingsUrl);
    }

    [Fact]
    public void Gnome_outside_ubuntu_points_at_the_extension_instead_of_a_command()
    {
        var checks = Run(HostOs.Linux, Nothing(new() { ["/etc/os-release"] = "ID=fedora" }, new() { ["XDG_CURRENT_DESKTOP"] = "GNOME" }));
        var tray = Get(checks, "Tray icon support");
        Assert.False(tray.Ok);
        Assert.Null(tray.Fix);
        Assert.Contains("extensions.gnome.org/extension/615", tray.Detail);
    }

    [Fact]
    public void Obs_is_looked_for_once_per_run()
    {
        var calls = 0;
        var p = Nothing() with { ObsLaunch = () => { calls++; throw new InvalidOperationException("OBS is not installed"); } };
        var checks = Run(HostOs.Linux, p);
        Assert.Equal(1, calls);
        Assert.False(Get(checks, "OBS installed").Ok);
        Assert.False(Get(checks, "OBS version").Ok);
    }

    [Fact]
    public void Hyprland_checks_appear_only_under_hyprland()
    {
        Assert.DoesNotContain(Run(HostOs.Linux, Nothing()), c => c.Name is "Share picker" or "Keyboard shortcut");
        var checks = Run(HostOs.Linux, Nothing(env: Hyprland));
        Assert.Contains(checks, c => c.Name == "Share picker");
        Assert.True(Get(checks, "Keyboard shortcut").Ok);
        Assert.Contains("sakshi toggle", Get(checks, "Keyboard shortcut").Detail);
    }

    private static DoctorProbes WithPreviewPicker(Dictionary<string, string>? files = null) =>
        Nothing(files, Hyprland) with { Which = n => n == "hyprland-preview-share-picker" ? "/usr/bin/" + n : null };

    [Fact]
    public void Share_picker_with_no_screencopy_section_gets_one_command_that_does_everything()
    {
        var check = Get(Run(HostOs.Linux, WithPreviewPicker()), "Share picker");
        Assert.False(check.Ok);
        Assert.False(check.Required);
        Assert.Equal(
            "mkdir -p \"/h/.local/bin\" && printf '#!/bin/sh\\nexec \"%s\" share-picker \"$@\"\\n' \"sakshi\" > \"/h/.local/bin/sakshi-share-picker\" && chmod +x \"/h/.local/bin/sakshi-share-picker\""
            + " && mkdir -p \"/h/.config/hypr\" && printf '\\nscreencopy {\\n    custom_picker_binary = %s\\n}\\n' \"/h/.local/bin/sakshi-share-picker\" >> \"/h/.config/hypr/xdph.conf\""
            + " && systemctl --user restart xdg-desktop-portal-hyprland",
            check.Fix);
    }

    [Fact]
    public void Share_picker_with_a_screencopy_section_makes_the_wrapper_and_explains_the_edit()
    {
        var files = new Dictionary<string, string> { ["/h/.config/hypr/xdph.conf"] = "screencopy {\n  allow_token_by_default = true\n}\n" };
        var check = Get(Run(HostOs.Linux, WithPreviewPicker(files)), "Share picker");
        Assert.DoesNotContain(">>", check.Fix);
        Assert.Contains("custom_picker_binary = /h/.local/bin/sakshi-share-picker", check.Detail);
        Assert.Contains("systemctl --user restart xdg-desktop-portal-hyprland", check.Detail);
    }

    [Fact]
    public void Share_picker_without_the_preview_picker_offers_no_command()
    {
        // sakshi's picker hands off to it: pointing the portal at sakshi without it would break screen sharing.
        var check = Get(Run(HostOs.Linux, Nothing(env: Hyprland)), "Share picker");
        Assert.Null(check.Fix);
        Assert.Contains("hyprland-preview-share-picker", check.Detail);
    }

    [Theory]
    [InlineData("screencopy {\n    custom_picker_binary = /h/bin/sakshi-share-picker\n}\n", null)]
    [InlineData("screencopy {\n  custom_picker_binary=~/bin/picker\n}\n", "#!/bin/sh\nexec /opt/sakshi share-picker \"$@\"\n")]
    public void Share_picker_set_to_a_sakshi_wrapper_passes(string conf, string? script)
    {
        var files = new Dictionary<string, string> { ["/h/.config/hypr/xdph.conf"] = conf };
        if (script is not null) files["/h/bin/picker"] = script;
        Assert.True(Get(Run(HostOs.Linux, Nothing(files, Hyprland)), "Share picker").Ok);
    }

    [Fact]
    public void Share_picker_set_to_another_picker_fails()
    {
        var files = new Dictionary<string, string>
        {
            ["/h/.config/hypr/xdph.conf"] = "screencopy {\n  custom_picker_binary = hyprland-preview-share-picker\n}\n",
        };
        Assert.False(Get(Run(HostOs.Linux, Nothing(files, Hyprland)), "Share picker").Ok);
    }
}
