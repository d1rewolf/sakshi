using Avalonia;

namespace Sakshi.App.Prompts;

/// <summary>The Avalonia application behind the prompt windows: Fluent, dark, in Sakshi's colours; no main window.</summary>
public class PromptApplication : Application
{
    public override void Initialize() => Witness.Apply(this);
}
