namespace Sakshi.Core.Ports;

public interface ITrash
{
    /// <summary>
    /// Moves a file or folder to the platform trash. Throws on failure. Blocks until done (it may run an
    /// external process): do not call it on a UI thread.
    /// </summary>
    void MoveToTrash(string path);
}
