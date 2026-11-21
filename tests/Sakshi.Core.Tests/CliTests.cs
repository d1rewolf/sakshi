using Sakshi.App;
using Sakshi.TestSupport;

namespace Sakshi.Core.Tests;

[Collection("Environment")]
public sealed class CliTests : IDisposable
{
    private readonly TempHome _home = new();

    public void Dispose() => _home.Dispose();

    private (int Code, string Out, string Err) Run(params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var code = Program.Run(args, o, e, _home.Home);
        return (code, o.ToString(), e.ToString());
    }

    [Fact]
    public void Help_ListsCommands()
    {
        foreach (var args in new[] { new[] { "--help" }, new[] { "-h" }, Array.Empty<string>() })
        {
            var (code, output, _) = Run(args);
            Assert.Equal(0, code);
            foreach (var c in new[] { "start", "stop", "toggle", "import", "housekeep", "migrate", "status", "config", "obs", "find", "index", "transcribe" })
                Assert.Contains("sakshi " + c, output);
        }
    }

    [Fact]
    public void Help_ShowsThatHousekeepAndMigrateRunForRealWithoutDashN()
    {
        var (_, output, _) = Run("--help");
        Assert.Contains("sakshi housekeep [-n]", output);
        Assert.Contains("sakshi migrate [-n]", output);
        Assert.DoesNotContain("sakshi housekeep -n ", output);
    }

    [Fact]
    public void UnknownCommand_Exit2()
    {
        var (code, _, err) = Run("frobnicate");
        Assert.Equal(2, code);
        Assert.Contains("frobnicate", err);
    }

    [Fact]
    public void Housekeep_DryRun_PrintsLines()
    {
        Directory.CreateDirectory(Path.Combine(_home.Meetings, "2020-01-02-Thu", "old"));
        var (code, output, _) = Run("housekeep", "-n");
        Assert.Equal(0, code);
        Assert.Contains("2020-01-02-Thu", output);
        Assert.True(Directory.Exists(Path.Combine(_home.Meetings, "2020-01-02-Thu")));
    }

    [Fact]
    public void Status_IdleJson()
    {
        var (code, output, _) = Run("status");
        Assert.Equal(0, code);
        Assert.Equal("{\"state\":\"idle\",\"tooltip\":\"Record a meeting (OBS)\",\"elapsed\":\"\"}\n", output.Replace("\r\n", "\n"));
    }

    [Fact]
    public void Config_PrintsPath()
    {
        var (code, output, _) = Run("config");
        Assert.Equal(0, code);
        Assert.Equal(Path.Combine(_home.ConfigDir, "config.json"), output.Trim());
    }

    [Fact]
    public void ConfigImport_WritesJson_AndKeepsBackup()
    {
        var bash = Path.Combine(_home.Home, ".config", "sakshi", "config");
        Directory.CreateDirectory(Path.GetDirectoryName(bash)!);
        File.WriteAllText(bash, "KEEP_DAYS=30\n");
        var target = Path.Combine(_home.ConfigDir, "config.json");
        File.WriteAllText(target, "{\"keepDays\": 3}");

        var (code, _, _) = Run("config", "import");

        Assert.Equal(0, code);
        Assert.Contains("\"keepDays\": 30", File.ReadAllText(target));
        Assert.Equal("{\"keepDays\": 3}", File.ReadAllText(target + ".bak"));
    }

    [Fact]
    public void ConfigImport_NeverOverwritesAnEarlierBackup()
    {
        var bash = Path.Combine(_home.Home, ".config", "sakshi", "config");
        Directory.CreateDirectory(Path.GetDirectoryName(bash)!);
        File.WriteAllText(bash, "KEEP_DAYS=30\n");
        var target = Path.Combine(_home.ConfigDir, "config.json");
        File.WriteAllText(target, "{\"keepDays\": 3}");
        File.WriteAllText(target + ".bak", "the first backup");

        var (code, output, _) = Run("config", "import");

        Assert.Equal(0, code);
        Assert.Equal("the first backup", File.ReadAllText(target + ".bak"));
        var stamped = Assert.Single(Directory.GetFiles(_home.ConfigDir, "config.json.bak-*"));
        Assert.Matches(@"config\.json\.bak-\d{14}$", stamped);
        Assert.Equal("{\"keepDays\": 3}", File.ReadAllText(stamped));
        Assert.Contains(stamped, output);
    }

