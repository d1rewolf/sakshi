using System.Text.RegularExpressions;
using Sakshi.Core.Config;
using Sakshi.Core.Paths;
using Sakshi.Platform.MacOS;

namespace Sakshi.Platform.Doctor;

/// <summary>What <c>sakshi doctor</c> and the tray's first start check: what is missing, and the command that fixes it.</summary>
public static class Doctor
{
    private const string AppIndicator = "ubuntu-appindicators@ubuntu.com";
    private const string FlatpakId = "com.obsproject.Studio";
    private const string DefaultModelFile = "ggml-base.en.bin";

    /// <summary>Every check, in order. Never throws: a probe that fails becomes "could not check".</summary>
    public static IReadOnlyList<DoctorCheck> Run(HostOs os, SakshiConfig config, SakshiPaths paths, DoctorProbes probes)
    {
        // Two checks ask where OBS is; finding it can run a command, so ask once.
        probes = probes with { ObsLaunch = Once(probes.ObsLaunch) };
        var checks = new List<(string Name, Func<DoctorCheck> Check)>
        {
            ("OBS installed", () => ObsInstalled(os, probes)),
            ("OBS version", () => ObsVersionCheck(os, probes)),
            ("OBS WebSocket", () => WebSocket(paths, probes)),
            ("ffmpeg", () => Ffmpeg(os, probes)),
            ("Speech model", () => SpeechModel(config, paths, probes)),
        };
        if (os == HostOs.Windows) checks.Add(("VC++ runtime", () => VcRuntime(probes)));
        if (os == HostOs.Linux && probes.Env("XDG_CURRENT_DESKTOP") is { } d && d.Contains("GNOME", StringComparison.OrdinalIgnoreCase))
            checks.Add(("Tray icon support", () => TrayIcon(probes)));
        if (os == HostOs.MacOS)
        {
            checks.Add(("Screen Recording", ScreenRecording));
            checks.Add(("Microphone", Microphone));
        }
        if (os == HostOs.Linux && !string.IsNullOrEmpty(probes.Env("HYPRLAND_INSTANCE_SIGNATURE")))
        {
            checks.Add(("Share picker", () => SharePicker(probes)));
            checks.Add(("Keyboard shortcut", () => KeyboardShortcut(probes)));
        }

        return checks.Select(c => Guarded(c.Name, c.Check)).ToList();
    }

    private static Func<ObsLaunch> Once(Func<ObsLaunch> probe)
    {
        var found = new Lazy<ObsLaunch>(probe); // a failure is kept too, and thrown again on each call
        return () => found.Value;
    }

    private static DoctorCheck Guarded(string name, Func<DoctorCheck> check)
    {
        try { return check(); }
        catch (Exception e) { return new DoctorCheck(name, false, $"could not check ({e.Message})", null); }
    }

    private static DoctorCheck ObsInstalled(HostOs os, DoctorProbes p)
    {
        try
        {
            var launch = p.ObsLaunch();
            return new DoctorCheck("OBS installed", true, ObsLocation(os, launch), null);
        }
        catch (InvalidOperationException e)
        {
            var fix = ObsFix(os, p);
            return new DoctorCheck("OBS installed", false, e.Message + (fix is null ? AnyDistro(UbuntuObs, ArchObs) : BrewNote(os)), fix);
        }
    }

    private static string ObsLocation(HostOs os, ObsLaunch launch) =>
        os == HostOs.MacOS ? MacBundle(launch) ?? launch.File : launch.File == "flatpak" ? "Flatpak " + FlatpakId : launch.File;

    private static DoctorCheck ObsVersionCheck(HostOs os, DoctorProbes p)
    {
        const string name = "OBS version";
        ObsLaunch launch;
        try { launch = p.ObsLaunch(); }
        catch (InvalidOperationException) { return new DoctorCheck(name, false, "unknown, OBS is not installed", null); }

        var text = os switch
        {
            HostOs.Windows => p.FileVersion(launch.File),
            HostOs.MacOS => MacBundle(launch) is { } b ? PlistVersion(p.ReadFile(Path.Combine(b, "Contents", "Info.plist"))) : null,
            _ => launch.File == "flatpak" ? FlatpakVersion(p) : p.Run("obs", ["--version"]).StdOut,
        };
        var version = text is null ? null : ObsVersion.Parse(text);
        // An unreadable version is not worth a false alarm on every start: say so, but pass.
        if (version is null)
            return new DoctorCheck(name, true, $"could not read it; OBS {ObsVersion.MinimumMajor} or newer is needed", null);
        if (version.Major >= ObsVersion.MinimumMajor)
            return new DoctorCheck(name, true, version.ToString(), null);
        var upgrade = ObsUpgradeFix(os, p);
        return new DoctorCheck(name, false, $"{version}, but OBS {ObsVersion.MinimumMajor} or newer is needed"
            + (upgrade is null ? AnyDistro(UbuntuObs, ArchObs) : BrewNote(os)), upgrade);
    }

