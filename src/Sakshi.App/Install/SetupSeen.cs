namespace Sakshi.App.Install;

/// <summary>Remembers that the setup window has been shown once, so an installed copy opens it unasked only the first time.</summary>
internal sealed class SetupSeen(string stateDir)
{
    private string Marker => Path.Combine(stateDir, "setup-shown");

    public bool Seen => File.Exists(Marker);

    public void Record()
    {
        Directory.CreateDirectory(stateDir);
        File.WriteAllText(Marker, "");
    }
}
