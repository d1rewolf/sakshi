namespace Sakshi.Platform.Autostart;

/// <summary>Autostart through the per-user Run key: a value named Sakshi whose data is the quoted exe path.</summary>
public sealed class WindowsRunKeyAutostart(IRunKey key) : IAutostart
{
    internal const string ValueName = "Sakshi";

    public bool IsEnabled => !string.IsNullOrWhiteSpace(key.Get(ValueName));

    public void Enable(string executable)
    {
        var value = $"\"{executable}\"";
        if (key.Get(ValueName) != value) key.Set(ValueName, value);
    }

    public void Disable()
    {
        if (key.Get(ValueName) is not null) key.Delete(ValueName);
    }
}
