using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Sakshi.Core.Ports;

namespace Sakshi.Core.HandOff;

/// <summary>
/// Runs the hand-off command after a transcript: the command's words (split on
/// whitespace, no quoting), then the meeting folder. Output goes to
/// handoff.log; a failure only adds a critical notification.
/// </summary>
public sealed class HandOffRunner(CommandResolver resolver, INotifier notifier)
{
    private const int NotFound = 127;
    private static readonly TimeSpan PumpGrace = TimeSpan.FromSeconds(2);

    /// <returns>true when the hand-off succeeded (or there was none to run).</returns>
    public async Task<bool> RunAsync(string handOff, string meetingDir, string meetingName, CancellationToken ct)
    {
        var words = handOff.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return true;

        var logPath = Path.Combine(meetingDir, "handoff.log");
        await using var log = new StreamWriter(logPath, append: false, new UTF8Encoding(false)) { NewLine = "\n", AutoFlush = true };
        var gate = new object();
        var closed = false;
        void Write(string line) { lock (gate) { if (!closed) log.WriteLine(line); } }

        Write($"$ {handOff} {meetingDir}");

        var program = resolver.Resolve(words[0]) ?? words[0];
        int status;
        try
        {
            status = await RunProcessAsync(program, words.Skip(1).Append(meetingDir), Write, ct);
        }
        catch
        {
            lock (gate) closed = true; // late pump output must not hit a disposed log
            throw;
        }
        if (status == 0)
        {
            lock (gate) closed = true;
            return true;
        }

        Write($"exit status {status}");
        lock (gate) closed = true;
        notifier.Notify(
            $"Hand-off failed: {meetingName}",
            $"\"{handOff}\" exited with status {status}. The meeting is untouched in {meetingDir}; see handoff.log",
            Urgency.Critical);
        return false;
    }

    private static async Task<int> RunProcessAsync(string program, IEnumerable<string> args, Action<string> write, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        // Process cannot start .cmd/.bat directly; cmd.exe /c can.
        if (OperatingSystem.IsWindows()
            && (program.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
                || program.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)))
        {
            var line = CmdExeCommandLine.Build(program, args, out var refusal);
            if (line is null)
            {
                write(refusal!);
                return NotFound;
            }
            psi.FileName = "cmd.exe";
            psi.Arguments = line;
        }
        else
        {
            psi.FileName = program;
            foreach (var a in args) psi.ArgumentList.Add(a);
        }

        Process process;
        try
        {
            process = Process.Start(psi) ?? throw new Win32Exception("could not start " + program);
        }
        catch (Exception e) when (e is Win32Exception or FileNotFoundException or DirectoryNotFoundException)
        {
            write(e.Message);
            return NotFound;
        }

        using (process)
        using (var stop = new CancellationTokenSource())
        {
            process.StandardInput.Close();
            Task[] pumps =
            [
                Pump(process.StandardOutput, write, stop.Token),
                Pump(process.StandardError, write, stop.Token),
            ];
            try
            {
                await process.WaitForExitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (Exception e) when (e is InvalidOperationException or Win32Exception or AggregateException) { }
                await StopPumps(pumps, stop);
                throw;
            }
            // A background child may keep the pipes open: don't wait for EOF forever.
            var drained = Task.WhenAll(pumps);
            if (await Task.WhenAny(drained, Task.Delay(PumpGrace, CancellationToken.None)) != drained)
            {
                await StopPumps(pumps, stop);
                write("(output after exit not captured)");
            }
            return process.ExitCode;
        }
    }

    private static async Task StopPumps(Task[] pumps, CancellationTokenSource stop)
    {
        await stop.CancelAsync();
        // Cancelling a pipe read is best effort: never wait on it for long.
        await Task.WhenAny(Task.WhenAll(pumps), Task.Delay(TimeSpan.FromSeconds(1), CancellationToken.None));
    }

    private static async Task Pump(StreamReader reader, Action<string> write, CancellationToken ct)
    {
        try
        {
            while (await reader.ReadLineAsync(ct) is { } line) write(line);
        }
        catch (OperationCanceledException) { }
    }
}
