using Sakshi.Core.Paths;

namespace Sakshi.Core.Tests;

public class PathsTests
{
    private static Func<string, string?> Env(params (string, string)[] vars)
    {
        var d = vars.ToDictionary(v => v.Item1, v => v.Item2);
        return k => d.GetValueOrDefault(k);
    }

    private static string P(params string[] parts) => Path.Combine(parts);

    [Fact]
    public void Resolve_Linux_Defaults()
    {
        var home = P(Path.GetTempPath(), "nohome-" + Guid.NewGuid().ToString("N"));
        var p = SakshiPaths.Resolve(HostOs.Linux, home, Env());
        Assert.Equal(P(home, ".config", "sakshi"), p.ConfigDir);
        Assert.Equal(P(home, ".local", "state", "sakshi"), p.StateDir);
        Assert.Equal(P(home, ".local", "share", "sakshi"), p.DataDir);
        Assert.Equal(P(home, ".config", "obs-studio"), p.ObsConfigDir);
        Assert.Equal(P(p.ConfigDir, "config.json"), p.ConfigFile);
        Assert.Equal(P(p.StateDir, "state.json"), p.StateFile);
        Assert.Equal(P(p.StateDir, "sakshi.log"), p.LogFile);
        Assert.Equal(P(p.DataDir, "index.db"), p.IndexFile);
        Assert.Equal(P(p.DataDir, "models"), p.ModelsDir);
    }

    [Fact]
    public void Resolve_Linux_XdgAndSakshiOverrides()
    {
        var home = P(Path.GetTempPath(), "h");
        var xdg = Env(("XDG_CONFIG_HOME", "/x/c"), ("XDG_STATE_HOME", "/x/s"), ("XDG_DATA_HOME", "/x/d"));
        var p = SakshiPaths.Resolve(HostOs.Linux, home, xdg);
        Assert.Equal(P("/x/c", "sakshi"), p.ConfigDir);
        Assert.Equal(P("/x/s", "sakshi"), p.StateDir);
        Assert.Equal(P("/x/d", "sakshi"), p.DataDir);

        var both = Env(("XDG_CONFIG_HOME", "/x/c"), ("SAKSHI_CONFIG_DIR", "/o/c"),
            ("SAKSHI_STATE_DIR", "/o/s"), ("SAKSHI_DATA_DIR", "/o/d"));
        p = SakshiPaths.Resolve(HostOs.Linux, home, both);
        Assert.Equal("/o/c", p.ConfigDir);
        Assert.Equal("/o/s", p.StateDir);
        Assert.Equal("/o/d", p.DataDir);
    }

    [Fact]
    public void Resolve_Linux_FlatpakObsWhenOnlyThatExists()
    {
        var home = P(Path.GetTempPath(), "sakshi-obs-" + Guid.NewGuid().ToString("N"));
        var flat = P(home, ".var", "app", "com.obsproject.Studio", "config", "obs-studio");
        Directory.CreateDirectory(flat);
        try
        {
            Assert.Equal(flat, SakshiPaths.Resolve(HostOs.Linux, home, Env()).ObsConfigDir);
            Directory.CreateDirectory(P(home, ".config", "obs-studio"));
            Assert.Equal(P(home, ".config", "obs-studio"), SakshiPaths.Resolve(HostOs.Linux, home, Env()).ObsConfigDir);
        }
        finally { Directory.Delete(home, true); }
    }

    [Fact]
    public void Resolve_MacOS()
    {
        var home = P(Path.GetTempPath(), "h");
        var support = P(home, "Library", "Application Support");
        var p = SakshiPaths.Resolve(HostOs.MacOS, home, Env());
        Assert.Equal(P(support, "Sakshi"), p.ConfigDir);
        Assert.Equal(P(support, "Sakshi", "state"), p.StateDir);
        Assert.Equal(P(support, "Sakshi", "data"), p.DataDir);
        Assert.Equal(P(support, "obs-studio"), p.ObsConfigDir);
    }

    [Fact]
    public void Resolve_Windows()
    {
        var home = P(Path.GetTempPath(), "h");
        var env = Env(("APPDATA", "/AD/Roaming"), ("LOCALAPPDATA", "/AD/Local"));
        var p = SakshiPaths.Resolve(HostOs.Windows, home, env);
        Assert.Equal(P("/AD/Roaming", "Sakshi"), p.ConfigDir);
        Assert.Equal(P("/AD/Roaming", "Sakshi", "state"), p.StateDir);
        Assert.Equal(P("/AD/Local", "Sakshi"), p.DataDir);
        Assert.Equal(P("/AD/Roaming", "obs-studio"), p.ObsConfigDir);
    }

    [Fact]
    public void Resolve_OverridesWinOnEveryOs()
    {
        var env = Env(("SAKSHI_CONFIG_DIR", "/o/c"), ("SAKSHI_STATE_DIR", "/o/s"), ("SAKSHI_DATA_DIR", "/o/d"),
            ("APPDATA", "/a"), ("LOCALAPPDATA", "/l"));
        foreach (var os in Enum.GetValues<HostOs>())
        {
            var p = SakshiPaths.Resolve(os, "/home", env);
            Assert.Equal("/o/c", p.ConfigDir);
            Assert.Equal("/o/s", p.StateDir);
            Assert.Equal("/o/d", p.DataDir);
        }
    }

    [Fact]
    public void Resolve_Windows_FallsBackUnderHomeWhenAppDataUnset()
    {
        var home = P(Path.GetTempPath(), "h");
        var p = SakshiPaths.Resolve(HostOs.Windows, home, Env());
        Assert.Equal(P(home, "AppData", "Roaming", "Sakshi"), p.ConfigDir);
        Assert.Equal(P(home, "AppData", "Roaming", "Sakshi", "state"), p.StateDir);
        Assert.Equal(P(home, "AppData", "Local", "Sakshi"), p.DataDir);
        Assert.Equal(P(home, "AppData", "Roaming", "obs-studio"), p.ObsConfigDir);
    }

    [Fact]
    public void Resolve_EmptyEnvVarCountsAsUnset()
    {
        var home = P(Path.GetTempPath(), "nohome-" + Guid.NewGuid().ToString("N"));
        var p = SakshiPaths.Resolve(HostOs.Linux, home,
            Env(("SAKSHI_CONFIG_DIR", ""), ("XDG_STATE_HOME", ""), ("SAKSHI_DATA_DIR", "")));
        Assert.Equal(P(home, ".config", "sakshi"), p.ConfigDir);
        Assert.Equal(P(home, ".local", "state", "sakshi"), p.StateDir);
        Assert.Equal(P(home, ".local", "share", "sakshi"), p.DataDir);
    }
}
