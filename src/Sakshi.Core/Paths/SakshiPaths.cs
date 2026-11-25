namespace Sakshi.Core.Paths;

public enum HostOs { Linux, MacOS, Windows }

public sealed record SakshiPaths(string ConfigDir, string StateDir, string DataDir, string ObsConfigDir)
{
    public string ConfigFile => Path.Combine(ConfigDir, "config.json");
    public string StateFile => Path.Combine(StateDir, "state.json");
    public string LogFile => Path.Combine(StateDir, "sakshi.log");
    public string IndexFile => Path.Combine(DataDir, "index.db");
    public string ModelsDir => Path.Combine(DataDir, "models");
    public string VoicesFile => Path.Combine(DataDir, "voices", "voices.json");

    public static SakshiPaths Current()
    {
        var os = OperatingSystem.IsWindows() ? HostOs.Windows
            : OperatingSystem.IsMacOS() ? HostOs.MacOS
            : HostOs.Linux;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Resolve(os, home, Environment.GetEnvironmentVariable);
    }

    public static SakshiPaths Resolve(HostOs os, string home, Func<string, string?> env)
    {
        string Env(string name) => env(name) is { Length: > 0 } v ? v : "";
        string Pick(string overrideVar, Func<string> fallback) =>
            Env(overrideVar) is { Length: > 0 } v ? v : fallback();

        switch (os)
        {
            case HostOs.Windows:
            {
                var roaming = Env("APPDATA") is { Length: > 0 } a ? a : Path.Combine(home, "AppData", "Roaming");
                var local = Env("LOCALAPPDATA") is { Length: > 0 } l ? l : Path.Combine(home, "AppData", "Local");
                return new SakshiPaths(
                    Pick("SAKSHI_CONFIG_DIR", () => Path.Combine(roaming, "Sakshi")),
                    Pick("SAKSHI_STATE_DIR", () => Path.Combine(roaming, "Sakshi", "state")),
                    Pick("SAKSHI_DATA_DIR", () => Path.Combine(local, "Sakshi")),
                    Path.Combine(roaming, "obs-studio"));
            }
            case HostOs.MacOS:
            {
                var support = Path.Combine(home, "Library", "Application Support");
                var root = Path.Combine(support, "Sakshi");
                return new SakshiPaths(
                    Pick("SAKSHI_CONFIG_DIR", () => root),
                    Pick("SAKSHI_STATE_DIR", () => Path.Combine(root, "state")),
                    Pick("SAKSHI_DATA_DIR", () => Path.Combine(root, "data")),
                    Path.Combine(support, "obs-studio"));
            }
            default:
            {
                string Xdg(string xdgVar, params string[] rel) =>
                    Env(xdgVar) is { Length: > 0 } x ? Path.Combine(x, "sakshi") : Path.Combine([home, .. rel, "sakshi"]);
                var obs = Path.Combine(home, ".config", "obs-studio");
                var flatpak = Path.Combine(home, ".var", "app", "com.obsproject.Studio", "config", "obs-studio");
                if (!Directory.Exists(obs) && Directory.Exists(flatpak)) obs = flatpak;
                return new SakshiPaths(
                    Pick("SAKSHI_CONFIG_DIR", () => Xdg("XDG_CONFIG_HOME", ".config")),
                    Pick("SAKSHI_STATE_DIR", () => Xdg("XDG_STATE_HOME", ".local", "state")),
                    Pick("SAKSHI_DATA_DIR", () => Xdg("XDG_DATA_HOME", ".local", "share")),
                    obs);
            }
        }
    }
}
