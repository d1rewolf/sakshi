using System.Diagnostics;

namespace Sakshi.App.Tests;

/// <summary>
/// Runs the built app as a real process, because only <c>Program.Main</c> starts Velopack (the in-process
/// <c>Program.Run</c> skips it). A CLI call must keep its own output and exit code whatever Velopack state
/// the machine is in.
/// </summary>
public sealed class VelopackStartupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sakshi-velo-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Velopack's log goes to the temp dir on Linux and (with no ~/Library/Logs) on macOS.</summary>
    private string Temp => Path.Combine(_root, "tmp");

    private string[] VelopackLogs() =>
        Directory.Exists(_root) ? Directory.GetFiles(_root, "velopack*.log", SearchOption.AllDirectories) : [];

    private (int Code, string Out, string Err) RunSakshi(string[] args, params (string, string)[] extraEnv)
    {
        var home = Path.Combine(_root, "home");
        Directory.CreateDirectory(home);
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "sakshi.dll"));
        foreach (var a in args) psi.ArgumentList.Add(a);
        // Everything the app might write lives under the temp root.
        foreach (var (k, v) in new[]
        {
            ("HOME", home), ("USERPROFILE", home), ("APPDATA", Path.Combine(_root, "roaming")), ("LOCALAPPDATA", Path.Combine(_root, "local")),
            ("XDG_CONFIG_HOME", Path.Combine(_root, "xdg-config")), ("XDG_STATE_HOME", Path.Combine(_root, "xdg-state")),
            ("XDG_DATA_HOME", Path.Combine(_root, "xdg-data")), ("SAKSHI_CONFIG_DIR", Path.Combine(_root, "config")),
            ("SAKSHI_STATE_DIR", Path.Combine(_root, "state")), ("SAKSHI_DATA_DIR", Path.Combine(_root, "data")),
            ("TMPDIR", Temp), ("TMP", Temp), ("TEMP", Temp),
        }.Concat(extraEnv))
            psi.Environment[k] = v;
        psi.Environment.Remove("SAKSHI_CONFIG");
        Directory.CreateDirectory(Temp);

        using var p = Process.Start(psi)!;
        p.StandardInput.Close();
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(TimeSpan.FromSeconds(30)))
        {
            p.Kill(entireProcessTree: true);
            Assert.Fail("sakshi did not exit within 30 seconds");
        }
        return (p.ExitCode, stdout.Result, stderr.Result);
    }

    [Fact]
    public void Status_keeps_its_output_and_exit_code_on_the_first_run_of_an_install()
    {
        var (code, output, _) = RunSakshi(["status"], ("VELOPACK_FIRSTRUN", "true"));

        Assert.Equal(0, code);
        var line = Assert.Single(output.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.StartsWith("{\"state\":", line);
    }

    [Fact]
    public void Install_hook_exits_cleanly_without_printing_usage()
    {
        var (code, output, error) = RunSakshi(["--veloapp-install", "0.1.0"]);

        Assert.Equal(0, code);
        Assert.DoesNotContain("usage", output + error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Installed_first_run_flag_without_an_install_changes_nothing_on_disk()
    {
        RunSakshi(["status"], ("VELOPACK_FIRSTRUN", "true"));

        // Velopack only fires OnFirstRun for an installed copy, so no autostart file appears.
        Assert.False(Directory.Exists(Path.Combine(_root, "xdg-config", "autostart")));
    }

    [Fact]
    public void A_plain_cli_call_leaves_no_velopack_log_but_a_hook_run_keeps_it()
    {
        // Windows logs under the known LocalAppData folder, which the environment cannot redirect.
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Velopack's Windows log is outside the temp root");

        RunSakshi(["status"]);
        Assert.Empty(VelopackLogs());

        RunSakshi(["--veloapp-install", "0.1.0"]);
        Assert.NotEmpty(VelopackLogs());
    }

    [Theory]
    [InlineData(ProgramRoute.Tray, new string[0], null, true)]
    [InlineData(ProgramRoute.Tray, new[] { "tray" }, null, true)]
    [InlineData(ProgramRoute.Cli, new[] { "--veloapp-install", "0.1.0" }, null, true)]
    [InlineData(ProgramRoute.Cli, new[] { "--VeloApp-Uninstall", "0.1.0" }, null, true)]
    [InlineData(ProgramRoute.Cli, new[] { "--squirrel-install", "0.1.0" }, null, true)]
    [InlineData(ProgramRoute.Cli, new[] { "status" }, "VELOPACK_FIRSTRUN", true)]
    [InlineData(ProgramRoute.Cli, new[] { "status" }, "VELOPACK_RESTART", true)]
    [InlineData(ProgramRoute.Cli, new[] { "status" }, null, false)]
    [InlineData(ProgramRoute.Cli, new[] { "transcribe", "/x" }, null, false)]
    public void Velopack_starts_for_the_tray_hooks_and_its_launches_only(ProgramRoute route, string[] args, string? setVar, bool expected) =>
        Assert.Equal(expected, Program.NeedsVelopack(route, args, n => n == setVar ? "true" : null));
}
