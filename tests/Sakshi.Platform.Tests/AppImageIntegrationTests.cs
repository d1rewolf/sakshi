using Sakshi.Core.Ports;
using Sakshi.Platform.Autostart;
using Sakshi.Platform.Linux;

namespace Sakshi.Platform.Tests;

public sealed class AppImageIntegrationTests : IDisposable
{
    private sealed class ListLog : ILog
    {
        public List<string> Lines { get; } = [];
        public void Info(string message) => Lines.Add(message);
    }

    private readonly string _home = Path.Combine(Path.GetTempPath(), "sakshi-appimage-" + Guid.NewGuid().ToString("N"));
    private readonly ListLog _log = new();
    private readonly byte[] _icon = [1, 2, 3, 4];

    private string Data => Path.Combine(_home, ".local", "share");
    private string Desktop => Path.Combine(Data, "applications", "sakshi.desktop");
    private string Icon => Path.Combine(Data, "icons", "hicolor", "256x256", "apps", "sakshi.png");
    private string Link => Path.Combine(_home, ".local", "bin", "sakshi");
    private string AutostartFile => Path.Combine(_home, ".config", "autostart", "sakshi.desktop");

    private XdgAutostart NewAutostart() => new(Path.Combine(_home, ".config"));

    private void Ensure(string appImage, IAutostart? autostart = null) =>
        AppImageIntegration.Ensure(appImage, _home, Data, _icon, autostart ?? NewAutostart(), _log);

    public void Dispose()
    {
        try { Directory.Delete(_home, recursive: true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    [Fact]
    public void Writes_the_desktop_file_the_icon_and_the_link()
    {
        Ensure("/opt/Sakshi.AppImage");

        var desktop = File.ReadAllText(Desktop);
        Assert.Contains("Name=Sakshi\n", desktop);
        Assert.Contains("Exec=\"/opt/Sakshi.AppImage\"\n", desktop);
        Assert.Contains("Icon=sakshi\n", desktop);
        Assert.Contains("Categories=AudioVideo;Recorder;\n", desktop);
        Assert.Contains("StartupWMClass=sakshi\n", desktop);
        Assert.Equal(_icon, File.ReadAllBytes(Icon));
        Assert.Equal("/opt/Sakshi.AppImage", new FileInfo(Link).LinkTarget);
    }

    [Fact]
    public void A_second_call_changes_nothing()
    {
        NewAutostart().Enable("/opt/Sakshi.AppImage");
        Ensure("/opt/Sakshi.AppImage");
        var old = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(Desktop, old);
        File.SetLastWriteTimeUtc(Icon, old);
        File.SetLastWriteTimeUtc(AutostartFile, old);
        var linkTime = new FileInfo(Link).LastWriteTimeUtc;

        Ensure("/opt/Sakshi.AppImage");

        Assert.Equal(old, File.GetLastWriteTimeUtc(Desktop));
        Assert.Equal(old, File.GetLastWriteTimeUtc(Icon));
        Assert.Equal(old, File.GetLastWriteTimeUtc(AutostartFile));
        Assert.Equal(linkTime, new FileInfo(Link).LastWriteTimeUtc);
        Assert.Equal("/opt/Sakshi.AppImage", new FileInfo(Link).LinkTarget);
    }

    [Fact]
    public void A_moved_AppImage_repoints_the_desktop_file_the_link_and_the_autostart_entry()
    {
        var autostart = NewAutostart();
        autostart.Enable("/opt/Sakshi.AppImage");
        Ensure("/opt/Sakshi.AppImage");

        Ensure("/home/j/Apps/Sakshi.AppImage");

        Assert.Contains("Exec=\"/home/j/Apps/Sakshi.AppImage\"\n", File.ReadAllText(Desktop));
        Assert.Equal("/home/j/Apps/Sakshi.AppImage", new FileInfo(Link).LinkTarget);
        Assert.Contains("Exec=\"/home/j/Apps/Sakshi.AppImage\"\n", File.ReadAllText(AutostartFile));
        Assert.True(autostart.IsEnabled);
    }

    [Fact]
    public void Autostart_that_is_off_stays_off()
    {
        Ensure("/opt/Sakshi.AppImage");

        Assert.False(File.Exists(AutostartFile));
    }

    [Fact]
    public void A_regular_file_at_the_link_path_is_kept_and_logged()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Link)!);
        File.WriteAllText(Link, "#!/bin/sh\n");

        Ensure("/opt/Sakshi.AppImage");

        Assert.Equal("#!/bin/sh\n", File.ReadAllText(Link));
        Assert.Null(new FileInfo(Link).LinkTarget);
        Assert.Contains(_log.Lines, l => l.Contains(Link) && l.Contains("not a symlink"));
        Assert.True(File.Exists(Desktop));   // the rest still happens
    }

    [Fact]
    public void A_directory_at_the_link_path_is_kept()
    {
        Directory.CreateDirectory(Link);

        Ensure("/opt/Sakshi.AppImage");

        Assert.True(Directory.Exists(Link));
        Assert.Null(new DirectoryInfo(Link).LinkTarget);
    }

    [Fact]
    public void The_Exec_path_is_quoted_for_the_desktop_file()
    {
        Ensure("/opt/My $Apps/Sakshi.AppImage");

        Assert.Contains("Exec=" + XdgAutostart.QuoteExec("/opt/My $Apps/Sakshi.AppImage") + "\n", File.ReadAllText(Desktop));
    }

    [Fact]
    public void DataHome_follows_XDG_DATA_HOME_when_it_is_absolute()
    {
        Assert.Equal("/x/data", AppImageIntegration.DataHome("/h", "/x/data"));
        Assert.Equal(Path.Combine("/h", ".local", "share"), AppImageIntegration.DataHome("/h", null));
        Assert.Equal(Path.Combine("/h", ".local", "share"), AppImageIntegration.DataHome("/h", "rel"));
    }
}
