using Sakshi.Core.Ports;

namespace Sakshi.TestSupport;

/// <summary>Records trashed paths and moves the file into the trash directory when one is set.</summary>
public sealed class FakeTrash(string? directory = null) : ITrash
{
    public List<string> Paths { get; } = [];

    /// <summary>When set, MoveToTrash throws this instead of trashing.</summary>
    public Exception? Failure { get; set; }

    public void MoveToTrash(string path)
    {
        if (Failure is not null) throw Failure;
        Paths.Add(path);
        if (directory is null) return;
        System.IO.Directory.CreateDirectory(directory);
        File.Move(path, Path.Combine(directory, Path.GetFileName(path)));
    }
}
