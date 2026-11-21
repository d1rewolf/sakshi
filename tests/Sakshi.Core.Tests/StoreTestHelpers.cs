using System.Globalization;
using Sakshi.Core.Ports;

namespace Sakshi.Core.Tests;

internal sealed class RecordingLog : ILog
{
    public List<string> Lines { get; } = [];
    public void Info(string message) => Lines.Add(message);
}

internal static class StoreTestHelpers
{
    public static string Day(DateTime d) => d.ToString("yyyy-MM-dd-ddd", CultureInfo.InvariantCulture);

    /// <summary>Port of mkvideo: a 4 KiB file with a fixed mtime.</summary>
    public static string MkVideo(string path, string mtime = "2026-01-05 10:00")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[4096]);
        Touch(path, mtime);
        return path;
    }

    public static void Touch(string path, string mtime)
    {
        var t = DateTime.Parse(mtime, CultureInfo.InvariantCulture);
        if (Directory.Exists(path)) Directory.SetLastWriteTime(path, t);
        else File.SetLastWriteTime(path, t);
    }

    public static string Slash(string s) => s.Replace('\\', '/');
}
