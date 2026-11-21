using Sakshi.Core.Ports;

namespace Sakshi.TestSupport;

/// <summary>
/// Answers prompts from a queue, in the order they are asked (choice and text prompts share it).
/// null = dismissed; an empty queue also counts as dismissed. Never opens a window.
/// </summary>
public sealed class FakePrompter : IPrompter
{
    private readonly Queue<string?> _answers = new();

    /// <summary>Every title (choice) or label (text) shown, in order.</summary>
    public List<string> Shown { get; } = [];

    /// <summary>The rows of every choice prompt, in order.</summary>
    public List<IReadOnlyList<PromptRow>> Rows { get; } = [];

    /// <summary>The titles joined like the bash tests print them: "a|b|".</summary>
    public string Sequence => string.Concat(Shown.Select(s => s + "|"));

    /// <summary>Called with each title/label as it is shown, before the answer is returned.</summary>
    public Action<string>? OnShown { get; set; }

    /// <summary>The timeout each prompt was shown with, in order.</summary>
    public List<TimeSpan> Timeouts { get; } = [];

    /// <summary>When set, every prompt throws it (a broken menu).</summary>
    public Exception? Failure { get; set; }

    /// <summary>When set, every prompt waits for it before answering.</summary>
    public Task? Gate { get; set; }

    public FakePrompter Answer(params string?[] answers)
    {
        foreach (var a in answers) _answers.Enqueue(a);
        return this;
    }

    public async Task<string?> ChooseAsync(string title, IReadOnlyList<PromptRow> rows, TimeSpan timeout, CancellationToken ct)
    {
        Rows.Add(rows);
        await Show(title, timeout);
        return Next();
    }

    public async Task<string?> AskTextAsync(string label, TimeSpan timeout, CancellationToken ct)
    {
        await Show(label, timeout);
        return Next();
    }

    private async Task Show(string text, TimeSpan timeout)
    {
        Shown.Add(text);
        Timeouts.Add(timeout);
        OnShown?.Invoke(text);
        if (Failure is not null) throw Failure;
        if (Gate is not null) await Gate;
    }

    private string? Next() => _answers.Count > 0 ? _answers.Dequeue() : null;
}
