using Sakshi.App.Tray;
using Sakshi.Platform;
using Sakshi.TestSupport;

namespace Sakshi.App.Tests;

[Collection("Environment")]
public sealed class ForwardingTests : IDisposable
{
    private readonly TempHome _home = new();
    private readonly ShortDir _short = new();
    private readonly string _stateDir;
    private readonly CancellationTokenSource _cts = new(TimeSpan.FromSeconds(60));

    public ForwardingTests()
    {
        // TempHome's state dir is under the (on macOS long) temp dir: use a short one so the socket path fits.
        _stateDir = Path.Combine(_short.Root, "state");
        Directory.CreateDirectory(_stateDir);
        Environment.SetEnvironmentVariable("SAKSHI_STATE_DIR", _stateDir);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("SAKSHI_STATE_DIR", _home.StateDir);
        _cts.Cancel();
        _cts.Dispose();
        _home.Dispose();
        _short.Dispose();
    }

    // Commands computes the same path; it must be inside this test's own temp dir, never a real tray's socket.
    private string Sock
    {
        get
        {
            var (path, isPrivate) = TrayInstance.SocketPathFor(_stateDir, Environment.GetEnvironmentVariable);
            Assert.False(isPrivate);
            Assert.StartsWith(_short.Root + Path.DirectorySeparatorChar, path);
            return path;
        }
    }

    private static TrayLaunch Launch(FakeProcessRunner r, double forSeconds = 10) =>
        new(r, "/opt/sakshi/sakshi", TimeSpan.FromMilliseconds(50), TimeSpan.FromSeconds(forSeconds));

    private async Task<(int Code, string Out, string Err)> Run(TrayLaunch launch, params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var code = await Commands.RunAsync(args, o, e, _home.Home, isTty: false, _cts.Token, launch);
        return (code, o.ToString(), e.ToString());
    }

    [Theory]
    [InlineData("start")]
    [InlineData("stop")]
    [InlineData("toggle")]
    [InlineData("housekeep")]
    [InlineData("migrate")]
    public async Task RunningTray_GetsTheCommand_AndItsAnswerIsPrinted(string command)
    {
        IReadOnlyList<string>? got = null;
        var server = TrayIpc.ServeAsync(Sock, (a, _) =>
        {
            got = a;
            return Task.FromResult(new CommandResult(3, "out text\n", "err text\n"));
        }, _cts.Token);
        for (var i = 0; i < 200 && !File.Exists(Sock); i++) await Task.Delay(25, TestContext.Current.CancellationToken);
        var runner = new FakeProcessRunner();
        var (code, o, e) = await Run(Launch(runner), command);
        Assert.Equal(3, code);
        Assert.Equal("out text\n", o);
        Assert.Equal("err text\n", e);
        Assert.Equal([command], got);
        Assert.Empty(runner.Detached);
        _cts.Cancel();
        await server;
    }

    [Fact]
    public async Task Import_SendsAbsolutePath()
    {
        IReadOnlyList<string>? got = null;
        var server = TrayIpc.ServeAsync(Sock, (a, _) => { got = a; return Task.FromResult(new CommandResult(0, "", "")); }, _cts.Token);
        for (var i = 0; i < 200 && !File.Exists(Sock); i++) await Task.Delay(25, TestContext.Current.CancellationToken);
        var (code, _, _) = await Run(Launch(new FakeProcessRunner()), "import", Path.Combine("rel", "v.mp4"), "Name");
        Assert.Equal(0, code);
        Assert.Equal(["import", Path.GetFullPath(Path.Combine("rel", "v.mp4")), "Name"], got);
        _cts.Cancel();
        await server;
    }

    [Fact]
    public async Task NoTray_StartsOneDetached_ThenRetriesUntilItAnswers()
    {
        var runner = new FakeProcessRunner();
        Task? server = null;
        var starter = Task.Run(async () =>
        {
            while (true)
            {
                lock (runner.Gate) if (runner.Detached.Count > 0) break;
                await Task.Delay(10, TestContext.Current.CancellationToken);
            }
            await Task.Delay(400, TestContext.Current.CancellationToken); // the tray takes a moment to come up
            server = TrayIpc.ServeAsync(Sock, (a, _) => Task.FromResult(new CommandResult(0, "toggled " + a[0], "")), _cts.Token);
        }, TestContext.Current.CancellationToken);
        var (code, o, _) = await Run(Launch(runner), "toggle");
        await starter;
        Assert.Equal(0, code);
        Assert.Equal("toggled toggle", o);
        lock (runner.Gate)
        {
            var (file, args, _) = Assert.Single(runner.Detached);
            Assert.Equal("/opt/sakshi/sakshi", file);
            Assert.Equal(["tray"], args);
        }
        _cts.Cancel();
        await server!;
    }

    [Fact]
    public async Task NoTray_NeverComesUp_Exit1_WithMessage()
    {
        var runner = new FakeProcessRunner();
        var (code, _, e) = await Run(Launch(runner, forSeconds: 0.5), "start");
        Assert.Equal(1, code);
        Assert.Contains("Could not reach the sakshi tray (see sakshi.log).", e);
        Assert.Single(runner.Detached);
    }

