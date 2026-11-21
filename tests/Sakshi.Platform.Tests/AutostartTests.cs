using Sakshi.Platform.Autostart;

namespace Sakshi.Platform.Tests;

public sealed class AutostartTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sakshi-autostart-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private sealed class FakeRunKey : IRunKey
    {
        public Dictionary<string, string> Values { get; } = [];
        public int Writes { get; private set; }
        public string? Get(string name) => Values.GetValueOrDefault(name);
        public void Set(string name, string value) { Values[name] = value; Writes++; }
        public void Delete(string name) => Values.Remove(name);
    }

    [Fact]
    public void Windows_writes_the_quoted_path_under_the_Sakshi_value_and_is_idempotent()
    {
        var key = new FakeRunKey();
        var a = new WindowsRunKeyAutostart(key);
        Assert.False(a.IsEnabled);

        a.Enable(@"C:\Users\j\AppData\Local\sakshi\Sakshi.exe");
        a.Enable(@"C:\Users\j\AppData\Local\sakshi\Sakshi.exe");

        Assert.True(a.IsEnabled);
        Assert.Equal("\"C:\\Users\\j\\AppData\\Local\\sakshi\\Sakshi.exe\"", key.Values["Sakshi"]);
        Assert.Equal(1, key.Writes);
        a.Disable();
        a.Disable();
        Assert.False(a.IsEnabled);
        Assert.Empty(key.Values);
    }

    [Fact]
    public void Windows_enable_repoints_an_entry_that_names_another_exe()
    {
        var key = new FakeRunKey();
        key.Values["Sakshi"] = "\"C:\\old\\Sakshi.exe\"";
        new WindowsRunKeyAutostart(key).Enable(@"C:\new\Sakshi.exe");
        Assert.Equal("\"C:\\new\\Sakshi.exe\"", key.Values["Sakshi"]);
    }

    [Fact]
    public void Mac_writes_the_launch_agent_plist()
    {
        var a = new MacLaunchAgentAutostart(_root);
        var file = Path.Combine(_root, "Library", "LaunchAgents", "io.github.d1rewolf.sakshi.plist");
        Assert.False(a.IsEnabled);

        a.Enable("/Applications/Sakshi & Co/sakshi");
        var first = File.ReadAllText(file);
        a.Enable("/Applications/Sakshi & Co/sakshi");

        Assert.Equal(first, File.ReadAllText(file));
        Assert.True(a.IsEnabled);
        Assert.Equal(
            """
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
                <key>Label</key>
                <string>io.github.d1rewolf.sakshi</string>
                <key>ProgramArguments</key>
                <array>
                    <string>/Applications/Sakshi &amp; Co/sakshi</string>
                </array>
                <key>RunAtLoad</key>
                <true/>
                <key>ProcessType</key>
                <string>Interactive</string>
            </dict>
            </plist>

            """, first);
        a.Disable();
        a.Disable();
        Assert.False(a.IsEnabled);
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void Linux_writes_the_autostart_desktop_file()
    {
        var a = new XdgAutostart(_root);
        var file = Path.Combine(_root, "autostart", "sakshi.desktop");
        Assert.False(a.IsEnabled);

        a.Enable("/home/j/Apps/Sakshi.AppImage");
        a.Enable("/home/j/Apps/Sakshi.AppImage");

        Assert.True(a.IsEnabled);
        Assert.Equal("[Desktop Entry]\nType=Application\nName=Sakshi\nExec=\"/home/j/Apps/Sakshi.AppImage\"\nIcon=sakshi\nX-GNOME-Autostart-enabled=true\n",
            File.ReadAllText(file));
        a.Disable();
        a.Disable();
        Assert.False(a.IsEnabled);
    }

    [Fact]
    public void Linux_entry_switched_off_by_the_desktop_counts_as_disabled()
    {
        var a = new XdgAutostart(_root);
        a.Enable("/x/sakshi");
        var file = Path.Combine(_root, "autostart", "sakshi.desktop");
        File.WriteAllText(file, File.ReadAllText(file).Replace("enabled=true", "enabled=false"));
        Assert.False(a.IsEnabled);
    }

    [Theory]
    [InlineData("/plain/sakshi", "\"/plain/sakshi\"")]
    [InlineData("/with space/sakshi", "\"/with space/sakshi\"")]
    [InlineData("/q\"uote", "\"/q\\\\\"uote\"")]
    [InlineData("/d$ollar", "\"/d\\\\$ollar\"")]
    [InlineData("/b`tick", "\"/b\\\\`tick\"")]
    [InlineData("/back\\slash", "\"/back\\\\\\\\slash\"")]
    [InlineData("/100%", "\"/100%%\"")]
    public void Linux_exec_is_quoted_and_escaped_per_the_desktop_entry_spec(string path, string expected) =>
        Assert.Equal(expected, XdgAutostart.QuoteExec(path));

    [Fact]
    public void Failure_to_write_is_an_IOException()
    {
        File.WriteAllText(_root, "a file where the config folder should be");
        Assert.ThrowsAny<IOException>(() => new XdgAutostart(_root).Enable("/x/sakshi"));
    }
}
