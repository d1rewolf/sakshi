using Sakshi.App.Tests.Prompts;
using Sakshi.App.Tray;
using Sakshi.Core.State;

namespace Sakshi.App.Tests;

public sealed class TrayUiTests : IDisposable
{
    // Where the socket goes if the state dir's path is too long for one (macOS's $TMPDIR): never the real /tmp/sakshi-<user>.
    private readonly ShortDir _short = new();

    public void Dispose() => _short.Dispose();

    private TrayInstance? Acquire(TrayFixture f) =>
        TrayInstance.TryAcquire(Path.GetDirectoryName(f.StateFile)!, _ => null, _short.Root);

    private static readonly TrayIconState[] States =
        [TrayIconState.Idle, TrayIconState.Recording, TrayIconState.RecordingDim, TrayIconState.Busy, TrayIconState.Transcribing];

    [Fact]
    public void Icon_pixels_differ_per_state_and_are_deterministic()
    {
        var seen = new List<byte[]>();
        foreach (var s in States)
        {
            var px = Icons.Pixels(s);
            Assert.Equal(Icons.Size * Icons.Size * 4, px.Length);
            Assert.Equal(px, Icons.Pixels(s));
            seen.Add(px);
        }
        for (var i = 0; i < seen.Count; i++)
            for (var j = i + 1; j < seen.Count; j++)
                Assert.NotEqual(seen[i], seen[j]);
    }

    [Fact]
    public void Centre_pixel_colours_follow_the_brief()
    {
        (byte B, byte G, byte R, byte A) Centre(TrayIconState s)
        {
            var px = Icons.Pixels(s);
            var o = (16 * Icons.Size + 16) * 4;
            return (px[o], px[o + 1], px[o + 2], px[o + 3]);
        }
        var rec = Centre(TrayIconState.Recording);
        Assert.True(rec.R > 200 && rec.G < 90 && rec.B < 90 && rec.A == 255);
        var blue = Centre(TrayIconState.Transcribing);
        Assert.True(blue.B > 200 && blue.R < 90 && blue.A == 255);
        var dim = Centre(TrayIconState.RecordingDim);
        Assert.True(dim.R < rec.R && dim.R > dim.G);
        Assert.Equal(0, Centre(TrayIconState.Busy).A);   // a ring: the middle is clear
        var idle = Centre(TrayIconState.Idle);
        Assert.True(idle.A == 255 && idle.R < 160);
    }

    [Fact]
    public Task Render_makes_a_32x32_bitmap_per_state() => HeadlessHost.Run(() =>
    {
        foreach (var s in States)
        {
            var bmp = Icons.Render(s);
            Assert.Equal(32, bmp.PixelSize.Width);
            Assert.Equal(32, bmp.PixelSize.Height);
        }
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData("idle", true, TrayIconState.Idle)]
    [InlineData("recording", true, TrayIconState.Recording)]
    [InlineData("recording", false, TrayIconState.RecordingDim)]
    [InlineData("starting", true, TrayIconState.Busy)]
    [InlineData("naming", false, TrayIconState.Busy)]
    [InlineData("transcribing", true, TrayIconState.Transcribing)]
    [InlineData("whatever", true, TrayIconState.Idle)]
    public void Icon_follows_status_and_blink(string state, bool blink, TrayIconState expected) =>
        Assert.Equal(expected, Icons.StateFor(state, blink));

    [Theory]
    [InlineData("idle", "Start recording")]
    [InlineData("recording", "Stop recording")]
    [InlineData("starting", "Start recording")]
    [InlineData("naming", "Name recording")]
    [InlineData("transcribing", "Start recording")]
    public void Menu_label_follows_state(string state, string label)
    {
        var view = TrayView.For(new Status(state, "tip", ""), blinkOn: true);
        Assert.Equal(label, view.ToggleLabel);
        Assert.Equal("tip", view.StatusLine);
    }

    [Theory]
    [InlineData("idle", false)]
    [InlineData("starting", false)]
    [InlineData("recording", true)]
    [InlineData("naming", false)]
    [InlineData("transcribing", false)]
    public void Repick_is_offered_only_while_recording(string state, bool enabled) =>
        Assert.Equal(enabled, TrayView.For(new Status(state, "tip", ""), blinkOn: true).Recording);

    // On Linux, Avalonia uses the tooltip as the tray item's ID, and the bar pins
    // icons by that ID; a tooltip that changes would drop the pin mid-recording.
    [Theory]
    [InlineData("idle", "Record a meeting (OBS)")]
    [InlineData("recording", "Recording meeting (0:12) — click to stop")]
    [InlineData("transcribing", "Transcribing standup (1:05)")]
    public void Tooltip_never_changes(string state, string statusText) =>
        Assert.Equal("Sakshi", TrayView.For(new Status(state, statusText, "0:12"), blinkOn: true).Tooltip);

    [Theory]
    [InlineData(new string[0], ProgramRoute.Tray)]
    [InlineData(new[] { "tray" }, ProgramRoute.Tray)]
    [InlineData(new[] { "status" }, ProgramRoute.Cli)]
    [InlineData(new[] { "--help" }, ProgramRoute.Cli)]
    [InlineData(new[] { "find", "tray" }, ProgramRoute.Cli)]
    public void Route_sends_no_args_and_tray_to_the_tray(string[] args, ProgramRoute expected) =>
        Assert.Equal(expected, Program.Route(args));

    private Task<(TrayFixture F, TrayHost Host, TrayInstance Instance)> Host()
    {
        var f = new TrayFixture();
        var controller = f.Controller();
        var instance = Acquire(f)!;
        Assert.NotNull(instance);
        var host = new TrayHost(controller.Services, controller, instance);
        return Task.FromResult((f, host, instance));
    }

    [Fact]
    public async Task Quit_cancels_ipc_and_background_and_releases_the_lock()
    {
        var (f, host, instance) = await Host();
        await using var _ = f;
        try
        {
            host.Start();
            Assert.False(host.QuitToken.IsCancellationRequested);
            Assert.False(host.Ipc.IsCompleted);
            Assert.False(host.Background.IsCompleted);

            await host.StopAsync(TimeSpan.FromSeconds(10));

            Assert.True(host.QuitToken.IsCancellationRequested);
            Assert.True(host.Ipc.IsCompleted);
            Assert.True(host.Background.IsCompleted);
            var again = Acquire(f);
            Assert.NotNull(again);   // the lock is free: another tray could start
            await again!.DisposeAsync();
        }
        finally { await instance.DisposeAsync(); }
    }

    [Fact]
    public async Task Quit_completes_with_a_connected_but_silent_client()
    {
        var (f, host, instance) = await Host();
        await using var _ = f;
        try
        {
            host.Start();
            using var client = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.Unix,
                System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Unspecified);
            await TrayFixture.Until(() => File.Exists(instance.SocketPath), "the socket");
            await client.ConnectAsync(new System.Net.Sockets.UnixDomainSocketEndPoint(instance.SocketPath),
                TestContext.Current.CancellationToken);
            await Task.Delay(300, TestContext.Current.CancellationToken);   // let the server accept and start reading

            var stop = host.StopAsync(TimeSpan.FromSeconds(10));
            await stop.WaitAsync(TimeSpan.FromSeconds(8), TestContext.Current.CancellationToken);

            Assert.True(host.Ipc.IsCompletedSuccessfully);
            var again = Acquire(f);
            Assert.NotNull(again);
            await again!.DisposeAsync();
        }
        finally { await instance.DisposeAsync(); }
    }

