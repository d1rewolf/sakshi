using Sakshi.Core.Obs;

namespace Sakshi.TestSupport;

/// <summary>A scripted OBS. Never talks to a real one.</summary>
public sealed class FakeObs : IObs
{
    public bool Recording { get; set; } = true;
    public string? OutputPath { get; set; }
    public string? RecordDirectory { get; set; }
    public bool StopFails { get; set; }
    public bool StatusFails { get; set; }

    /// <summary>Runs right after StopRecord succeeds (a cancellation, say).</summary>
    public Action? AfterStop { get; set; }
    public List<string> Calls { get; } = [];

    /// <summary>How many times the Recorder released this connection.</summary>
    public int Disposed { get; private set; }

    public ValueTask DisposeAsync()
    {
        Disposed++;
        return ValueTask.CompletedTask;
    }

    public Task<bool> IsRecordingAsync(CancellationToken ct)
    {
        Calls.Add("IsRecording");
        if (StatusFails) throw new InvalidOperationException("status broke");
        return Task.FromResult(Recording);
    }

    public Task<string?> StopRecordAsync(CancellationToken ct)
    {
        Calls.Add("StopRecord");
        if (StopFails) throw new InvalidOperationException("OBS said no");
        AfterStop?.Invoke();
        // A real client whose token is cancelled while the reply is in flight throws, even
        // though OBS has already stopped.
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(OutputPath);
    }

    public Task<string?> GetRecordDirectoryAsync(CancellationToken ct)
    {
        Calls.Add("GetRecordDirectory");
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(RecordDirectory);
    }
}
