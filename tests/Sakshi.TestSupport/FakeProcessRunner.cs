using Sakshi.Platform;

namespace Sakshi.TestSupport;

/// <summary>Records every call; results come from <see cref="OnRun"/> (default: exit 0, no output).</summary>
public sealed class FakeProcessRunner : IProcessRunner
{
    public List<(string File, IReadOnlyList<string> Args)> Calls { get; } = [];
    public Func<string, IReadOnlyList<string>, ProcessResult>? OnRun { get; set; }
    public List<(string File, IReadOnlyList<string> Args, IReadOnlyDictionary<string, string?> Env)> Attached { get; } = [];
    public Func<string, IReadOnlyList<string>, IReadOnlyDictionary<string, string?>, int>? OnAttached { get; set; }
    public HashSet<string> Running { get; } = [];
    public Dictionary<string, string> OnPath { get; } = [];
    public List<(string, IReadOnlyList<string>, string?)> Detached { get; } = [];
    private int _nextPid = 1000;
    private readonly object _gate = new();

    /// <summary>Tests that touch Calls, Detached, Running or OnPath while code under test runs should lock this.</summary>
    public object Gate => _gate;

    private T Locked<T>(Func<T> f) { lock (_gate) return f(); }

    public Task<ProcessResult> RunAsync(string file, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct)
    {
        lock (_gate) Calls.Add((file, args));
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(OnRun?.Invoke(file, args) ?? new ProcessResult(0, "", ""));
    }

    public Task<int> RunAttachedAsync(string file, IReadOnlyList<string> args, IReadOnlyDictionary<string, string?> env, CancellationToken ct)
    {
        lock (_gate) Attached.Add((file, args, env));
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(OnAttached?.Invoke(file, args, env) ?? 0);
    }

    public int StartDetached(string file, IReadOnlyList<string> args, string? workingDirectory = null)
    {
        lock (_gate) Detached.Add((file, args, workingDirectory));
        return Interlocked.Increment(ref _nextPid);
    }

    public bool IsRunning(string processName) => Locked(() => Running.Contains(processName));

    public string? Which(string command) => Locked(() => OnPath.GetValueOrDefault(command));
}
