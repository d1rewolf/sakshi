namespace Sakshi.TestSupport;

/// <summary>
/// A throwaway home directory. Points SAKSHI_CONFIG_DIR, SAKSHI_STATE_DIR and
/// SAKSHI_DATA_DIR inside itself, clears SAKSHI_CONFIG (the bash config `config import`
/// reads) and the XDG base-directory vars, and restores all of them on dispose. Env vars
/// are process-wide: tests using this belong in the "Environment" collection.
/// </summary>
public sealed class TempHome : IDisposable
{
    private static readonly string[] VarNames =
    [
        "SAKSHI_CONFIG_DIR", "SAKSHI_STATE_DIR", "SAKSHI_DATA_DIR",
        "SAKSHI_CONFIG", "XDG_CONFIG_HOME", "XDG_STATE_HOME", "XDG_DATA_HOME",
    ];
    private readonly string?[] _saved;

    public string Root { get; }
    public string Home { get; }
    public string Meetings { get; }
    public string ConfigDir { get; }
    public string StateDir { get; }
    public string DataDir { get; }

    public TempHome()
    {
        Root = Path.Combine(Path.GetTempPath(), "sakshi-test-" + Guid.NewGuid().ToString("N"));
        Home = Path.Combine(Root, "home");
        Meetings = Path.Combine(Home, "meetings");
        ConfigDir = Path.Combine(Root, "config");
        StateDir = Path.Combine(Root, "state");
        DataDir = Path.Combine(Root, "data");
        foreach (var d in new[] { Home, Meetings, ConfigDir, StateDir, DataDir })
            Directory.CreateDirectory(d);

        _saved = VarNames.Select(Environment.GetEnvironmentVariable).ToArray();
        Environment.SetEnvironmentVariable("SAKSHI_CONFIG_DIR", ConfigDir);
        Environment.SetEnvironmentVariable("SAKSHI_STATE_DIR", StateDir);
        Environment.SetEnvironmentVariable("SAKSHI_DATA_DIR", DataDir);
        foreach (var v in new[] { "SAKSHI_CONFIG", "XDG_CONFIG_HOME", "XDG_STATE_HOME", "XDG_DATA_HOME" })
            Environment.SetEnvironmentVariable(v, null);
    }

    public void Dispose()
    {
        for (var i = 0; i < VarNames.Length; i++)
            Environment.SetEnvironmentVariable(VarNames[i], _saved[i]);
        try { Directory.Delete(Root, recursive: true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
