using System.Globalization;

namespace Sakshi.Core.Ports;

/// <summary>Appends timestamped lines to a log file. Never throws: a log that cannot be written is skipped.</summary>
public sealed class FileLog(string path) : ILog
{
    public void Info(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path,
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " " + message + "\n");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
