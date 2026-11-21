namespace Sakshi.Platform.Autostart;

/// <summary>Shared by the macOS and Linux autostart: one file that exists exactly when autostart is on.</summary>
public abstract class FileAutostart(string file) : IAutostart
{
    public abstract bool IsEnabled { get; }

    protected string File { get; } = file;

    protected abstract string Content(string executable);

    public void Enable(string executable)
    {
        var content = Content(executable);
        try
        {
            if (System.IO.File.Exists(File) && System.IO.File.ReadAllText(File) == content) return;
            Directory.CreateDirectory(Path.GetDirectoryName(File)!);
            System.IO.File.WriteAllText(File, content);
        }
        catch (UnauthorizedAccessException e) { throw new IOException(e.Message, e); }
    }

    public void Disable()
    {
        try { System.IO.File.Delete(File); }   // no error when it is already gone
        catch (UnauthorizedAccessException e) { throw new IOException(e.Message, e); }
    }
}
