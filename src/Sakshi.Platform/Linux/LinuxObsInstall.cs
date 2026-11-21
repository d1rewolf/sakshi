namespace Sakshi.Platform.Linux;

public sealed class LinuxObsInstall(IProcessRunner runner) : IObsInstall
{
    private const string FlatpakId = "com.obsproject.Studio";

    public string ProcessName => "obs";

    public async Task<ObsLaunch> LaunchAsync(IReadOnlyList<string> extraArgs, CancellationToken ct)
    {
        if (runner.Which("obs") is not null)
            return runner.Which("uwsm-app") is not null
                ? new ObsLaunch("uwsm-app", ["--", "obs", .. extraArgs])
                : new ObsLaunch("obs", extraArgs);
        if (runner.Which("flatpak") is not null)
        {
            var r = await runner.RunAsync("flatpak", ["info", FlatpakId], TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            if (r.ExitCode == 0) return new ObsLaunch("flatpak", ["run", FlatpakId, .. extraArgs]);
        }
        throw new InvalidOperationException("OBS is not installed (no obs on PATH and no Flatpak com.obsproject.Studio)");
    }
}
