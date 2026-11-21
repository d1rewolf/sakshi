using Sakshi.App.Tray;
using Sakshi.Core.Paths;

namespace Sakshi.App.Tests;

/// <summary>
/// The tray's startup before Avalonia. Pressed from a keybinding, nobody sees its stderr: every
/// failure must also be in sakshi.log. Never starts a UI.
/// </summary>
public sealed class TrayEntryTests : IDisposable
{
    private readonly ShortDir _short = new();
    private readonly StringWriter _out = new();
    private readonly StringWriter _err = new();
    private readonly SakshiPaths _paths;

    public TrayEntryTests()
    {
        _paths = new SakshiPaths(Path.Combine(_short.Root, "config"), Path.Combine(_short.Root, "state"),
            Path.Combine(_short.Root, "data"), Path.Combine(_short.Root, "obs"));
    }

    public void Dispose() => _short.Dispose();

    private string Log => File.Exists(_paths.LogFile) ? File.ReadAllText(_paths.LogFile) : "";

    private TrayInstance? Acquire() => TrayInstance.TryAcquire(_paths.StateDir, _ => null, _short.Root);

    private static TrayServices NoServices() => throw new InvalidOperationException("create must not be called");

    [Fact]
    public void LockDirectoryNotOurs_Exit1_AndLogged()
    {
        var (code, host) = TrayEntry.Prepare(_paths, _out, _err,
            () => throw new InvalidOperationException("private socket directory /tmp/sakshi-x is not ours; refusing to use it"),
            NoServices);

        Assert.Equal(1, code);
        Assert.Null(host);
        Assert.Contains("private socket directory /tmp/sakshi-x is not ours", _err.ToString());
        Assert.Contains("private socket directory /tmp/sakshi-x is not ours", Log);
    }

    [Fact]
    public void LockFails_AnyOtherWay_Exit1_AndLogged()
    {
        var (code, host) = TrayEntry.Prepare(_paths, _out, _err,
            () => throw new UnauthorizedAccessException("state dir is read-only"), NoServices);

        Assert.Equal(1, code);
        Assert.Null(host);
        Assert.Contains("state dir is read-only", _err.ToString());
        Assert.Contains("state dir is read-only", Log);
    }

    [Fact]
    public async Task AlreadyRunning_Exit0_NotAFailure()
    {
        await using var other = Acquire();
        Assert.NotNull(other);

        var (code, host) = TrayEntry.Prepare(_paths, _out, _err, Acquire, NoServices);

        Assert.Equal(0, code);
        Assert.Null(host);
        Assert.Equal("sakshi is already running" + Environment.NewLine, _out.ToString());
        Assert.Equal("", Log);
    }

    [Fact]
    public async Task BrokenConfig_Exit1_Logged_AndTheLockIsReleased()
    {
        var (code, host) = TrayEntry.Prepare(_paths, _out, _err, Acquire,
            () => throw new InvalidDataException("config.json is not valid JSON"));

        Assert.Equal(1, code);
        Assert.Null(host);
        Assert.Contains("Error: config.json is not valid JSON", _err.ToString());
        Assert.Contains("config.json is not valid JSON", Log);
        await using var again = Acquire();
        Assert.NotNull(again);
    }

    [Fact]
    public async Task Success_ReturnsTheHost_WithoutStartingIt()
    {
        await using var f = new TrayFixture();
        var services = f.Controller().Services;

        var (code, host) = TrayEntry.Prepare(_paths, _out, _err, Acquire, () => services);

        Assert.Equal(0, code);
        Assert.NotNull(host);
        Assert.True(host!.Ipc.IsCompleted);   // not started: TrayApp starts it once Avalonia runs
        await host.StopAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("", Log);
    }
}
