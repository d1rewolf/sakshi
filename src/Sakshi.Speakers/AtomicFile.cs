using System.Text;

namespace Sakshi.Speakers;

public static class AtomicFile
{
    /// <summary>Writes UTF-8 (no BOM) to a temp file beside the target, then renames it over the target.</summary>
    public static void WriteAllText(string path, string text) => Write(path, text, privateFile: false);

    internal static void Write(string path, string text, bool privateFile)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        if (privateFile && !OperatingSystem.IsWindows())
        {
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        else Directory.CreateDirectory(dir);

        var tmp = Path.Combine(dir, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (privateFile && !OperatingSystem.IsWindows())
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var fs = new FileStream(tmp, options))
            {
                var bytes = new UTF8Encoding(false).GetBytes(text);
                fs.Write(bytes);
            }
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch { }
            throw;
        }
    }
}
