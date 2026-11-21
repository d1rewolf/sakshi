using Sakshi.Core.Paths;
using Sakshi.App.Tray;
using Sakshi.Core.Ports;
using Sakshi.Platform;
using Sakshi.Platform.Doctor;

namespace Sakshi.App.Tests;

public sealed class FirstStartCheckTests : IAsyncDisposable
{
    private readonly TrayFixture _f = new();
    private readonly ShortDir _short = new();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask DisposeAsync()
    {
        await _f.DisposeAsync();
        _short.Dispose();
    }

    private static DoctorProbes Probes(bool installed) => new(
        Which: n => installed ? "/usr/bin/" + n : null,
        FileExists: _ => false,
        ReadFile: _ => null,
        FilesIn: _ => [],
        Run: (_, _) => new ProcessResult(0, "OBS Studio - 32.0.2 (linux)", ""),
        Env: _ => null,
        ObsLaunch: () => installed ? new ObsLaunch("obs", []) : throw new InvalidOperationException("OBS is not installed"),
        ObsRunning: () => false,
        FileVersion: _ => null);

    private async Task<(TrayHost Host, TrayInstance Instance)> Started(DoctorProbes? probes, bool installed = false)
    {
        var controller = _f.Controller();
        var s = controller.Services;
        var services = new TrayServices(s.Paths, s.Os, s.Home, s.Config, s.Store, s.Recorder, s.Starter, s.Queue,
            s.Prompter, s.Notifier, s.Log, s.Clock, s.ProcessRunner)
        { DoctorProbes = probes, Delay = s.Delay, ObsRunning = s.ObsRunning, IsInstalledCopy = () => installed };
        var instance = TrayInstance.TryAcquire(Path.GetDirectoryName(_f.StateFile)!, _ => null, _short.Root)!;
        var host = new TrayHost(services, controller, instance);
        host.Start();
        await host.SetupAtStart.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        return (host, instance);
    }

    private async Task<bool> Wanted(DoctorProbes? probes, bool installed = false)
    {
        var (host, instance) = await Started(probes, installed);
        await using var _ = instance;
        var wanted = await host.SetupAtStart is not null;
        await host.StopAsync(TimeSpan.FromSeconds(5));
        return wanted;
    }

    [Fact]
    public async Task Setup_opens_when_a_check_fails_and_says_so_in_the_log()
    {
        Assert.True(await Wanted(Probes(installed: false)));
        Assert.Contains(_f.Log.Lines, l => l.StartsWith("doctor: missing OBS installed, OBS version, ffmpeg"));
        Assert.DoesNotContain(_f.Notifier.Sent, n => n.Headline == "Sakshi needs setting up");
    }

    [Fact]
    public async Task Setup_stays_shut_when_everything_passes_on_a_source_build() =>
        Assert.False(await Wanted(Probes(installed: true)));

    [Fact]
    public async Task Setup_opens_once_on_an_installed_copys_first_start()
    {
        Assert.True(await Wanted(Probes(installed: true), installed: true));
        var (host, instance) = await Started(Probes(installed: true), installed: true);
        await using (instance)
        {
            await host.MarkSetupSeen();
            await host.StopAsync(TimeSpan.FromSeconds(5));
        }
        Assert.False(await Wanted(Probes(installed: true), installed: true));
    }

    [Fact]
    public async Task Without_probes_the_check_does_not_run()
    {
        Assert.False(await Wanted(null, installed: true));
        Assert.Empty(_f.Notifier.Sent);
    }
}
