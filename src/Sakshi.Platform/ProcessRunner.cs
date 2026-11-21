using System.Diagnostics;

namespace Sakshi.Platform;

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr);

/// <summary>Every process launch in the tray app goes through this, so tests can fake it.</summary>
public interface IProcessRunner
{
    /// <summary>
    /// Runs <paramref name="file"/> with <paramref name="args"/> (no shell) and captures its output.
    /// Kills the process tree and throws <see cref="TimeoutException"/> on timeout, or
    /// <see cref="OperationCanceledException"/> when <paramref name="ct"/> is cancelled.
    /// A process that cannot be started throws <see cref="System.ComponentModel.Win32Exception"/>.
    /// </summary>
    Task<ProcessResult> RunAsync(string file, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct);

    /// <summary>
    /// Runs <paramref name="file"/> with the caller's stdin, stdout and stderr inherited (nothing captured),
    /// with <paramref name="env"/> applied on top of the inherited environment (a null value removes the
    /// variable), and returns its exit code. Kills the process tree when <paramref name="ct"/> is cancelled.
    /// A process that cannot be started throws <see cref="System.ComponentModel.Win32Exception"/>.
    /// </summary>
    Task<int> RunAttachedAsync(string file, IReadOnlyList<string> args, IReadOnlyDictionary<string, string?> env, CancellationToken ct);

    /// <summary>
    /// Starts a process without waiting for it or capturing anything; returns its pid. It is detached
    /// from the caller's stdio and session so a captured parent never waits for it.
    /// Throws <see cref="System.ComponentModel.Win32Exception"/> when the target is missing or not
    /// executable by the current user: a path that does not exist or lacks execute permission, or a name
    /// not found on PATH. (On Windows Process.Start's own resolution and exception apply.)
    /// The process starts in <paramref name="workingDirectory"/> when given, else in the caller's.
    /// </summary>
    int StartDetached(string file, IReadOnlyList<string> args, string? workingDirectory = null);

    /// <summary>True when a process with exactly this name is running (like <c>pgrep -x</c>).</summary>
    bool IsRunning(string processName);

    /// <summary>The full path of <paramref name="command"/> found on PATH, or null.</summary>
    string? Which(string command);
}

public sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(string file, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct)
    {
        var psi = NewStartInfo(file, args);
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.RedirectStandardInput = true;

        using var proc = new Process { StartInfo = psi };
        proc.Start();
        proc.StandardInput.Close();

        // Drain both pipes concurrently so a chatty child never blocks on a full pipe.
        var stdout = proc.StandardOutput.ReadToEndAsync();
        var stderr = proc.StandardError.ReadToEndAsync();

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        string outText, errText;
        try
        {
            await proc.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            // A grandchild may keep the pipes open after the child exits: bound the drain too.
            await Task.WhenAll(stdout, stderr).WaitAsync(linked.Token).ConfigureAwait(false);
            outText = stdout.Result;
            errText = stderr.Result;
        }
        catch (OperationCanceledException)
        {
            Kill(proc);
            if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
            throw new TimeoutException($"{Path.GetFileName(file)} did not finish within {timeout.TotalSeconds:0.#} s");
        }
        return new ProcessResult(proc.ExitCode, outText, errText);
    }

    public async Task<int> RunAttachedAsync(string file, IReadOnlyList<string> args, IReadOnlyDictionary<string, string?> env, CancellationToken ct)
    {
        var psi = NewStartInfo(file, args);
        foreach (var (k, v) in env)
        {
            if (v is null) psi.Environment.Remove(k);
            else psi.Environment[k] = v;
        }
        using var proc = new Process { StartInfo = psi };
        proc.Start();
        try
        {
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Kill(proc);
            throw;
        }
        return proc.ExitCode;
    }

    public int StartDetached(string file, IReadOnlyList<string> args, string? workingDirectory = null)
    {
        // Resolve first: through sh a missing target would only be a silent exit 127 and a dead pid.
        // Windows keeps Process.Start's own resolution (extensionless paths, app dir, CWD) and its Win32Exception.
        var windows = OperatingSystem.IsWindows();
        var resolved = windows ? file : Which(file)
            ?? throw new System.ComponentModel.Win32Exception(2, $"cannot start {file}: not found or not executable by this user");
        var (f, a) = BuildDetachedCommand(resolved, args, windows, !windows && Which("setsid") is not null, workingDirectory);
        var psi = NewStartInfo(f, a);
        if (windows && workingDirectory is not null) psi.WorkingDirectory = workingDirectory;
        if (windows)
        {
            psi.CreateNoWindow = true;
            // Without a redirect the child inherits the caller's stdin; redirect it and close it so the child sees EOF.
            psi.RedirectStandardInput = true;
        }
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException($"could not start {file}");
        var pid = proc.Id;
        if (windows)
        {
            try { proc.StandardInput.Close(); }
            catch (Exception e) when (e is IOException or InvalidOperationException or ObjectDisposedException) { }
        }
        return pid;
    }

    /// <summary>
    /// The command that starts <paramref name="file"/> detached. Unix: a shell that execs it (so the
    /// pid is kept) with stdio on /dev/null, in its own session when setsid exists. Windows: direct
    /// (the caller sets the working directory on the start info).
    /// </summary>
    internal static (string File, IReadOnlyList<string> Args) BuildDetachedCommand(
        string file, IReadOnlyList<string> args, bool isWindows, bool hasSetsid, string? workingDirectory = null)
    {
        if (isWindows) return (file, args);
        var exec = (hasSetsid ? "exec setsid \"$0\" \"$@\"" : "exec \"$0\" \"$@\"") + " </dev/null >/dev/null 2>&1";
        // The directory is a positional argument ($1), never part of the script text, so no quoting can break out.
        if (workingDirectory is null) return ("/bin/sh", ["-c", exec, file, .. args]);
        return ("/bin/sh", ["-c", "cd -- \"$1\" && shift && " + exec, file, workingDirectory, .. args]);
    }

    /// <summary>
    /// Compares <see cref="Process.ProcessName"/> exactly. On Linux that is the kernel's comm,
    /// truncated to 15 characters, so a longer name never matches (pgrep -x has the same limit).
    /// </summary>
    public bool IsRunning(string processName)
    {
        var all = Process.GetProcesses();
        try
        {
            foreach (var p in all)
            {
                try { if (string.Equals(p.ProcessName, processName, StringComparison.Ordinal)) return true; }
                catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
            return false;
        }
        finally { foreach (var p in all) p.Dispose(); }
    }

    public string? Which(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        if (command.Contains(Path.DirectorySeparatorChar) || command.Contains(Path.AltDirectorySeparatorChar))
            return IsExecutable(command) ? Path.GetFullPath(command) : null;

        var names = new List<string> { command };
        if (OperatingSystem.IsWindows() && string.IsNullOrEmpty(Path.GetExtension(command)))
        {
            var exts = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
                .Split(';', StringSplitOptions.RemoveEmptyEntries);
            names.AddRange(exts.Select(e => command + e));
        }
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            foreach (var n in names)
            {
                var candidate = Path.Combine(dir.Trim('"'), n);
                if (IsExecutable(candidate)) return candidate;
            }
        return null;
    }

    private static bool IsExecutable(string path)
    {
        if (!File.Exists(path)) return false;
        if (OperatingSystem.IsWindows()) return true;
        try { return access(path, 1) == 0; }  // X_OK: honours the owner/group/other bits for the current user
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return (File.GetUnixFileMode(path) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
        }
    }

    [System.Runtime.InteropServices.DllImport("libc", SetLastError = true)]
    private static extern int access([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPUTF8Str)] string path, int mode);

    private static ProcessStartInfo NewStartInfo(string file, IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo(file) { UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        return psi;
    }

    private static void Kill(Process proc)
    {
        try { proc.Kill(entireProcessTree: true); }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        try { proc.WaitForExit(TimeSpan.FromSeconds(5)); } catch (InvalidOperationException) { }
    }
}