    private static string? MacBundle(ObsLaunch launch)
    {
        var i = launch.Args.ToList().IndexOf("-a");
        return i >= 0 && i + 1 < launch.Args.Count ? launch.Args[i + 1] : null;
    }

    private static string? PlistVersion(string? plist)
    {
        if (plist is null) return null;
        var m = System.Text.RegularExpressions.Regex.Match(plist,
            @"<key>CFBundleShortVersionString</key>\s*<string>([^<]*)</string>");
        return m.Success ? m.Groups[1].Value : null;
    }

    // "flatpak info" prints a Version line; the rest (commit hashes) must not be mistaken for it.
    private static string? FlatpakVersion(DoctorProbes p) =>
        p.Run("flatpak", ["info", FlatpakId]).StdOut.Split('\n')
            .Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("Version:", StringComparison.Ordinal));

    private static DoctorCheck WebSocket(SakshiPaths paths, DoctorProbes p)
    {
        const string name = "OBS WebSocket";
        var file = ObsWebSocketConfig.FileIn(paths.ObsConfigDir);
        var text = p.ReadFile(file);
        if (text is null) return new DoctorCheck(name, true, "no config yet; sakshi turns the server on when it starts OBS", null);
        try
        {
            if (ObsWebSocketConfig.ServerEnabled(ObsWebSocketConfig.Parse(file, text)))
                return new DoctorCheck(name, true, "server enabled", null);
        }
        catch (InvalidOperationException e)
        {
            return new DoctorCheck(name, false, e.Message, null);
        }
        return p.ObsRunning()
            ? new DoctorCheck(name, false, ObsWebSocketConfig.ServerOffMessage, null)
            : new DoctorCheck(name, true, "server off; sakshi turns it on when it starts OBS", null);
    }

    private static DoctorCheck Ffmpeg(HostOs os, DoctorProbes p)
    {
        var found = p.Which("ffmpeg")
            ?? (os == HostOs.MacOS
                ? MacPath.HomebrewDirs.Select(d => Path.Combine(d, "ffmpeg")).FirstOrDefault(p.FileExists)
                : null);
        return found is not null
            ? new DoctorCheck("ffmpeg", true, found, null)
            : FfmpegMissing(os, p);
    }

    private static DoctorCheck FfmpegMissing(HostOs os, DoctorProbes p)
    {
        var fix = FfmpegFix(os, p);
        var detail = os == HostOs.Windows ? "not found on PATH; after installing it, quit and restart Sakshi" : "not found on PATH";
        return new DoctorCheck("ffmpeg", false, detail + (fix is null ? AnyDistro(UbuntuFfmpeg, ArchFfmpeg) : BrewNote(os)), fix);
    }

    private static DoctorCheck SpeechModel(SakshiConfig config, SakshiPaths paths, DoctorProbes p)
    {
        if (config.Model.Length > 0 && p.FileExists(config.Model)) return new DoctorCheck("Speech model", true, config.Model, null);
        if (p.FileExists(Path.Combine(paths.ModelsDir, DefaultModelFile))) return new DoctorCheck("Speech model", true, Path.Combine(paths.ModelsDir, DefaultModelFile), null);
        if (p.FilesIn(paths.ModelsDir).FirstOrDefault() is { } any) return new DoctorCheck("Speech model", true, any, null);
        return new DoctorCheck("Speech model", true, "downloads about 150 MB on first transcription", null);
    }

    private static DoctorCheck VcRuntime(DoctorProbes p)
    {
        var system32 = Path.Combine(p.Env("SystemRoot") ?? @"C:\Windows", "System32");
        var missing = new[] { "vcruntime140.dll", "msvcp140.dll" }.Where(f => !p.FileExists(Path.Combine(system32, f))).ToList();
        return missing.Count == 0
            ? new DoctorCheck("VC++ runtime", true, "installed", null)
            : new DoctorCheck("VC++ runtime", false, $"missing {string.Join(", ", missing)}", "winget install Microsoft.VCRedist.2015+.x64");
    }

    private static DoctorCheck TrayIcon(DoctorProbes p)
    {
        const string name = "Tray icon support";
        // No gnome-extensions at all means no extension either: that is a "not on", not a failed check.
        ProcessResult info;
        try { info = p.Run("gnome-extensions", ["info", AppIndicator]); }
        catch (System.ComponentModel.Win32Exception) { info = new ProcessResult(127, "", ""); }
        var active = info.ExitCode == 0 && info.StdOut.Split('\n').Any(l =>
            l.Trim() is var t && t.StartsWith("State:", StringComparison.Ordinal)
            && (t.Contains("ACTIVE", StringComparison.OrdinalIgnoreCase) || t.Contains("ENABLED", StringComparison.OrdinalIgnoreCase)));
        if (active) return new DoctorCheck(name, true, "the AppIndicator extension is on", null);
        // Ubuntu ships the extension under this ID; elsewhere it has to be installed first, under its own ID.
        return OsReleaseIds(p.ReadFile("/etc/os-release")).Contains("ubuntu")
            ? new DoctorCheck(name, false, "the AppIndicator extension is not on, so GNOME shows no tray icon", $"gnome-extensions enable {AppIndicator}")
            : new DoctorCheck(name, false,
                "GNOME shows no tray icon without the AppIndicator extension: install and turn on " +
                "https://extensions.gnome.org/extension/615/appindicator-support/, then log out and back in", null);
    }

    // macOS does not let another app read these settings, so both are reminders with a link, not checks.
    private static DoctorCheck ScreenRecording() => new("Screen Recording", true,
        "OBS needs it to record the meeting window. In System Settings → Privacy & Security → Screen & System Audio " +
        "Recording (macOS 13 and 14: Screen Recording), turn OBS on.", null,
        "x-apple.systempreferences:com.apple.preference.security?Privacy_ScreenCapture");

    private static DoctorCheck Microphone() => new("Microphone", true,
        "OBS needs it to record your voice. In System Settings → Privacy & Security → Microphone, turn OBS on.", null,
        "x-apple.systempreferences:com.apple.preference.security?Privacy_Microphone");

    private static readonly Regex PickerLine = new(@"^\s*custom_picker_binary\s*=\s*(.*?)\s*$", RegexOptions.Multiline);
    private static readonly Regex ScreencopySection = new(@"^\s*screencopy\s*\{", RegexOptions.Multiline);
    private const string PreviewPicker = "hyprland-preview-share-picker";

    private static DoctorCheck KeyboardShortcut(DoctorProbes p) => new("Keyboard shortcut", true,
        $"optional: bind `{p.SakshiCommand} toggle` to a key (Omarchy: ~/.config/hypr/bindings.lua)", null, Required: false);

    /// <summary>
    /// Hyprland's portal must run sakshi's share picker, which lists meeting windows first and keeps their titles.
    /// It counts as set when the configured picker is named for sakshi or is a script that runs <c>sakshi share-picker</c>.
    /// </summary>
    private static DoctorCheck SharePicker(DoctorProbes p)
    {
        const string name = "Share picker";
        var home = p.Env("HOME") ?? "~";
        var configHome = p.Env("XDG_CONFIG_HOME") is { Length: > 0 } x ? x : home + "/.config";
        var conf = configHome + "/hypr/xdph.conf";
        var text = p.ReadFile(conf);
        var picker = text is not null && PickerLine.Match(text) is { Success: true } m ? m.Groups[1].Value : null;
        var path = picker?.StartsWith("~/", StringComparison.Ordinal) == true ? home + picker[1..] : picker;
        if (path is not null && (Path.GetFileName(path).Contains("sakshi", StringComparison.OrdinalIgnoreCase)
                || p.ReadFile(path) is { } script && script.Contains("share-picker", StringComparison.Ordinal)
                    && script.Contains("sakshi", StringComparison.Ordinal)))
            return new DoctorCheck(name, true, $"{conf} uses {picker}", null, Required: false);

        // sakshi's picker hands the list to this one; without it, pointing the portal at sakshi breaks screen sharing.
        if (p.Which(PreviewPicker) is null)
            return new DoctorCheck(name, false,
                $"optional: sakshi's picker lists meeting windows first, and needs {PreviewPicker}, which is not installed", null,
                Required: false);

        const string restart = "systemctl --user restart xdg-desktop-portal-hyprland";
        var wrapper = home + "/.local/bin/sakshi-share-picker";
        var makeWrapper = $"mkdir -p \"{home}/.local/bin\" && printf '#!/bin/sh\\nexec \"%s\" share-picker \"$@\"\\n' \"{p.SakshiCommand}\" > \"{wrapper}\" && chmod +x \"{wrapper}\"";
        // Appending is safe only when the file has no screencopy section yet; otherwise it is a hand edit.
        if (text is null || !ScreencopySection.IsMatch(text))
            return new DoctorCheck(name, false,
                "optional: Hyprland does not use sakshi's picker, which lists meeting windows first", 
                $"{makeWrapper} && mkdir -p \"{configHome}/hypr\" && printf '\\nscreencopy {{\\n    custom_picker_binary = %s\\n}}\\n' \"{wrapper}\" >> \"{conf}\" && {restart}",
                Required: false);
        return new DoctorCheck(name, false,
            $"optional: Hyprland does not use sakshi's picker, which lists meeting windows first. Run the command, then put " +
            $"`custom_picker_binary = {wrapper}` in the screencopy {{ }} section of {conf} and run `{restart}`",
            makeWrapper, Required: false);
    }

    // ----------------------------------------------------------------- fix lines

    private const string UbuntuObs = "sudo add-apt-repository ppa:obsproject/obs-studio && sudo apt install obs-studio";
    private const string ArchObs = "sudo pacman -S obs-studio";

    private const string UbuntuFfmpeg = "sudo apt install ffmpeg";
    private const string ArchFfmpeg = "sudo pacman -S ffmpeg";

    private static string? ObsFix(HostOs os, DoctorProbes p) => os switch
    {
        HostOs.Windows => "winget install OBSProject.OBSStudio",
        HostOs.MacOS => "brew install --cask obs",
        _ => LinuxFix(p, UbuntuObs, ArchObs),
    };

    private static string? ObsUpgradeFix(HostOs os, DoctorProbes p) => os switch
    {
        HostOs.Windows => "winget upgrade OBSProject.OBSStudio",
        HostOs.MacOS => "brew upgrade --cask obs",
        _ => LinuxFix(p, UbuntuObs, ArchObs),
    };

    private static string? FfmpegFix(HostOs os, DoctorProbes p) => os switch
    {
        HostOs.Windows => "winget install Gyan.FFmpeg",
        HostOs.MacOS => "brew install ffmpeg",
        _ => LinuxFix(p, UbuntuFfmpeg, ArchFfmpeg),
    };

    /// <summary>The apt line on Ubuntu/Debian, the pacman line on Arch; null elsewhere (see <see cref="AnyDistro"/>).</summary>
    private static string? LinuxFix(DoctorProbes p, string ubuntu, string arch)
    {
        var ids = OsReleaseIds(p.ReadFile("/etc/os-release"));
        if (ids.Any(i => i is "ubuntu" or "debian")) return ubuntu;
        if (ids.Contains("arch")) return arch;
        return null;
    }

    /// <summary>Another distribution: no command to copy, so the detail names both for the user to adapt.</summary>
    private static string AnyDistro(string ubuntu, string arch) =>
        $"; install it with your package manager (Ubuntu: {ubuntu}; Arch: {arch})";

    private static string BrewNote(HostOs os) =>
        os == HostOs.MacOS ? "; the command needs Homebrew (https://brew.sh)" : "";

    private static HashSet<string> OsReleaseIds(string? text)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in (text ?? "").Split('\n'))
        {
            var t = line.Trim();
            if (t.StartsWith("ID=", StringComparison.Ordinal)) ids.Add(t[3..].Trim('"', '\'').ToLowerInvariant());
            else if (t.StartsWith("ID_LIKE=", StringComparison.Ordinal))
                foreach (var id in t[8..].Trim('"', '\'').Split(' ', StringSplitOptions.RemoveEmptyEntries)) ids.Add(id.ToLowerInvariant());
        }
        return ids;
    }
}
