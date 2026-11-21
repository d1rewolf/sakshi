namespace Sakshi.App.Tests;

public sealed class CliExecutableTests
{
    [Fact]
    public void The_windows_tray_runs_commands_through_the_console_exe_beside_it() =>
        Assert.Equal(Path.Combine(@"C:\Users\j\AppData\Local\sakshi\current", "sakshi.exe"),
            CliExecutable.Resolve(true, Path.Combine(@"C:\Users\j\AppData\Local\sakshi\current", "sakshiw.exe"), _ => true));

    [Fact]
    public void Without_the_console_exe_it_falls_back_to_itself() =>
        Assert.Equal("sakshiw.exe", CliExecutable.Resolve(true, "sakshiw.exe", _ => false));

    [Theory]
    [InlineData(false, "/opt/sakshi/sakshi")]
    [InlineData(true, @"C:\sakshi\sakshi.exe")]
    public void Anything_else_is_itself(bool windows, string path) =>
        Assert.Equal(path, CliExecutable.Resolve(windows, path, _ => true));
}
