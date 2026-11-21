using Sakshi.Core.Config;
using Sakshi.Core.Paths;

namespace Sakshi.Core.Tests;

public sealed class ConfigTests
{
    private const string Home = "/home/test";
    private const string Model = "/usr/local/share/whisper.cpp/models/ggml-base.en.bin";

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Sakshi.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Sakshi.slnx not found");
    }

    private static SakshiConfig Import(string text, Func<string, bool>? exists = null, List<string>? warnings = null) =>
        BashConfigImport.Import(text, HostOs.Linux, Home, exists ?? (_ => false), warnings is null ? null : warnings.Add);

    [Fact]
    public void Import_ShippedBashConfig()
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot(), "tests", "fixtures", "bash", "config"));
        var cfg = Import(text);

        Assert.Equal(6, cfg.FramesModes.Count);
        Assert.Equal("file-meeting", cfg.FramesModes[1].HandOff);
        Assert.Null(cfg.FramesModes[0].HandOff);
        Assert.NotEmpty(cfg.FramesModes[0].Glyph); // the shipped config carries a nerd-font glyph
        Assert.Equal("Changes → Notes", cfg.FramesModes[1].Label);
        Assert.Equal(Home + "/meetings", cfg.MeetingsDir);
        Assert.Equal(14, cfg.KeepDays);
        Assert.Equal(@"zoom|teams|meet\.google|webex|gotomeeting", cfg.PickerPriorityRegex);
        Assert.Equal(4455, cfg.ObsWsPort);
        Assert.True(cfg.ObsReselectWindow);
        Assert.Equal(1800, cfg.RemindInterval);
        Assert.Equal([Home + "/bin/omarchy", Home + "/bin"], cfg.HandOffPath);
    }

    [Fact]
    public void Import_MapsTranscribePathToHandOffPath_AndDropsTranscribeCmd()
    {
        var warnings = new List<string>();
        var cfg = Import("TRANSCRIBE_CMD=\"my-transcriber\"\nTRANSCRIBE_PATH=\"$HOME/bin/omarchy:$HOME/bin\"\n", warnings: warnings);
        Assert.Equal([Home + "/bin/omarchy", Home + "/bin"], cfg.HandOffPath);
        Assert.Equal(["bash config: TRANSCRIBE_CMD ignored (transcription is built in)"], warnings);
    }

    [Fact]
    public void Import_SetsModelWhenWhisperCppModelExists()
    {
        Assert.Equal(Model, Import("", p => p == Model).Model);
        Assert.Equal("", Import("", _ => false).Model);
    }

    [Fact]
    public void Import_FourFieldRowsAndEmptyGlyph()
    {
        var cfg = Import("FRAMES_MODES=(\n  \"|None|-F none|Transcript only, fastest\"\n)\n");
        var row = Assert.Single(cfg.FramesModes);
        Assert.Equal(new FrameMode("", "None", "-F none", "Transcript only, fastest", null), row);
    }

    [Fact]
    public void Import_IgnoresCommentsAndUnknownLines()
    {
        var warnings = new List<string>();
        var cfg = Import("# a comment\n\nKEEP_DAYS=3 # trailing\nWHAT_IS_THIS=1\nif true; then :; fi\n", warnings: warnings);
        Assert.Equal(3, cfg.KeepDays);
        Assert.Equal(2, warnings.Count);
    }

    [Fact]
    public void Import_WithoutTranscribePath_UsesBashDefaultSearchPath()
    {
        Assert.Equal([Home + "/bin/omarchy", Home + "/bin"], Import("").HandOffPath);
        Assert.Empty(BashConfigImport.Import("", HostOs.Windows, Home, _ => false).HandOffPath);
    }

    [Fact]
    public void Import_FlagsAreTrueOnlyForOne()
    {
        var cfg = Import("KEEP_PROMPT=2\nFRAMES_PROMPT=1\nOBS_RESELECT_WINDOW=0\n");
        Assert.False(cfg.KeepPrompt);
        Assert.True(cfg.FramesPrompt);
        Assert.False(cfg.ObsReselectWindow);
    }

    [Fact]
    public void Import_HandOffMayContainPipe_AndThreeFieldRowsHaveEmptyDescription()
    {
        var cfg = Import("FRAMES_MODES=(\n \"|A|-F none|d|cmd a|b\"\n \"|B|-F all\"\n)\n");
        Assert.Equal("cmd a|b", cfg.FramesModes[0].HandOff);
        Assert.Equal(new FrameMode("", "B", "-F all", "", null), cfg.FramesModes[1]);
    }

    [Fact]
    public void Load_ExplicitNulls_MeanDefaults()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sakshi-cfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "config.json");
            File.WriteAllText(path, "{\"model\": null, \"handOffPath\": null, \"meetingsDir\": null}");
            var cfg = ConfigStore.Load(path, HostOs.Linux, Home);
            Assert.Equal("", cfg.Model);
            Assert.Equal([Path.Combine(Home, "bin")], cfg.HandOffPath);
            Assert.Equal(Path.Combine(Home, "meetings"), cfg.MeetingsDir);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Load_MalformedJson_ThrowsInvalidDataExceptionNamingFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sakshi-cfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "config.json");
            File.WriteAllText(path, "{ not json");
            var ex = Assert.Throws<InvalidDataException>(() => ConfigStore.Load(path, HostOs.Linux, Home));
            Assert.Contains(path, ex.Message);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Load_MissingFile_GivesDefaults()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "config.json");
        Assert.Equal(SakshiConfig.Defaults(HostOs.Linux, Home), ConfigStore.Load(path, HostOs.Linux, Home), ConfigEquality.Instance);
    }

    [Fact]
    public void SaveThenLoad_RoundTrips()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sakshi-cfg-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(dir, "config.json");
            var cfg = SakshiConfig.Defaults(HostOs.Linux, Home) with
            {
                KeepDays = 7,
                Model = "/m/ggml.bin",
                HandOffPath = ["/a", "/b"],
                FramesModes = [new("", "X", "-F none", "d", "file-meeting --go")],
            };
            ConfigStore.Save(path, cfg);
            Assert.Equal(cfg, ConfigStore.Load(path, HostOs.Linux, Home), ConfigEquality.Instance);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void Load_AllowsComments_TrailingCommas_AndTilde()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sakshi-cfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "config.json");
            File.WriteAllText(path, "{\n // comment\n \"keepDays\": 5,\n \"meetingsDir\": \"~/m\",\n \"bogus\": 1,\n}");
            var warnings = new List<string>();
            var cfg = ConfigStore.Load(path, HostOs.Linux, Home, warnings.Add);
            Assert.Equal(5, cfg.KeepDays);
            Assert.Equal(Path.Combine(Home, "m"), cfg.MeetingsDir);
            Assert.Equal([Path.Combine(Home, "bin")], cfg.HandOffPath);
            Assert.Single(warnings);
            Assert.Equal(Path.Combine(Home, "m", "archives"), cfg.EffectiveArchiveDir);
        }
        finally { Directory.Delete(dir, true); }
    }

    private static SakshiConfig LoadText(string json, HostOs os = HostOs.Linux)
    {
        var dir = Path.Combine(Path.GetTempPath(), "sakshi-cfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "config.json");
            File.WriteAllText(path, json);
            return ConfigStore.Load(path, os, Home);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Load_BlankMeetingsAndArchiveDirs_MeanDefaults(string blank)
    {
        var cfg = LoadText($"{{\"meetingsDir\": \"{blank}\", \"archiveDir\": \"{blank}\"}}");
        Assert.Equal(Path.Combine(Home, "meetings"), cfg.MeetingsDir);
        Assert.Equal(Path.Combine(Home, "meetings", "archives"), cfg.EffectiveArchiveDir);
    }

    [Fact]
    public void Load_NullsInsideArrays_AreDropped()
    {
        var cfg = LoadText("{\"framesModes\": [null, {\"label\": \"X\", \"args\": \"-F none\"}, null], \"handOffPath\": [null, \"~/x\"]}");
        Assert.Equal([new FrameMode("", "X", "-F none", "", null)], cfg.FramesModes);
        Assert.Equal([Path.Combine(Home, "x")], cfg.HandOffPath);
    }

    [Fact]
    public void Load_ArraysOfOnlyNulls_MeanDefaults()
    {
        var cfg = LoadText("{\"framesModes\": [null], \"handOffPath\": [null]}");
        var defaults = SakshiConfig.Defaults(HostOs.Linux, Home);
        Assert.Equal(defaults.FramesModes, cfg.FramesModes);
        Assert.Equal(defaults.HandOffPath, cfg.HandOffPath);
    }

    [Fact]
    public void Load_EmptyArraysStayEmpty()
    {
        Assert.Empty(LoadText("{\"handOffPath\": []}").HandOffPath);
    }

    [Fact]
    public void Save_ReplacesTheFileAtomically()
    {
        // A reader holding the old file keeps seeing it whole: Save writes a new file and renames
        // it over the old one instead of truncating and rewriting it in place.
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows will not rename over a file another handle has open");
        var dir = Path.Combine(Path.GetTempPath(), "sakshi-cfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "config.json");
            File.WriteAllText(path, "{\"keepDays\": 3}");
            using (var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                ConfigStore.Save(path, SakshiConfig.Defaults(HostOs.Linux, Home) with { KeepDays = 9 });
                Assert.Equal("{\"keepDays\": 3}", new StreamReader(reader).ReadToEnd());
            }
            Assert.Equal(9, ConfigStore.Load(path, HostOs.Linux, Home).KeepDays);
            Assert.Equal(["config.json"], Directory.GetFiles(dir).Select(Path.GetFileName));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData(HostOs.Linux, "Screen Capture (PipeWire)")]
    [InlineData(HostOs.MacOS, "Sakshi Window Capture")]
    [InlineData(HostOs.Windows, "Sakshi Window Capture")]
    public void ObsSource_default_follows_the_os(HostOs os, string expected)
    {
        Assert.Equal(expected, SakshiConfig.Defaults(os, Home).ObsSource);
        Assert.Equal(expected, LoadText("{\"keepDays\": 3}", os).ObsSource);
        Assert.Equal("Mine", LoadText("{\"obsSource\": \"Mine\"}", os).ObsSource);
        Assert.Equal(expected, LoadText("{\"obsSource\": null}", os).ObsSource);
    }

    [Theory]
    [InlineData(HostOs.Linux)]
    [InlineData(HostOs.MacOS)]
    [InlineData(HostOs.Windows)]
    public void Starter_file_names_common_keys_and_loads_as_the_defaults(HostOs os)
    {
        var dir = Path.Combine(Path.GetTempPath(), "sakshi-cfg-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(dir, "sub", "config.json");
            Assert.True(ConfigStore.CreateStarter(path, os));

            var text = File.ReadAllText(path);
            Assert.Contains($"// \"obsSource\": \"{SakshiConfig.Defaults(os, Home).ObsSource}\",", text);
            foreach (var key in new[] { "meetingsDir", "obsScene", "obsSource", "pickerPriorityRegex" })
                Assert.Contains(key, text);
            var warnings = new List<string>();
            Assert.Equal(SakshiConfig.Defaults(os, Home), ConfigStore.Load(path, os, Home, warnings.Add), ConfigEquality.Instance);
            Assert.Empty(warnings);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Starter_file_never_replaces_a_config()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sakshi-cfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "config.json");
            File.WriteAllText(path, "{\"keepDays\": 3}");
            Assert.False(ConfigStore.CreateStarter(path, HostOs.Linux));
            Assert.Equal("{\"keepDays\": 3}", File.ReadAllText(path));
        }
        finally { Directory.Delete(dir, true); }
    }

    private sealed class ConfigEquality : IEqualityComparer<SakshiConfig>
    {
        public static readonly ConfigEquality Instance = new();
        public bool Equals(SakshiConfig? a, SakshiConfig? b) =>
            a is not null && b is not null
            && a with { HandOffPath = [], FramesModes = [] } == b with { HandOffPath = [], FramesModes = [] }
            && a.HandOffPath.SequenceEqual(b.HandOffPath)
            && a.FramesModes.SequenceEqual(b.FramesModes);
        public int GetHashCode(SakshiConfig c) => c.KeepDays;
    }
}
