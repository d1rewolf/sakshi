namespace Sakshi.Tray;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args) => Sakshi.App.Program.RunTray(args);
}
