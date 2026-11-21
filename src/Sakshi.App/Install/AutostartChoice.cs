namespace Sakshi.App.Install;

/// <summary>
/// Remembers that the user (or the first run) has decided about start at login. Velopack reports a first run
/// after every Setup, so without this a reinstall would turn back on what the user switched off.
/// </summary>
internal sealed class AutostartChoice(string stateDir)
{
    private string Marker => Path.Combine(stateDir, "autostart-chosen");

    public bool Made => File.Exists(Marker);

    public void Record()
    {
        Directory.CreateDirectory(stateDir);
        File.WriteAllText(Marker, "");
    }

    /// <summary>Uninstall: a later reinstall is a fresh install again, so it starts at login by default.</summary>
    public void Forget()
    {
        if (File.Exists(Marker)) File.Delete(Marker);
    }
}
