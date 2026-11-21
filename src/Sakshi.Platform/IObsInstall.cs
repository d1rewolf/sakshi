namespace Sakshi.Platform;

/// <summary>How to start OBS: the program, its arguments and, when it needs one, its working directory.</summary>
public sealed record ObsLaunch(string File, IReadOnlyList<string> Args, string? WorkingDirectory = null);

/// <summary>Where OBS lives on this OS and how it is started there.</summary>
public interface IObsInstall
{
    /// <summary>The name OBS runs under, as <see cref="IProcessRunner.IsRunning"/> sees it.</summary>
    string ProcessName { get; }

    /// <summary>Throws <see cref="InvalidOperationException"/>, with a message fit to show, when OBS is not installed.</summary>
    Task<ObsLaunch> LaunchAsync(IReadOnlyList<string> extraArgs, CancellationToken ct);
}
