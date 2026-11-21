namespace Sakshi.Platform.MacOS;

public sealed class MacObsInstall(string home, Func<string, bool> dirExists) : IObsInstall
{
    public string ProcessName => "OBS";

    public Task<ObsLaunch> LaunchAsync(IReadOnlyList<string> extraArgs, CancellationToken ct)
    {
        var bundle = new[] { "/Applications/OBS.app", Path.Combine(home, "Applications", "OBS.app") }.FirstOrDefault(dirExists)
            ?? throw new InvalidOperationException("OBS is not installed (no OBS.app in /Applications or ~/Applications). Get it from https://obsproject.com");
        // -g keeps OBS in the background so it does not steal focus from the meeting.
        return Task.FromResult(new ObsLaunch("open", ["-g", "-a", bundle, "--args", .. extraArgs]));
    }
}