    [Fact]
    public async Task NoTray_CannotBeStarted_Exit1()
    {
        var runner = new ThrowingRunner();
        var (code, _, e) = await Run(Launch(new FakeProcessRunner()) with { Runner = runner }, "start");
        Assert.Equal(1, code);
        Assert.Contains("Could not start the sakshi tray", e);
    }

    [Fact]
    public async Task NoTray_StartThrowsAnyException_Exit1_AndLogged()
    {
        var (code, _, e) = await Run(Launch(new FakeProcessRunner()) with { Runner = new ThrowingRunner(new InvalidOperationException("kaput")) }, "start");
        Assert.Equal(1, code);
        Assert.Contains("Could not start the sakshi tray: kaput", e);
        Assert.Contains("kaput", File.ReadAllText(Path.Combine(_stateDir, "sakshi.log")));
    }

    // From a keybinding nobody sees stderr: on Linux a failure is also a critical desktop notification
    // (through the injected runner, so no real notify-send) and a line in sakshi.log.
    private static void LinuxOnly()
    {
        if (!OperatingSystem.IsLinux()) Assert.Skip("desktop notifications from the CLI are Linux-only in stage 2");
    }

    private string LogText => File.ReadAllText(Path.Combine(_stateDir, "sakshi.log"));

    [Fact]
    public async Task NoTray_NeverComesUp_SendsACriticalNotification_AndLogs()
    {
        LinuxOnly();
        var runner = new FakeProcessRunner();
        runner.OnPath["notify-send"] = "/usr/bin/notify-send";
        var (code, _, _) = await Run(Launch(runner, forSeconds: 0.5), "toggle");

        Assert.Equal(1, code);
        var call = Assert.Single(runner.Calls, c => c.File == "notify-send");
        Assert.Equal(["-a", "Sakshi", "-u", "critical", "Sakshi", "Could not reach the sakshi tray (see sakshi.log)."], call.Args);
        Assert.Contains("Could not reach the sakshi tray", LogText);
    }

    [Fact]
    public async Task NoTray_CannotBeStarted_SendsACriticalNotification_AndLogs()
    {
        LinuxOnly();
        var notifier = new FakeProcessRunner();
        notifier.OnPath["notify-send"] = "/usr/bin/notify-send";
        var runner = new FailingStartRunner(notifier);
        var (code, _, _) = await Run(Launch(notifier) with { Runner = runner }, "toggle");

        Assert.Equal(1, code);
        var call = Assert.Single(notifier.Calls, c => c.File == "notify-send");
        Assert.Equal(["-a", "Sakshi", "-u", "critical", "Sakshi", "Could not start the sakshi tray: no such file"], call.Args);
        Assert.Contains("Could not start the sakshi tray: no such file", LogText);
    }

    /// <summary>Starting the tray fails; everything else goes to the fake.</summary>
    private sealed class FailingStartRunner(FakeProcessRunner inner) : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(string file, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct) => inner.RunAsync(file, args, timeout, ct);
        public Task<int> RunAttachedAsync(string file, IReadOnlyList<string> args, IReadOnlyDictionary<string, string?> env, CancellationToken ct) => inner.RunAttachedAsync(file, args, env, ct);
        public int StartDetached(string file, IReadOnlyList<string> args, string? workingDirectory = null) => throw new System.ComponentModel.Win32Exception("no such file");
        public bool IsRunning(string processName) => inner.IsRunning(processName);
        public string? Which(string command) => inner.Which(command);
    }

    [Fact]
    public async Task CtrlC_WhileWaitingForTheTray_Exit130()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        var o = new StringWriter();
        var e = new StringWriter();
        var run = Commands.RunAsync(["toggle"], o, e, _home.Home, isTty: false, cts.Token, Launch(new FakeProcessRunner(), forSeconds: 30));
        await Task.Delay(300, TestContext.Current.CancellationToken);
        cts.Cancel();
        Assert.Equal(130, await run);
        Assert.DoesNotContain("   at ", e.ToString());
    }

    [Fact]
    public async Task DryRunHousekeep_StillRunsLocally_WithoutTheTray()
    {
        var runner = new FakeProcessRunner();
        var (code, _, _) = await Run(Launch(runner), "housekeep", "-n");
        Assert.Equal(0, code);
        Assert.Empty(runner.Detached);
    }

    private sealed class ThrowingRunner(Exception? error = null) : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(string file, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct) => throw new NotSupportedException();
        public Task<int> RunAttachedAsync(string file, IReadOnlyList<string> args, IReadOnlyDictionary<string, string?> env, CancellationToken ct) => throw new NotSupportedException();
        public int StartDetached(string file, IReadOnlyList<string> args, string? workingDirectory = null) => throw error ?? new System.ComponentModel.Win32Exception("no such file");
        public bool IsRunning(string processName) => false;
        public string? Which(string command) => null;
    }
}
