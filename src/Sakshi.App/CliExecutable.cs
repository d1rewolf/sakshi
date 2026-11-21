namespace Sakshi.App;

/// <summary>
/// The executable that runs a sakshi command line (transcribe, live-transcribe). On Windows the tray runs as
/// <c>sakshiw.exe</c>, which is always the tray whatever the arguments; the CLI is <c>sakshi.exe</c> beside it.
/// </summary>
internal static class CliExecutable
{
    public static string Resolve() => Resolve(OperatingSystem.IsWindows(), Environment.ProcessPath, File.Exists);

    internal static string Resolve(bool windows, string? processPath, Func<string, bool> exists)
    {
        if (windows && processPath is { Length: > 0 }
            && Path.GetFileName(processPath).Equals("sakshiw.exe", StringComparison.OrdinalIgnoreCase)
            && Path.GetDirectoryName(processPath) is { } dir)
        {
            var cli = Path.Combine(dir, "sakshi.exe");
            if (exists(cli)) return cli;
        }
        return processPath ?? "sakshi";
    }
}
