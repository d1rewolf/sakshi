using Sakshi.Core.Config;
using Sakshi.Core.Paths;
using Sakshi.Core.Ports;
using Sakshi.Platform.Linux;
using Sakshi.TestSupport;

namespace Sakshi.Platform.Tests;

public sealed class HyprlandCaptureRecoveryTests
{
    private sealed class NullLog : ILog { public void Info(string m) { } }

    private static HyprlandCaptureRecovery Recovery(FakeProcessRunner runner)
    {
        var paths = new SakshiPaths("/nonexistent/c", "/nonexistent/s", "/nonexistent/d", "/nonexistent/obs");
        var capture = new PipeWireWindowCapture(new SakshiConfig(), paths, runner, new FakeNotifier(), new NullLog(),
            (_, _) => Task.CompletedTask, () => false, _ => "sig");
        return new HyprlandCaptureRecovery(capture, runner);
    }

    [Fact]
    public async Task Window_sizes_include_each_window_and_its_size_at_its_monitors_scale()
    {
        var runner = new FakeProcessRunner
        {
            OnRun = (file, args) => (file, args[1]) switch
            {
                ("hyprctl", "monitors") => new ProcessResult(0, """[{"id":1,"scale":1.0},{"id":2,"scale":1.5}]""", ""),
                ("hyprctl", "clients") => new ProcessResult(0,
                    """[{"size":[1500,800],"monitor":1},{"size":[1001,667],"monitor":2},{"size":"odd","monitor":1},{"monitor":1}]""", ""),
                _ => throw new InvalidOperationException($"unexpected {file}"),
            },
        };
        var sizes = await Recovery(runner).WindowSizesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            new HashSet<PixelSize> { new(1500, 800), new(1001, 667), new(1501, 1000), new(1502, 1001) },
            sizes.ToHashSet());
    }

    [Fact]
    public async Task A_failing_hyprctl_throws_rather_than_reporting_no_windows()
    {
        // No windows would make every capture look frozen.
        var runner = new FakeProcessRunner { OnRun = (_, _) => new ProcessResult(1, "", "no socket") };
        await Assert.ThrowsAsync<InvalidOperationException>(() => Recovery(runner).WindowSizesAsync(TestContext.Current.CancellationToken));
    }
}
