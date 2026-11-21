using System.Text.Json;
using Sakshi.App.Tray;
using Sakshi.Core.Paths;
using Sakshi.TestSupport;

namespace Sakshi.App.Tests;

[Collection("Environment")]
public sealed class TrayServicesTests : IDisposable
{
    private readonly TempHome _home = new();
    private readonly SakshiPaths _paths;
    private readonly string _bashConfig;

    public TrayServicesTests()
    {
        _paths = SakshiPaths.Resolve(HostOs.Linux, _home.Home, Environment.GetEnvironmentVariable);
        _bashConfig = Path.Combine(_home.Home, ".config", "sakshi", "config");
    }

    public void Dispose() => _home.Dispose();

    private void WriteBashConfig()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_bashConfig)!);
        File.WriteAllText(_bashConfig, "OBS_SCENE=\"Meetings\"\nKEEP_DAYS=7\nREMIND_INTERVAL=600\n");
    }

    private static void LinuxOnly()
    {
        if (!OperatingSystem.IsLinux()) Assert.Skip("the tray's Linux adapters are built only on Linux in stage 2");
    }

    [Fact]
    public void FirstStart_ImportsTheBashConfig_IntoConfigJson()
    {
        LinuxOnly();
        WriteBashConfig();

        var s = TrayServices.Create(_paths, HostOs.Linux, _home.Home, new FakePrompter());

        Assert.True(File.Exists(_paths.ConfigFile));
        Assert.Equal("Meetings", s.Config.ObsScene);
        Assert.Equal(7, s.Config.KeepDays);
        Assert.Equal(600, s.Config.RemindInterval);
        using var json = JsonDocument.Parse(File.ReadAllText(_paths.ConfigFile));
        Assert.Equal("Meetings", json.RootElement.GetProperty("obsScene").GetString());
        Assert.Contains(File.ReadAllLines(_paths.LogFile), l => l.Contains($"imported {_bashConfig} into {_paths.ConfigFile}", StringComparison.Ordinal));
    }

    [Fact]
    public void ExistingConfigJson_IsNotReplaced()
    {
        LinuxOnly();
        WriteBashConfig();
        Directory.CreateDirectory(_paths.ConfigDir);
        File.WriteAllText(_paths.ConfigFile, "{ \"obsScene\": \"Mine\" }");

        var s = TrayServices.Create(_paths, HostOs.Linux, _home.Home, new FakePrompter());

        Assert.Equal("Mine", s.Config.ObsScene);
        Assert.Equal("{ \"obsScene\": \"Mine\" }", File.ReadAllText(_paths.ConfigFile));
    }

    [Fact]
    public void NoBashConfig_UsesTheDefaults_WithoutWritingAConfig()
    {
        LinuxOnly();

        var s = TrayServices.Create(_paths, HostOs.Linux, _home.Home, new FakePrompter());

        Assert.False(File.Exists(_paths.ConfigFile));
        Assert.Equal("Scene", s.Config.ObsScene);
        Assert.Equal(Path.Combine(_home.Home, "meetings"), s.Config.MeetingsDir);
    }

    [Theory]
    [InlineData(HostOs.Linux, "obs")]
    [InlineData(HostOs.MacOS, "OBS")]
    [InlineData(HostOs.Windows, "obs64")]
    public void EveryOs_BuildsTheServices_WithItsObsProcessName(HostOs os, string processName)
    {
        var s = TrayServices.Create(_paths, os, _home.Home, new FakePrompter());

        Assert.Equal(processName, s.ObsProcessName);
        Assert.Equal(os, s.Os);
    }
}
