using Sakshi.Core.Ports;

namespace Sakshi.Platform.Linux;

public sealed class GioTrash(IProcessRunner runner) : ITrash
{
    public void MoveToTrash(string path)
    {
        ProcessResult r;
        try
        {
            r = runner.RunAsync("gio", ["trash", "--", path], TimeSpan.FromSeconds(30), CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new IOException($"could not run gio trash: {e.Message}", e);
        }
        if (r.ExitCode != 0)
            throw new IOException(r.StdErr.Trim().Length > 0 ? r.StdErr.Trim() : $"gio trash exited with {r.ExitCode}");
    }
}