    [Fact]
    public void Find_UsesBothRoots()
    {
        MeetingFixture.Make(_home.Meetings, "2026-11-04-Wed", "alpha", [(0, "the Catholic diocese")]);
        var archive = Path.Combine(_home.Meetings, "archives");
        MeetingFixture.Make(archive, "2026-01-05-Mon", "beta", [(0, "another catholic matter")]);

        var (code, output, _) = Run("find", "catholic");

        Assert.Equal(0, code);
        Assert.Contains("alpha", output);
        Assert.Contains("beta", output);
        Assert.True(File.Exists(Path.Combine(_home.DataDir, "index.db")));
    }

    [Fact]
    public void Find_NoMatch_Exit1()
    {
        Assert.Equal(1, Run("find", "zzzqqq").Code);
    }

    private static int ClosedPort()
    {
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        var port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    [Fact]
    public void Obs_ConnectionRefused_Exit1_NoStack()
    {
        File.WriteAllText(Path.Combine(_home.ConfigDir, "config.json"), $"{{\"obsWsPort\": {ClosedPort()}}}");
        var (code, _, err) = Run("obs", "GetVersion");
        Assert.Equal(1, code);
        Assert.Contains("Error:", err);
        Assert.DoesNotContain("   at ", err);
    }

    [Fact]
    public void Index_IoError_Exit1_NoStack()
    {
        Directory.CreateDirectory(Path.Combine(_home.DataDir, "index.db"));
        var (code, _, err) = Run("index");
        Assert.Equal(1, code);
        Assert.Contains("Error:", err);
        Assert.DoesNotContain("   at ", err);
    }

    [Fact]
    public void BrokenConfig_StillAllowsHelpAndUsage()
    {
        File.WriteAllText(Path.Combine(_home.ConfigDir, "config.json"), "{ not json");
        Assert.Equal(0, Run("transcribe", "--help").Code);
        Assert.Equal(2, Run("obs").Code);
    }

    [Fact]
    public void Transcribe_Log_UsageErrorIsWrittenToTheLog()
    {
        var log = Path.Combine(_home.Root, "transcribe.log");

        var (code, _, err) = Run("transcribe", "--log", log, "-F", "bogus", "x.mp4");

        Assert.Equal(2, code);
        Assert.Contains("Error: invalid --frames mode: bogus", err);
        Assert.Contains("Error: invalid --frames mode: bogus", File.ReadAllText(log));
    }

    [Fact]
    public void Transcribe_Log_BrokenConfigIsWrittenToTheLog()
    {
        var config = Path.Combine(_home.ConfigDir, "config.json");
        File.WriteAllText(config, "{ not json");
        var log = Path.Combine(_home.Root, "transcribe.log");
        File.WriteAllText(log, "stale");

        var (code, _, err) = Run("transcribe", "--log", log, "x.mp4");

        Assert.Equal(1, code);
        Assert.Contains($"Error: Config file {config} is not valid JSON", err);
        var text = File.ReadAllText(log);
        Assert.Contains($"Error: Config file {config} is not valid JSON", text);
        Assert.DoesNotContain("stale", text);
    }

    [Fact]
    public void Status_CreatesNoStateFile()
    {
        Assert.Equal(0, Run("status").Code);
        Assert.False(File.Exists(Path.Combine(_home.StateDir, "state.json")));
    }

    [Theory]
    [InlineData("--root")]
    [InlineData("--root=/x")]
    public void Find_RootRejected_Exit2(string root)
    {
        Assert.Equal(2, Run("find", root, "word").Code);
    }
}
