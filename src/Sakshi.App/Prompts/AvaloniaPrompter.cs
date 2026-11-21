using Avalonia.Controls;
using Avalonia.Threading;
using Sakshi.Core.Ports;

namespace Sakshi.App.Prompts;

/// <summary>
/// IPrompter over Avalonia windows. Callable from any thread; the windows live on the UI thread and the
/// caller is never blocked. One window at a time (later prompts queue). A timeout closes the window and
/// returns null; the caller's token closes it and throws OperationCanceledException.
/// </summary>
public sealed class AvaloniaPrompter : IPrompter
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>The window currently open, if any (for tests).</summary>
    internal Window? OpenWindow { get; private set; }

    /// <summary>Raised on the UI thread as each window is about to show (for tests).</summary>
    internal event Action<Window>? Opened;

    public Task<string?> ChooseAsync(string title, IReadOnlyList<PromptRow> rows, TimeSpan timeout, CancellationToken ct)
    {
        if (rows.Count == 0) return Task.FromResult<string?>(null);
        return Run(() =>
        {
            var w = new ChooserWindow(title, rows);
            return (w, () => w.Result);
        }, timeout, ct);
    }

    public Task<string?> AskTextAsync(string label, TimeSpan timeout, CancellationToken ct) =>
        Run(() =>
        {
            var w = new TextPromptWindow(label);
            return (w, () => w.Result);
        }, timeout, ct);

    private async Task<string?> Run(Func<(Window Window, Func<string?> Result)> create, TimeSpan timeout, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ct.ThrowIfCancellationRequested();
            return await Dispatcher.UIThread.InvokeAsync(() => Show(create, timeout, ct));
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<string?> Show(Func<(Window Window, Func<string?> Result)> create, TimeSpan timeout, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var (window, result) = create();
        var done = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Closed += (_, _) => done.TrySetResult(result());

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var onTimeout = timeoutCts.Token.Register(() => Dispatcher.UIThread.Post(window.Close));
        using var onCancel = ct.Register(() =>
        {
            done.TrySetCanceled(ct);
            Dispatcher.UIThread.Post(window.Close);
        });

        OpenWindow = window;
        Opened?.Invoke(window);
        try
        {
            window.Show();
            window.Activate();
            return await done.Task;
        }
        finally
        {
            OpenWindow = null;
            if (window.IsVisible) window.Close();
        }
    }
}
