using Sakshi.TestSupport;

namespace Sakshi.Core.Tests;

[Collection("Environment")]
public class TempHomeTests
{
    private static readonly string[] Vars = ["SAKSHI_CONFIG_DIR", "SAKSHI_STATE_DIR", "SAKSHI_DATA_DIR"];

    [Fact]
    public void EnvVarsPointInsideTempRootAndAreRestored()
    {
        var before = Vars.Select(Environment.GetEnvironmentVariable).ToArray();
        string root, config, state, data;
        using (var home = new TempHome())
        {
            root = home.Root;
            config = home.ConfigDir;
            state = home.StateDir;
            data = home.DataDir;
            Assert.True(Directory.Exists(home.Home));
            Assert.True(Directory.Exists(home.Meetings));
            Assert.True(Directory.Exists(config));
            Assert.True(Directory.Exists(state));
            Assert.True(Directory.Exists(data));
            Assert.Equal(config, Environment.GetEnvironmentVariable("SAKSHI_CONFIG_DIR"));
            Assert.Equal(state, Environment.GetEnvironmentVariable("SAKSHI_STATE_DIR"));
            Assert.Equal(data, Environment.GetEnvironmentVariable("SAKSHI_DATA_DIR"));
            foreach (var p in new[] { home.Home, home.Meetings, config, state, data })
                Assert.StartsWith(root + Path.DirectorySeparatorChar, p);
        }
        Assert.Equal(before, Vars.Select(Environment.GetEnvironmentVariable).ToArray());
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void ClearsTheBashConfigAndXdgVars_AndRestoresThem()
    {
        string[] cleared = ["SAKSHI_CONFIG", "XDG_CONFIG_HOME", "XDG_STATE_HOME", "XDG_DATA_HOME"];
        var before = cleared.Select(Environment.GetEnvironmentVariable).ToArray();
        try
        {
            foreach (var v in cleared) Environment.SetEnvironmentVariable(v, "/real/" + v);
            using (new TempHome())
                Assert.All(cleared, v => Assert.Null(Environment.GetEnvironmentVariable(v)));
            Assert.All(cleared, v => Assert.Equal("/real/" + v, Environment.GetEnvironmentVariable(v)));
        }
        finally
        {
            for (var i = 0; i < cleared.Length; i++) Environment.SetEnvironmentVariable(cleared[i], before[i]);
        }
    }
}
