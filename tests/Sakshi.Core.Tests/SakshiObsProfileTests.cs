using Sakshi.Core.Config;
using Sakshi.Core.Obs;
using Sakshi.Core.Paths;

namespace Sakshi.Core.Tests;

public sealed class SakshiObsProfileTests : IDisposable
{
    private readonly string _obs = Path.Combine(Path.GetTempPath(), "sakshi-obsprofile-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_obs, true); } catch (IOException) { } }

    [Fact]
    public void Ini_values_are_read_by_section()
    {
        const string text = "[General]\nName=Mine\n\n[Video]\nBaseCX=1920\nName=not this one\n";
        Assert.Equal("Mine", IniText.Get(text, "General", "Name"));
        Assert.Equal("1920", IniText.Get(text, "Video", "BaseCX"));
        Assert.Null(IniText.Get(text, "Video", "BaseCY"));
    }

    [Fact]
    public void Ini_set_changes_adds_and_removes_and_keeps_the_rest()
    {
        var text = "[General]\nName=Mine\n\n[Video]\nBaseCX=1920\nScaleType=bicubic\n";
        text = IniText.Set(text, "Video", "BaseCX", "1280");
        text = IniText.Set(text, "General", "Extra", "1");
        text = IniText.Set(text, "Audio", "SampleRate", "48000");
        text = IniText.Set(text, "Video", "ScaleType", null);
        Assert.Equal("[General]\nName=Mine\nExtra=1\n\n[Video]\nBaseCX=1280\n\n[Audio]\nSampleRate=48000\n", text);
    }

    [Fact]
    public void The_profile_holds_the_configs_recording_settings()
    {
        var config = new SakshiConfig { RecordFps = 12, RecordMaxWidth = 1280, RecordMaxHeight = 720, RecordFormat = "mkv", RecordFolder = "/rec" };
        var text = SakshiObsProfile.Apply("[Output]\nMode=Advanced\n[Panels]\nKeep=me\n", config, HostOs.Linux,
            "[Audio]\nSampleRate=44100\nChannelSetup=Mono\n");

        Assert.Equal("Sakshi", IniText.Get(text, "General", "Name"));
        Assert.Equal("Advanced", IniText.Get(text, "Output", "Mode"));   // the only mode with a keyframe setting
        Assert.Equal("Standard", IniText.Get(text, "AdvOut", "RecType"));
        Assert.Equal("mkv", IniText.Get(text, "AdvOut", "RecFormat2"));
        Assert.Equal("/rec", IniText.Get(text, "AdvOut", "RecFilePath"));
        Assert.Equal("obs_x264", IniText.Get(text, "AdvOut", "RecEncoder"));
        Assert.Equal(("2", "12", "1"), (IniText.Get(text, "Video", "FPSType"), IniText.Get(text, "Video", "FPSNum"), IniText.Get(text, "Video", "FPSDen")));
        Assert.Equal(("1280", "720"), (IniText.Get(text, "Video", "BaseCX"), IniText.Get(text, "Video", "BaseCY")));
        Assert.Equal(("1280", "720"), (IniText.Get(text, "Video", "OutputCX"), IniText.Get(text, "Video", "OutputCY")));
        Assert.Equal(("44100", "Mono"), (IniText.Get(text, "Audio", "SampleRate"), IniText.Get(text, "Audio", "ChannelSetup")));
        Assert.Equal("me", IniText.Get(text, "Panels", "Keep"));
    }

    [Theory]
    [InlineData(HostOs.MacOS, "", "com.apple.videotoolbox.videoencoder.ave.avc")]
    [InlineData(HostOs.Windows, "", "obs_x264")]
    [InlineData(HostOs.Linux, "", "obs_x264")]
    [InlineData(HostOs.Windows, "nvenc", "obs_nvenc_h264_tex")]
    [InlineData(HostOs.Windows, " AMD ", "h264_texture_amf")]
    [InlineData(HostOs.Windows, "qsv", "obs_qsv11_v2")]
    [InlineData(HostOs.Linux, "ffmpeg_vaapi_tex", "ffmpeg_vaapi_tex")]   // any OBS encoder name, as it is
    public void The_encoder_is_the_configs_else_apples_on_a_mac_and_x264_elsewhere(HostOs os, string configured, string expected) =>
        Assert.Equal(expected, SakshiObsProfile.Encoder(new SakshiConfig { RecordEncoder = configured }, os));

    [Theory]
    [InlineData("obs_x264", "CRF")]
    [InlineData("obs_nvenc_h264_tex", "CQP")]
    [InlineData("com.apple.videotoolbox.videoencoder.ave.avc", "CRF")]
    [InlineData("ffmpeg_vaapi_tex", null)]
    public void Every_encoder_gets_a_keyframe_every_two_seconds(string encoder, string? rateControl)
    {
        using var json = System.Text.Json.JsonDocument.Parse(SakshiObsProfile.EncoderSettings(encoder));
        Assert.Equal(2, json.RootElement.GetProperty("keyint_sec").GetInt32());
        Assert.Equal(rateControl, json.RootElement.TryGetProperty("rate_control", out var r) ? r.GetString() : null);
    }

    [Fact]
    public void A_blank_folder_leaves_obs_its_own()
    {
        var text = SakshiObsProfile.Apply("[AdvOut]\nRecFilePath=/old\n", new SakshiConfig(), HostOs.Linux, null);
        Assert.Null(IniText.Get(text, "AdvOut", "RecFilePath"));
    }

    [Theory]
    [InlineData(1280, 768, 1280, 768)]     // fits: its own size
    [InlineData(800, 600, 800, 600)]
    [InlineData(3840, 2160, 1920, 1080)]   // too big: scaled down, same shape
    [InlineData(2560, 1600, 1728, 1080)]   // taller shape: the height decides
    [InlineData(1366, 767, 1368, 768)]     // width a multiple of 4, height even
    public void The_frame_is_the_windows_size_within_the_maximum(long w, long h, int fw, int fh) =>
        Assert.Equal((fw, fh), SakshiObsProfile.Frame(w, h, 1920, 1080));

    [Fact]
    public void Profiles_are_found_by_their_name_not_their_folder()
    {
        var dir = Path.Combine(SakshiObsProfile.ProfilesDir(_obs), "Some_Folder");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "basic.ini"), "[General]\nName=Work\n");
        Assert.Equal(dir, SakshiObsProfile.DirectoryOf(_obs, "Work"));
        Assert.Null(SakshiObsProfile.DirectoryOf(_obs, "Some_Folder"));
    }

    [Fact]
    public void A_folder_called_Sakshi_that_holds_another_profile_is_left_alone()
    {
        var theirs = Path.Combine(SakshiObsProfile.ProfilesDir(_obs), "Sakshi");
        Directory.CreateDirectory(theirs);
        File.WriteAllText(Path.Combine(theirs, "basic.ini"), "[General]\nName=Streaming\n");

        SakshiObsProfile.Write(_obs, new SakshiConfig(), HostOs.Linux, null);

        Assert.Equal("[General]\nName=Streaming\n", File.ReadAllText(Path.Combine(theirs, "basic.ini")));
        Assert.Equal(Path.Combine(SakshiObsProfile.ProfilesDir(_obs), "Sakshi_2"), SakshiObsProfile.DirectoryOf(_obs, "Sakshi"));
    }

    [Fact]
    public void Writing_makes_the_profile_and_copies_the_users_audio_format()
    {
        var user = Path.Combine(SakshiObsProfile.ProfilesDir(_obs), "Untitled");
        Directory.CreateDirectory(user);
        File.WriteAllText(Path.Combine(user, "basic.ini"), "[General]\nName=Untitled\n[Audio]\nSampleRate=44100\n");

        SakshiObsProfile.Write(_obs, new SakshiConfig(), HostOs.Windows, "Untitled");

        var dir = SakshiObsProfile.DirectoryOf(_obs, "Sakshi");
        Assert.NotNull(dir);
        var text = File.ReadAllText(Path.Combine(dir, "basic.ini"));
        Assert.Equal("44100", IniText.Get(text, "Audio", "SampleRate"));
        Assert.Equal("15", IniText.Get(text, "Video", "FPSNum"));
        Assert.Contains("\"keyint_sec\": 2", File.ReadAllText(Path.Combine(dir, "recordEncoder.json")));
        Assert.Equal("[General]\nName=Untitled\n[Audio]\nSampleRate=44100\n", File.ReadAllText(Path.Combine(user, "basic.ini")));
    }
}
