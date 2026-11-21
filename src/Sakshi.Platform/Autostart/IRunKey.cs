namespace Sakshi.Platform.Autostart;

/// <summary>The values of the per-user Windows Run key; an interface so tests never touch the registry.</summary>
public interface IRunKey
{
    string? Get(string name);
    void Set(string name, string value);
    void Delete(string name);
}
