using System.Net.Sockets;
using System.Text;
using Sakshi.App.Tray;
using Sakshi.TestSupport;

namespace Sakshi.App.Tests;

[Collection("Environment")]
public sealed class IpcTests : IDisposable
{
    private readonly TempHome _home = new();
    private readonly ShortDir _short = new();
    private readonly CancellationTokenSource _cts = new(TimeSpan.FromSeconds(60));

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        _home.Dispose();
        _short.Dispose();
    }

    private static UnixFileMode Mode(string path) =>
        OperatingSystem.IsWindows() ? throw new PlatformNotSupportedException() : File.GetUnixFileMode(path);

    // Tests must never touch a real tray's socket: every path they use lives under the test's own temp dir.
    private string Own(string path)
    {
        Assert.StartsWith(_short.Root + Path.DirectorySeparatorChar, path);
        return path;
    }

    // Short on every OS (macOS $TMPDIR and %TEMP% are too long for a socket path); see ShortDir.
    private string StateDir
    {
        get
        {
            var d = Path.Combine(_short.Root, "state");
            Directory.CreateDirectory(d);
            return d;
        }
    }

    private string Sock => Own(Path.Combine(StateDir, "tray.sock"));
    private string TmpRoot => _short.Root;

    private static Task<CommandResult> Echo(IReadOnlyList<string> args, CancellationToken ct) =>
        Task.FromResult(new CommandResult(args.Count, string.Join("|", args), "err"));

    private Task Serve(Func<IReadOnlyList<string>, CancellationToken, Task<CommandResult>> h, string? path = null) =>
        TrayIpc.ServeAsync(path ?? Sock, h, _cts.Token);

    private static async Task WaitForSocket(string path)
    {
        for (var i = 0; i < 200 && !File.Exists(path); i++) await Task.Delay(25, TestContext.Current.CancellationToken);
        Assert.True(File.Exists(path), "server did not create its socket");
    }

    private static string RawExchange(string path, string request)
    {
        using var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        s.Connect(new UnixDomainSocketEndPoint(path));
        s.Send(Encoding.UTF8.GetBytes(request));
        var buf = new List<byte>();
        var one = new byte[1];
        while (s.Receive(one) == 1 && one[0] != (byte)'\n') buf.Add(one[0]);
        return Encoding.UTF8.GetString(buf.ToArray());
    }

    [Fact]
    public async Task RoundTrip_CarriesArgsAndResult()
    {
        var server = Serve(Echo);
        await WaitForSocket(Sock);
        var r = await TrayIpc.SendAsync(Sock, ["import", "/tmp/a b.mp4", "Naïve “name”\n2"], TimeSpan.FromSeconds(10), _cts.Token);
        Assert.Equal(new CommandResult(3, "import|/tmp/a b.mp4|Naïve “name”\n2", "err"), r);
        _cts.Cancel();
        await server;
    }

    [Fact]
    public async Task NoServer_ReturnsNull_Quickly()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.Null(await TrayIpc.SendAsync(Sock, ["start"], TimeSpan.FromSeconds(10), _cts.Token));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1.5));
    }

    [Fact]
    public async Task StaleSocketFile_NoListener_ReturnsNull()
    {
        if (OperatingSystem.IsWindows()) Assert.Skip("a plain file at the socket path behaves differently on Windows");
        File.WriteAllText(Sock, "");
        Assert.Null(await TrayIpc.SendAsync(Sock, ["start"], TimeSpan.FromSeconds(10), _cts.Token));
    }

    [Fact]
    public async Task TwoConcurrentClients_EachGetTheirOwnAnswer_WhileOneIsSlow()
    {
        var release = new TaskCompletionSource();
        var server = Serve(async (a, ct) =>
        {
            if (a[0] == "slow") await release.Task;
            return new CommandResult(0, a[0], "");
        });
        await WaitForSocket(Sock);
        var slow = TrayIpc.SendAsync(Sock, ["slow"], TimeSpan.FromSeconds(30), _cts.Token);
        var fast = await TrayIpc.SendAsync(Sock, ["fast"], TimeSpan.FromSeconds(30), _cts.Token);
        Assert.Equal("fast", fast!.StdOut);
        Assert.False(slow.IsCompleted);
        release.SetResult();
        Assert.Equal("slow", (await slow)!.StdOut);
        _cts.Cancel();
        await server;
    }

    [Fact]
    public async Task MalformedRequest_ReplyBadRequest()
    {
        var server = Serve(Echo);
        await WaitForSocket(Sock);
        foreach (var bad in new[] { "not json\n", "{\"args\":5}\n", "{}\n", "{\"args\":[1]}\n", "[]\n" })
        {
            var reply = RawExchange(Sock, bad);
            Assert.Contains("\"exit\":2", reply);
            Assert.Contains("bad request", reply);
        }
        _cts.Cancel();
        await server;
    }

    [Fact]
    public async Task HandlerException_ReplyExit1WithMessage()
    {
        var server = Serve((_, _) => throw new InvalidOperationException("boom"));
        await WaitForSocket(Sock);
        var r = await TrayIpc.SendAsync(Sock, ["start"], TimeSpan.FromSeconds(10), _cts.Token);
        Assert.Equal(1, r!.ExitCode);
        Assert.Contains("boom", r.StdErr);
        _cts.Cancel();
        await server;
    }

    [Fact]
    public async Task ReplyTimeout_ReturnsAnErrorResult_NotNull()
    {
        var server = Serve(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return new CommandResult(0, "", ""); });
        await WaitForSocket(Sock);
        var r = await TrayIpc.SendAsync(Sock, ["start"], TimeSpan.FromMilliseconds(300), _cts.Token);
        Assert.NotNull(r);
        Assert.Equal(1, r.ExitCode);
        _cts.Cancel();
        await server;
    }

    [Fact]
    public async Task Server_StopsWhenCancelled()
    {
        using var cts = new CancellationTokenSource();
        var server = TrayIpc.ServeAsync(Sock, Echo, cts.Token);
        await WaitForSocket(Sock);
        cts.Cancel();
        await server.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task SocketFile_IsOwnerOnly_OnUnix()
    {
        if (OperatingSystem.IsWindows()) Assert.Skip("Unix file modes");
        var server = Serve(Echo);
        await WaitForSocket(Sock);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, Mode(Sock));
        _cts.Cancel();
        await server;
    }

    [Fact]
    public async Task TrayInstance_SecondAcquireRefused_UntilFirstDisposed()
    {
        await using (var first = TrayInstance.TryAcquire(StateDir, _ => null, TmpRoot))
        {
            Assert.NotNull(first);
            Assert.Null(TrayInstance.TryAcquire(StateDir, _ => null, TmpRoot));
        }
        await using var again = TrayInstance.TryAcquire(StateDir, _ => null, TmpRoot);
        Assert.NotNull(again);
    }

    [Fact]
    public async Task TrayInstance_RemovesStaleSocket_AndDeletesItOnDispose()
    {
        if (OperatingSystem.IsWindows()) Assert.Skip("a plain file at the socket path behaves differently on Windows");
        File.WriteAllText(Sock, "stale");
        var inst = TrayInstance.TryAcquire(StateDir, _ => null, TmpRoot);
        Assert.NotNull(inst);
        Assert.False(File.Exists(inst.SocketPath));
        File.WriteAllText(inst.SocketPath, "mine");
        await inst.DisposeAsync();
        Assert.False(File.Exists(inst.SocketPath));
    }

    [Fact]
    public async Task TrayInstance_ServerAndClient_Work()
    {
        await using var inst = TrayInstance.TryAcquire(StateDir, _ => null, TmpRoot);
        Assert.NotNull(inst);
        var server = Serve(Echo, inst.SocketPath);
        await WaitForSocket(inst.SocketPath);
        Assert.Equal("toggle", (await TrayIpc.SendAsync(inst.SocketPath, ["toggle"], TimeSpan.FromSeconds(10), _cts.Token))!.StdOut);
        _cts.Cancel();
        await server;
    }

    [Fact]
    public void SocketPathFor_TypicalMacStateDir_StaysUnderTheSunPathLimit()
    {
        var home = Path.Combine("/Users", "averagename");
        var state = Path.Combine(home, "Library", "Application Support", "Sakshi", "state");
        var (p, isPrivate) = TrayInstance.SocketPathFor(state, _ => null, TmpRoot);
        Assert.False(isPrivate);
        Assert.Equal(Path.Combine(state, "tray.sock"), p);
        Assert.True(Encoding.UTF8.GetByteCount(p) <= 103, p);
        // a 40-character user name still fits; a much longer one falls back to the private dir
        var longer = Path.Combine("/Users", new string('u', 40), "Library", "Application Support", "Sakshi", "state");
        Assert.False(TrayInstance.SocketPathFor(longer, _ => null, TmpRoot).IsPrivateDir);
        var tooLong = Path.Combine("/Users", new string('u', 60), "Library", "Application Support", "Sakshi", "state");
        Assert.True(TrayInstance.SocketPathFor(tooLong, _ => null, TmpRoot).IsPrivateDir);
    }

    [Fact]
    public void SocketPathFor_ShortStateDir_UsesIt()
    {
        Assert.Equal((Path.Combine("/s", "state", "tray.sock"), false),
            TrayInstance.SocketPathFor(Path.Combine("/s", "state"), _ => null, TmpRoot));
    }

    [Fact]
    public void SocketPathFor_LongStateDir_FallsBackToRuntimeDir_OnLinux()
    {
        if (!OperatingSystem.IsLinux()) Assert.Skip("XDG_RUNTIME_DIR is the Linux fallback");
        var longDir = "/" + new string('a', 120) + "/state";
        var p = TrayInstance.SocketPathFor(longDir, n => n == "XDG_RUNTIME_DIR" ? "/run/user/1000" : null, TmpRoot);
        Assert.Equal(("/run/user/1000/sakshi-tray.sock", false), p);
    }

    [Fact]
    public void SocketPathFor_LongStateDir_NoRuntimeDir_UsesPrivateDirUnderTheRoot()
    {
        var longDir = "/" + new string('a', 120) + "/state";
        var (p, isPrivate) = TrayInstance.SocketPathFor(longDir, _ => null, TmpRoot);
        Assert.True(isPrivate);
        Assert.StartsWith(Path.Combine(TmpRoot, "sakshi-"), p);
        Assert.EndsWith("tray.sock", p);
        Assert.True(Encoding.UTF8.GetByteCount(p) <= 103,
            $"test precondition: the fallback path '{p}' must fit in sun_path; the test's TmpRoot is too long");
    }

    [Fact]
    public async Task TrayInstance_LongStateDir_UsesPrivateDir0700_AndWorks()
    {
        if (OperatingSystem.IsWindows()) Assert.Skip("Unix fallback");
        var longDir = Path.Combine(_home.Root, new string('x', 110), "state");
        await using var inst = TrayInstance.TryAcquire(longDir, _ => null, TmpRoot);
        Assert.NotNull(inst);
        Own(inst.SocketPath);
        Assert.StartsWith(TmpRoot, inst.SocketPath);
        var dir = Path.GetDirectoryName(inst.SocketPath)!;
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, Mode(dir));
        var server = Serve(Echo, inst.SocketPath);
        await WaitForSocket(inst.SocketPath);
        Assert.Equal("x", (await TrayIpc.SendAsync(inst.SocketPath, ["x"], TimeSpan.FromSeconds(10), _cts.Token))!.StdOut);
        _cts.Cancel();
        await server;
    }

    [Fact]
    public void TrayInstance_PrivateDirNotUsable_ThrowsClearReason()
    {
        var longDir = Path.Combine(_home.Root, new string('x', 110), "state");
        var (sock, _) = TrayInstance.SocketPathFor(longDir, _ => null, TmpRoot);
        Directory.CreateDirectory(TmpRoot);
        File.WriteAllText(Path.GetDirectoryName(sock)!, "squatter"); // a file where the directory must be
        var e = Assert.Throws<InvalidOperationException>(() => TrayInstance.TryAcquire(longDir, _ => null, TmpRoot));
        Assert.Contains("is not ours; refusing to use it", e.Message);
        Assert.Contains(Path.GetDirectoryName(sock)!, e.Message);
        // and the lock was released again
        Assert.Throws<InvalidOperationException>(() => TrayInstance.TryAcquire(longDir, _ => null, TmpRoot));
    }

    [Fact]
    public async Task LargeReply_UpTo16MiB_RoundTrips()
    {
        var big = new string('é', 3 << 20); // 6 MiB of UTF-8
        var server = Serve((_, _) => Task.FromResult(new CommandResult(0, big, "")));
        await WaitForSocket(Sock);
        var r = await TrayIpc.SendAsync(Sock, ["status"], TimeSpan.FromSeconds(30), _cts.Token);
        Assert.Equal(big, r!.StdOut);
        _cts.Cancel();
        await server;
    }

    [Fact]
    public async Task OversizedRequest_ReplyBadRequest()
    {
        var server = Serve(Echo);
        await WaitForSocket(Sock);
        var r = await TrayIpc.SendAsync(Sock, ["import", new string('a', (1 << 20) + 10)], TimeSpan.FromSeconds(30), _cts.Token);
        Assert.Equal(2, r!.ExitCode);
        Assert.Contains("bad request", r.StdErr);
        _cts.Cancel();
        await server;
    }
}
