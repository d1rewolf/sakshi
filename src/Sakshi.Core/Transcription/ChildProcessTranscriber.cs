using System.Diagnostics;
using Sakshi.Core.State;

namespace Sakshi.Core.Transcription;

/// <summary>
/// Runs <c>fileName prefixArgs… --log Dir/transcribe.log FrameArgs-words… -o Dir Video</c>
/// as a child process (production: the sakshi executable with "transcribe"). The child
/// writes its own log (<c>--log</c>), so nothing here depends on the tray staying alive.
/// The child's stdout/stderr are piped and drained into the void: a pipe nobody reads would
/// block it once full, and an inherited terminal could vanish under it. If the tray exits,
/// the pipe breaks; .NET and native writers then get an ignorable write error, not a signal.
/// Cancelling stops waiting but never kills the child: the job stays persisted with its
/// pid and the next start adopts it. stdin is closed.
/// </summary>
public sealed class ChildProcessTranscriber(string fileName, IReadOnlyList<string> prefixArgs) : ITranscriber
{
    public async Task<int> RunAsync(TranscriptionJob job, Action<int> onStarted, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in prefixArgs) psi.ArgumentList.Add(a);
        psi.ArgumentList.Add("--log");
        psi.ArgumentList.Add(Path.Combine(job.Dir, "transcribe.log"));
        foreach (var w in job.FrameArgs.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)) psi.ArgumentList.Add(w);
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add(job.Dir);
        psi.ArgumentList.Add(job.Video);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("could not start " + fileName);
        onStarted(process.Id);
        process.StandardInput.Close();
        _ = Drain(process.StandardOutput.BaseStream);
        _ = Drain(process.StandardError.BaseStream);

        await process.WaitForExitAsync(ct); // cancellation propagates; the child keeps running
        return process.ExitCode;
    }

    private static async Task Drain(Stream source)
    {
        try { await source.CopyToAsync(Stream.Null); }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException) { }
    }
}
