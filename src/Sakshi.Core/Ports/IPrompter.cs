namespace Sakshi.Core.Ports;

public sealed record PromptRow(string Glyph, string Label, string Description);

public interface IPrompter
{
    /// <summary>Returns the chosen row's Label; null when dismissed or timed out.</summary>
    Task<string?> ChooseAsync(string title, IReadOnlyList<PromptRow> rows, TimeSpan timeout, CancellationToken ct);

    /// <summary>Returns the typed text; null when dismissed or timed out.</summary>
    Task<string?> AskTextAsync(string label, TimeSpan timeout, CancellationToken ct);
}
