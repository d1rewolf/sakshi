using System.Text.Json;
using Sakshi.Core.Config;
using Sakshi.Core.Obs;
using Sakshi.Core.Ports;
using Sakshi.Core.Recording;
using Sakshi.TestSupport;

namespace Sakshi.Core.Tests;

public sealed class FreezeWatchTests : IAsyncDisposable
{
    private readonly FakeNotifier _notifier = new();
    private readonly FakeClock _clock = new(new DateTimeOffset(2026, 11, 20, 9, 0, 0, TimeSpan.Zero));
    private readonly List<string> _log = [];
    private readonly FakeRecovery _recovery = new();
    private readonly FakeObsServer _server;
    private string? _picture = "frame-1";      // null: the screenshot fails
    private (int W, int H) _sourceSize = (1500, 800);
    private bool _recording = true;

    private sealed class ListLog(List<string> lines) : ILog { public void Info(string m) { lock (lines) lines.Add(m); } }

    private sealed class FakeRecovery : ICaptureRecovery
    {
        public HashSet<PixelSize> Sizes { get; } = [new(1500, 800)];
        public int Reconnects { get; private set; }
        public Exception? ReconnectFailure { get; set; }
        public Action? OnReconnect { get; set; }

        public Task<IReadOnlySet<PixelSize>> WindowSizesAsync(CancellationToken ct) => Task.FromResult<IReadOnlySet<PixelSize>>(Sizes);

        public Task ReconnectAsync(ObsClient obs, CancellationToken ct)
        {
            Reconnects++;
            if (ReconnectFailure is { } e) throw e;
            OnReconnect?.Invoke();
            return Task.CompletedTask;
        }

        public Task RepickAsync(ObsClient obs, string scene, string source, CancellationToken ct) => Task.CompletedTask;
    }

    public FreezeWatchTests()
    {
        _server = new FakeObsServer((type, _) => type switch
        {
            "GetRecordStatus" => (true, new { outputActive = _recording }),
            "GetSourceScreenshot" => _picture is { } p ? (true, new { imageData = p }) : (false, "Failed to render screenshot."),
            "GetSceneItemId" => (true, new { sceneItemId = 3 }),
            "GetSceneItemTransform" => (true, new { sceneItemTransform = new { sourceWidth = (double)_sourceSize.W, sourceHeight = (double)_sourceSize.H } }),
            _ => (true, null),
        });
    }

    public async ValueTask DisposeAsync() => await _server.DisposeAsync();

    private FreezeWatch Watch() => new(new SakshiConfig(), _recovery, _notifier, new ListLog(_log), _clock);

    private Task<ObsClient> Connect() => ObsClient.ConnectAsync("127.0.0.1", _server.Port, null, TestContext.Current.CancellationToken);

    /// <summary>Checks every 5 s for <paramref name="seconds"/>.</summary>
    private static async Task RunFor(FreezeWatch watch, ObsClient obs, FakeClock clock, int seconds)
    {
        for (var t = 0; t <= seconds; t += 5)
        {
            await watch.CheckAsync(obs, TestContext.Current.CancellationToken);
            clock.Now += TimeSpan.FromSeconds(5);
        }
    }

    // The window has grown to 3840x2160 but the stream is stuck at its last size.
    private void Freeze()
    {
        _recovery.Sizes.Clear();
        _recovery.Sizes.Add(new PixelSize(3840, 2160));
        _sourceSize = (3561, 2029);
    }

    [Fact]
    public async Task A_changing_picture_is_left_alone()
    {
        Freeze();
        await using var obs = await Connect();
        var watch = Watch();
        for (var i = 0; i < 10; i++)
        {
            _picture = $"frame-{i}";
            await watch.CheckAsync(obs, TestContext.Current.CancellationToken);
            _clock.Now += TimeSpan.FromSeconds(5);
        }
        Assert.Equal(0, _recovery.Reconnects);
        Assert.Empty(_notifier.Sent);
    }

    [Fact]
    public async Task Nothing_happens_when_obs_is_not_actually_recording()
    {
        // The state file still says recording, but OBS stopped on its own: reloading its sources would be pointless.
        Freeze();
        _recording = false;
        await using var obs = await Connect();
        await RunFor(Watch(), obs, _clock, 120);
        Assert.Equal(0, _recovery.Reconnects);
        Assert.Empty(_notifier.Sent);
    }

    [Fact]
    public async Task A_still_picture_of_a_window_its_own_size_is_left_alone()
    {
        // A slide left up: nothing moves, but the capture still matches its window.
        await using var obs = await Connect();
        await RunFor(Watch(), obs, _clock, 120);
        Assert.Equal(0, _recovery.Reconnects);
        Assert.Empty(_notifier.Sent);
    }

    [Fact]
    public async Task A_scaled_window_size_counts_as_a_match()
    {
        _recovery.Sizes.Add(new PixelSize(2250, 1200));
        _sourceSize = (2250, 1200);
        await using var obs = await Connect();
        await RunFor(Watch(), obs, _clock, 60);
        Assert.Equal(0, _recovery.Reconnects);
    }

    [Fact]
    public async Task A_frozen_capture_is_reconnected_once_after_ten_seconds_and_the_user_told()
    {
        Freeze();
        await using var obs = await Connect();
        var watch = Watch();

        await RunFor(watch, obs, _clock, 5);    // checks at 0 and 5 s: still under 10 s
        Assert.Equal(0, _recovery.Reconnects);

        await watch.CheckAsync(obs, TestContext.Current.CancellationToken);   // 10 s
        Assert.Equal(1, _recovery.Reconnects);
        Assert.Equal([(Urgency.Normal, FreezeWatch.ReconnectedHeadline, FreezeWatch.ReconnectedBody)], _notifier.Sent);
        Assert.Contains(_log, l => l == "capture: no new picture for 10 s and no window is 3561x2029; reconnecting");
    }

