using System.Diagnostics;
using Sakshi.Platform;
using Sakshi.TestSupport;

namespace Sakshi.Platform.Tests;

[Collection("Environment")]
public sealed class ProcessRunnerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sakshi-proc-" + Guid.NewGuid().ToString("N"));
    private readonly string _bin;
    private readonly string _helper;
    private readonly string?[] _saved;
    private static readonly string[] Vars = ["TEST_ARGS_FILE", "TEST_EXIT_CODE", "TEST_SLEEP_SECONDS", "TEST_READ_STDIN", "TEST_PID_FILE", "TEST_SPAWN_SLEEPER"];

    public ProcessRunnerTests()
    {
        Directory.CreateDirectory(_root);
        _bin = TestHelper.Install(_root);
        _helper = Path.Combine(_bin, TestHelper.Name + (OperatingSystem.IsWindows() ? ".exe" : ""));
        _saved = Vars.Select(Environment.GetEnvironmentVariable).ToArray();
        foreach (var v in Vars) Environment.SetEnvironmentVariable(v, null);
    }

    public void Dispose()
    {
        for (var i = 0; i < Vars.Length; i++) Environment.SetEnvironmentVariable(Vars[i], _saved[i]);
        try { Directory.Delete(_root, recursive: true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private static void AssertGone(string pidFile)
    {
        Assert.True(File.Exists(pidFile), "helper never started");
        var pid = int.Parse(File.ReadAllText(pidFile));
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            try { using var p = Process.GetProcessById(pid); if (p.HasExited) return; }
            catch (ArgumentException) { return; }
            Thread.Sleep(50);
        }
        Assert.Fail($"process {pid} is still running");
    }

    [Fact]
    public async Task ProcessRunner_RunAttached_PassesArgsEnvAndExitCode()
    {
        var argsFile = Path.Combine(_root, "args.txt");
        var envFile = Path.Combine(_root, "env.txt");
        Environment.SetEnvironmentVariable("TEST_ARGS_FILE", argsFile);
        Environment.SetEnvironmentVariable("TEST_EXIT_CODE", "5");
        Environment.SetEnvironmentVariable("TEST_DUMP_ENV", "XDPH_WINDOW_SHARING_LIST");
        Environment.SetEnvironmentVariable("TEST_ENV_FILE", envFile);
        try
        {
            var code = await new ProcessRunner().RunAttachedAsync(_helper, ["--config", "a b"],
                new Dictionary<string, string?> { ["XDPH_WINDOW_SHARING_LIST"] = "1[HC>]x[HA>]" }, CancellationToken.None);

            Assert.Equal(5, code);
            Assert.Equal(["--config", "a b"], File.ReadAllLines(argsFile));
            Assert.Equal("1[HC>]x[HA>]", File.ReadAllText(envFile));
        }
        finally
        {
            Environment.SetEnvironmentVariable("TEST_DUMP_ENV", null);
            Environment.SetEnvironmentVariable("TEST_ENV_FILE", null);
        }
    }

    [System.Runtime.InteropServices.DllImport("libc", SetLastError = true)] private static extern int dup(int fd);
    [System.Runtime.InteropServices.DllImport("libc", SetLastError = true)] private static extern int dup2(int oldfd, int newfd);
    [System.Runtime.InteropServices.DllImport("libc", SetLastError = true)] private static extern int close(int fd);

    [Fact]
    public async Task ProcessRunner_RunAttached_ChildWritesToOurStdout()
    {
        if (OperatingSystem.IsWindows()) Assert.Skip("redirects file descriptor 1");
        var outFile = Path.Combine(_root, "stdout.txt");
        var saved = dup(1);
        Assert.True(saved >= 0);
        try
        {
            using (var fs = new FileStream(outFile, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
            {
                Assert.True(dup2((int)fs.SafeFileHandle.DangerousGetHandle(), 1) >= 0);
                try { await new ProcessRunner().RunAttachedAsync(_helper, [], new Dictionary<string, string?>(), CancellationToken.None); }
                finally { dup2(saved, 1); }
            }
        }
        finally { close(saved); }
        Assert.Contains("helper stdout", File.ReadAllText(outFile));
    }

    [Fact]
    public async Task ProcessRunner_RunAttached_NullEnvValueRemovesVariable()
    {
        var envFile = Path.Combine(_root, "env.txt");
        Environment.SetEnvironmentVariable("TEST_DUMP_ENV", "SAKSHI_PICKER_PROBE");
        Environment.SetEnvironmentVariable("TEST_ENV_FILE", envFile);
        Environment.SetEnvironmentVariable("SAKSHI_PICKER_PROBE", "set");
        try
        {
            await new ProcessRunner().RunAttachedAsync(_helper, [], new Dictionary<string, string?> { ["SAKSHI_PICKER_PROBE"] = null }, CancellationToken.None);
            Assert.Equal("", File.ReadAllText(envFile));
        }
        finally
        {
            foreach (var v in new[] { "TEST_DUMP_ENV", "TEST_ENV_FILE", "SAKSHI_PICKER_PROBE" })
                Environment.SetEnvironmentVariable(v, null);
        }
    }

    [Fact]
    public async Task ProcessRunner_RunAttached_CancelKillsChild()
    {
        var pidFile = Path.Combine(_root, "pid.txt");
        Environment.SetEnvironmentVariable("TEST_PID_FILE", pidFile);
        Environment.SetEnvironmentVariable("TEST_SLEEP_SECONDS", "60");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new ProcessRunner().RunAttachedAsync(_helper, [], new Dictionary<string, string?>(), cts.Token));
        AssertGone(pidFile);
    }

    [Fact]
    public async Task ProcessRunner_RunsAndCaptures()
    {
        var argsFile = Path.Combine(_root, "args.txt");
        Environment.SetEnvironmentVariable("TEST_ARGS_FILE", argsFile);
        Environment.SetEnvironmentVariable("TEST_EXIT_CODE", "3");

        var r = await new ProcessRunner().RunAsync(_helper, ["a b", "c;d", "$HOME"], TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.Equal(3, r.ExitCode);
        Assert.Contains("helper stdout", r.StdOut);
        Assert.Contains("helper stderr", r.StdErr);
        Assert.Equal(["a b", "c;d", "$HOME"], File.ReadAllLines(argsFile));
    }

    [Fact]
    public async Task ProcessRunner_TimeoutKills()
    {
        Environment.SetEnvironmentVariable("TEST_SLEEP_SECONDS", "30");
        var pidFile = Path.Combine(_root, "pid.txt");
        Environment.SetEnvironmentVariable("TEST_PID_FILE", pidFile);
        var sw = Stopwatch.StartNew();
        await Assert.ThrowsAsync<TimeoutException>(() =>
            new ProcessRunner().RunAsync(_helper, [], TimeSpan.FromSeconds(2), CancellationToken.None));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20), $"took {sw.Elapsed}");
        AssertGone(pidFile);
    }

    [Fact]
    public async Task ProcessRunner_CancellationThrowsOce()
    {
        Environment.SetEnvironmentVariable("TEST_SLEEP_SECONDS", "30");
        var pidFile = Path.Combine(_root, "pid.txt");
        Environment.SetEnvironmentVariable("TEST_PID_FILE", pidFile);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new ProcessRunner().RunAsync(_helper, [], TimeSpan.FromSeconds(60), cts.Token));
        AssertGone(pidFile);
    }

    [Fact]
    public void StartDetached_ReturnsQuicklyWithStdinOnDevNull_AndArgvIntact()
    {
        // TEST_READ_STDIN: the helper reads stdin to EOF before recording argv, so it only
        // finishes if stdin is closed / /dev/null.
        Environment.SetEnvironmentVariable("TEST_READ_STDIN", "1");
        var argsFile = Path.Combine(_root, "detached.txt");
        Environment.SetEnvironmentVariable("TEST_ARGS_FILE", argsFile);
        string[] argv = ["has space", "a\"quote", "$HOME", "x;y"];

        var sw = Stopwatch.StartNew();
        var pid = new ProcessRunner().StartDetached(_helper, argv);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1), $"StartDetached took {sw.Elapsed}");
        Assert.True(pid > 0);

        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline && LinesOf(argsFile).Length < argv.Length)
            Thread.Sleep(50);
        Assert.Equal(argv, LinesOf(argsFile));
    }

    /// <summary>The file's lines, or none while it is missing or (Windows) the helper still has it open.</summary>
    private static string[] LinesOf(string path)
    {
        try { return File.Exists(path) ? File.ReadAllLines(path) : []; }
        catch (IOException) { return []; }
    }

    [Fact]
    public void StartDetached_MissingFile_Throws()
    {
        var ex = Assert.Throws<System.ComponentModel.Win32Exception>(() =>
            new ProcessRunner().StartDetached(Path.Combine(_root, "no-such-file"), []));
        Assert.Contains("no-such-file", ex.Message);
        Assert.Throws<System.ComponentModel.Win32Exception>(() =>
            new ProcessRunner().StartDetached("sakshi-no-such-command-xyz", []));
    }

    [Fact]
    public void StartDetached_NonExecutableFile_Throws()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix execute bit");
        var f = Path.Combine(_root, "plain.txt");
        File.WriteAllText(f, "x");
        var ex = Assert.Throws<System.ComponentModel.Win32Exception>(() => new ProcessRunner().StartDetached(f, []));
        Assert.Contains("plain.txt", ex.Message);
    }

    [Fact]
    public void StartDetached_ExecutableOnlyForOthers_Throws()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix permissions");
        Assert.SkipWhen(Environment.UserName == "root", "root passes access(X_OK) when any exec bit is set");
        var f = Path.Combine(_root, "others-only");
        File.WriteAllText(f, "#!/bin/sh\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(f, UnixFileMode.OtherExecute);
        Assert.Throws<System.ComponentModel.Win32Exception>(() => new ProcessRunner().StartDetached(f, []));
    }

    [Fact]
    public void StartDetached_WindowsExtensionlessPath_NotRejectedByPrecheck()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows CreateProcess path resolution");
        var comspec = Environment.GetEnvironmentVariable("ComSpec")!;
        var noExt = Path.Combine(Path.GetDirectoryName(comspec)!, Path.GetFileNameWithoutExtension(comspec));
        var pid = new ProcessRunner().StartDetached(noExt, ["/c", "exit 0"]);
        Assert.True(pid > 0);
    }

    [Fact]
    public void Which_FindsOnPath()
    {
        var saved = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("PATH", _bin + Path.PathSeparator + saved);
            var found = new ProcessRunner().Which(TestHelper.Name);
            Assert.NotNull(found);
            Assert.Equal(_bin, Path.GetDirectoryName(found));
            Assert.Null(new ProcessRunner().Which("sakshi-no-such-command-xyz"));
        }
        finally { Environment.SetEnvironmentVariable("PATH", saved); }
    }

    [Fact]
    public void IsRunning_MatchesExactProcessName()
    {
        var me = Process.GetCurrentProcess().ProcessName;
        var runner = new ProcessRunner();
        Assert.True(runner.IsRunning(me));
        Assert.False(runner.IsRunning(me[..^1]));
        Assert.False(runner.IsRunning("sakshi-no-such-process-xyz"));
    }

    [Fact]
    public async Task RunAsync_GrandchildHoldingStdout_DoesNotHangPastTimeout()
    {
        // The helper exits at once but leaves a 4 s sleeper holding its stdout/stderr pipes.
        Environment.SetEnvironmentVariable("TEST_SPAWN_SLEEPER", "1");
        var sw = Stopwatch.StartNew();
        await Assert.ThrowsAsync<TimeoutException>(() =>
            new ProcessRunner().RunAsync(_helper, [], TimeSpan.FromSeconds(1), CancellationToken.None));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3.5), $"took {sw.Elapsed}");
    }

    [Fact]
    public void BuildDetachedCommand_Unix()
    {
        var (file, args) = ProcessRunner.BuildDetachedCommand("/usr/bin/x", ["a b", "c"], isWindows: false, hasSetsid: true);
        Assert.Equal("/bin/sh", file);
        Assert.Equal(["-c", "exec setsid \"$0\" \"$@\" </dev/null >/dev/null 2>&1", "/usr/bin/x", "a b", "c"], args);

        (file, args) = ProcessRunner.BuildDetachedCommand("/usr/bin/x", [], isWindows: false, hasSetsid: false);
        Assert.Equal("/bin/sh", file);
        Assert.Equal(["-c", "exec \"$0\" \"$@\" </dev/null >/dev/null 2>&1", "/usr/bin/x"], args);
    }

    [Fact]
    public void BuildDetachedCommand_Unix_PassesTheWorkingDirectoryAsAnArgument()
    {
        var (file, args) = ProcessRunner.BuildDetachedCommand("/usr/bin/x", ["a"], isWindows: false, hasSetsid: false, workingDirectory: "/d; rm -rf \"x\"");
        Assert.Equal("/bin/sh", file);
        Assert.Equal(["-c", "cd -- \"$1\" && shift && exec \"$0\" \"$@\" </dev/null >/dev/null 2>&1", "/usr/bin/x", "/d; rm -rf \"x\"", "a"], args);
    }

    [Fact]
    public void BuildDetachedCommand_Windows_RunsDirectly()
    {
        var (file, args) = ProcessRunner.BuildDetachedCommand("C:\\x.exe", ["a"], isWindows: true, hasSetsid: false);
        Assert.Equal("C:\\x.exe", file);
        Assert.Equal(["a"], args);
    }

    [Fact]
    public void StartDetached_ReturnsQuicklyAndDoesNotInheritStdout()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "relies on /bin/sh");
        Environment.SetEnvironmentVariable("TEST_SLEEP_SECONDS", "3");
        var runner = new ProcessRunner();
        // Run the detach command with stdout a pipe we read to EOF: if the helper inherited that pipe, EOF would not come until it exits (3 s).
        var psi = new ProcessStartInfo() { UseShellExecute = false, RedirectStandardOutput = true };
        var (file, args) = ProcessRunner.BuildDetachedCommand(_helper, [], false, runner.Which("setsid") is not null);
        psi.FileName = file;
        foreach (var a in args) psi.ArgumentList.Add(a);
        var sw = Stopwatch.StartNew();
        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"pipe held open for {sw.Elapsed}");
    }
}
