using Avalonia.Input;
using Sakshi.Core.Ports;

namespace Sakshi.App.Prompts;

/// <summary>Selection and result logic of the chooser, separate from the view.</summary>
internal sealed class ChooserModel(IReadOnlyList<PromptRow> rows)
{
    public int Index { get; private set; }
    public bool Done { get; private set; }

    /// <summary>The chosen row's label; null while undecided or when dismissed.</summary>
    public string? Result { get; private set; }

    public event Action? SelectionChanged;

    /// <summary>Handles a key; true when the key was consumed.</summary>
    public bool Handle(Key key)
    {
        switch (key)
        {
            case Key.Up: Move(-1); return true;
            case Key.Down: Move(1); return true;
            case Key.Enter: Pick(Index); return true;
            case Key.Escape: Done = true; Result = null; return true;
            default: return false;
        }
    }

    public void Pick(int index)
    {
        if (Done || index < 0 || index >= rows.Count) return;
        Index = index;
        Result = rows[index].Label;
        Done = true;
    }

    private void Move(int delta)
    {
        var next = Math.Clamp(Index + delta, 0, Math.Max(0, rows.Count - 1));
        if (next == Index) return;
        Index = next;
        SelectionChanged?.Invoke();
    }
}