    [Fact]
    public async Task Open_meetings_and_config_use_the_desktop_opener_through_the_runner()
    {
        var (f, host, instance) = await Host();
        await using var _ = f;
        await using var __ = instance;
        host.OpenMeetings();
        host.OpenConfig();
        await TrayFixture.Until(() => { lock (f.Runner.Gate) return f.Runner.Detached.Count == 2; }, "two xdg-open launches");
        lock (f.Runner.Gate)
        {
            Assert.Contains(f.Runner.Detached, d => d.Item1 == "xdg-open" && d.Item2.SequenceEqual([f.Config.MeetingsDir]));
            Assert.Contains(f.Runner.Detached, d => d.Item1 == "xdg-open" && d.Item2.Count == 1 && d.Item2[0].EndsWith("config.json"));
        }
    }

    [Fact]
    public async Task On_a_fresh_machine_open_creates_the_meetings_folder_and_a_starter_config_first()
    {
        var (f, host, instance) = await Host();
        await using var _ = f;
        await using var __ = instance;
        Directory.Delete(f.Meetings, true);
        var configFile = host.Services.Paths.ConfigFile;
        Assert.False(File.Exists(configFile));

        host.OpenMeetings();
        host.OpenConfig();
        await TrayFixture.Until(() => { lock (f.Runner.Gate) return f.Runner.Detached.Count == 2; }, "two xdg-open launches");

        Assert.True(Directory.Exists(f.Meetings));
        Assert.Contains("\"obsSource\"", File.ReadAllText(configFile));
        Assert.Equal(Sakshi.Core.Config.SakshiConfig.Defaults(Sakshi.Core.Paths.HostOs.Linux, f.Root).ObsSource,
            Sakshi.Core.Config.ConfigStore.Load(configFile, Sakshi.Core.Paths.HostOs.Linux, f.Root).ObsSource);
    }

    [Fact]
    public async Task Open_config_leaves_an_existing_config_alone()
    {
        var (f, host, instance) = await Host();
        await using var _ = f;
        await using var __ = instance;
        var configFile = host.Services.Paths.ConfigFile;
        Directory.CreateDirectory(Path.GetDirectoryName(configFile)!);
        File.WriteAllText(configFile, "{\"keepDays\": 3}");

        host.OpenConfig();
        await TrayFixture.Until(() => { lock (f.Runner.Gate) return f.Runner.Detached.Count == 1; }, "an xdg-open launch");

        Assert.Equal("{\"keepDays\": 3}", File.ReadAllText(configFile));
    }

    [Fact]
    public async Task Toggle_returns_before_the_controller_runs_and_never_throws()
    {
        var (f, host, instance) = await Host();
        await using var _ = f;
        await using var __ = instance;
        f.Launcher.Block = new TaskCompletionSource();   // a start blocks inside the launcher
        f.ObsRunning(false);
        host.Toggle();                                   // returns although the command is stuck
        try
        {
            await f.Launcher.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Equal(1, f.Launcher.Calls);
        }
        finally { f.Launcher.Block.SetResult(); }
    }
}
