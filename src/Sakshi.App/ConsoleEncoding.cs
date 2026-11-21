using System.Text;

namespace Sakshi.App;

public enum ConsoleMode
{
    /// <summary>Wrap stdout/stderr in UTF-8 (no BOM) writers; the console code page is untouched.</summary>
    Utf8Streams,

    /// <summary>A Windows console: switch its output code page to UTF-8 for the run, then restore it.</summary>
    WindowsCodePage,
}

internal static class ConsoleEncoding
{
    public static ConsoleMode Choose(bool isWindows, bool outRedirected, bool errRedirected) =>
        isWindows && (!outRedirected || !errRedirected) ? ConsoleMode.WindowsCodePage : ConsoleMode.Utf8Streams;

    /// <summary>Applies the chosen mode; dispose the result to undo it.</summary>
    public static IDisposable Apply()
    {
        var mode = Choose(OperatingSystem.IsWindows(), Console.IsOutputRedirected, Console.IsErrorRedirected);
        var utf8 = new UTF8Encoding(false);
        if (mode == ConsoleMode.WindowsCodePage)
        {
            Encoding? original = null;
            try
            {
                original = Console.OutputEncoding;
                Console.OutputEncoding = utf8;
            }
            catch (Exception e) when (e is IOException or PlatformNotSupportedException)
            {
                original = null; // no console to configure: keep what .NET does
            }
            return new Restore(original);
        }
        Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = true });
        Console.SetError(new StreamWriter(Console.OpenStandardError(), utf8) { AutoFlush = true });
        return new Restore(null);
    }

    private sealed class Restore(Encoding? original) : IDisposable
    {
        public void Dispose()
        {
            if (original is null) return;
            try { Console.OutputEncoding = original; }
            catch (Exception e) when (e is IOException or PlatformNotSupportedException) { }
        }
    }
}