    [Fact]
    public async Task A_capture_still_frozen_after_the_reconnect_gets_one_warning_and_no_second_reconnect()
    {
        Freeze();
        await using var obs = await Connect();
        await RunFor(Watch(), obs, _clock, 300);

        Assert.Equal(1, _recovery.Reconnects);
        Assert.Equal(2, _notifier.Sent.Count);
        Assert.Equal((Urgency.Critical, FreezeWatch.StuckHeadline, FreezeWatch.StuckBody), _notifier.Sent[1]);
    }

    [Fact]
    public async Task The_first_picture_after_a_reconnect_is_not_taken_as_a_sign_of_life()
    {
        // The reloaded source shows one new (dead) picture and then nothing: that must not earn a second reconnect.
        Freeze();
        _recovery.OnReconnect = () => _picture = "black";
        await using var obs = await Connect();
        await RunFor(Watch(), obs, _clock, 300);

        Assert.Equal(1, _recovery.Reconnects);
        Assert.Equal(FreezeWatch.StuckHeadline, _notifier.Sent[^1].Headline);
    }

    [Fact]
    public async Task A_capture_that_came_back_and_froze_again_is_reconnected_again()
    {
        Freeze();
        await using var obs = await Connect();
        var watch = Watch();
        await RunFor(watch, obs, _clock, 10);                 // frozen: reconnect 1
        Assert.Equal(1, _recovery.Reconnects);

        for (var i = 0; i < 3; i++)                           // alive again
        {
            _picture = $"live-{i}";
            await watch.CheckAsync(obs, TestContext.Current.CancellationToken);
            _clock.Now += TimeSpan.FromSeconds(5);
        }
        await RunFor(watch, obs, _clock, 15);                 // frozen again
        Assert.Equal(2, _recovery.Reconnects);
        Assert.All(_notifier.Sent, n => Assert.Equal(FreezeWatch.ReconnectedHeadline, n.Headline));
    }

    [Fact]
    public async Task A_failed_reconnect_warns_at_once_and_is_not_retried()
    {
        Freeze();
        _recovery.ReconnectFailure = new InvalidOperationException("could not switch to the scratch scene collection");
        await using var obs = await Connect();
        await RunFor(Watch(), obs, _clock, 120);

        Assert.Equal(1, _recovery.Reconnects);
        Assert.Equal([(Urgency.Critical, FreezeWatch.StuckHeadline, FreezeWatch.StuckBody)], _notifier.Sent);
        Assert.Contains("capture: reconnect failed: could not switch to the scratch scene collection", _log);
    }

    [Fact]
    public async Task A_source_with_no_size_is_not_reconnected_but_the_user_is_told_once()
    {
        // The window was closed (or a pick dismissed): a reconnect would pop the picker mid-meeting.
        _picture = null;
        _sourceSize = (0, 0);
        await using var obs = await Connect();
        await RunFor(Watch(), obs, _clock, 120);
        Assert.Equal(0, _recovery.Reconnects);
        Assert.Equal([(Urgency.Critical, FreezeWatch.StuckHeadline, FreezeWatch.StuckBody)], _notifier.Sent);
    }

    [Fact]
    public async Task A_recording_gets_at_most_three_reconnects()
    {
        // A size Sakshi can never match (a scale it gets wrong) must not cost a gap after every slide.
        Freeze();
        await using var obs = await Connect();
        var watch = Watch();
        for (var round = 0; round < 6; round++)
        {
            await RunFor(watch, obs, _clock, 15);              // still: reconnect (while allowed)
            for (var i = 0; i < 2; i++)                        // the next slide
            {
                _picture = $"slide-{round}-{i}";
                await watch.CheckAsync(obs, TestContext.Current.CancellationToken);
                _clock.Now += TimeSpan.FromSeconds(5);
            }
        }
        Assert.Equal(FreezeWatch.MaxReconnects, _recovery.Reconnects);
        Assert.Contains(_notifier.Sent, n => n.Headline == FreezeWatch.StuckHeadline);
    }

    [Fact]
    public async Task Reset_starts_over_from_a_fresh_baseline()
    {
        Freeze();
        await using var obs = await Connect();
        var watch = Watch();
        await RunFor(watch, obs, _clock, 300);                // reconnected and warned
        watch.Reset();
        await RunFor(watch, obs, _clock, 10);                 // a new recording: allowed to reconnect again
        Assert.Equal(2, _recovery.Reconnects);
    }

    [Fact]
    public async Task The_screenshot_asks_for_the_configured_source()
    {
        JsonElement? request = null;
        await using var server = new FakeObsServer((type, data) =>
        {
            if (type == "GetSourceScreenshot") request = data.Clone();
            return (true, new { imageData = "x" });
        });
        await using var obs = await ObsClient.ConnectAsync("127.0.0.1", server.Port, null, TestContext.Current.CancellationToken);
        await new FreezeWatch(new SakshiConfig { ObsSource = "My Capture" }, _recovery, _notifier, new ListLog(_log), _clock)
            .CheckAsync(obs, TestContext.Current.CancellationToken);

        Assert.Equal("My Capture", request!.Value.GetProperty("sourceName").GetString());
        Assert.Equal("png", request.Value.GetProperty("imageFormat").GetString());
    }
}
