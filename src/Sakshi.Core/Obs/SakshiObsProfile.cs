using System.Globalization;
using Sakshi.Core.Config;
using Sakshi.Core.Paths;

namespace Sakshi.Core.Obs;

/// <summary>
/// Sakshi's own OBS profile and scene collection, so the user's OBS setup is never changed. The profile's
/// basic.ini holds the recording settings from Sakshi's config, and its recordEncoder.json the encoder's. OBS reads
/// both when it switches to the profile (the encoder only then), so they are written while OBS is on another profile,
/// or not running.
///
/// The profile uses OBS's advanced output mode, the only one that sets a recording's keyframe interval. Hybrid MP4
/// adds to the file one keyframe at a time, so the live transcript can read the sound only that often: OBS's default
/// of 250 frames was 17 s at 15 frames a second; <see cref="KeyframeSeconds"/> makes it 2 s.
/// </summary>
public static class SakshiObsProfile
{
    /// <summary>The name of both Sakshi's OBS profile and its scene collection.</summary>
    public const string Name = "Sakshi";

    private const string SettingsFile = "basic.ini";
    private const string EncoderFile = "recordEncoder.json";

    /// <summary>Seconds between keyframes: how often the live transcript can see new sound.</summary>
    public const int KeyframeSeconds = 2;

    public static string ProfilesDir(string obsConfigDir) => Path.Combine(obsConfigDir, "basic", "profiles");

    /// <summary>The folder of the OBS profile called <paramref name="name"/>, or null when there is none.</summary>
    public static string? DirectoryOf(string obsConfigDir, string name)
    {
        var root = ProfilesDir(obsConfigDir);
        if (!Directory.Exists(root)) return null;
        foreach (var dir in Directory.GetDirectories(root))
        {
            var ini = Path.Combine(dir, SettingsFile);
            if (!File.Exists(ini)) continue;
            // OBS names a profile by its General/Name, falling back to the folder's name.
            if ((IniText.Get(File.ReadAllText(ini), "General", "Name") ?? Path.GetFileName(dir)) == name) return dir;
        }
        return null;
    }

    /// <summary>
    /// Writes Sakshi's settings into its profile, creating the profile's folder when missing. The audio format is
    /// copied from <paramref name="userProfile"/> (the profile OBS is on), so switching never asks for a restart.
    /// </summary>
    public static void Write(string obsConfigDir, SakshiConfig config, HostOs os, string? userProfile)
    {
        var dir = DirectoryOf(obsConfigDir, Name) ?? FreeDirectory(ProfilesDir(obsConfigDir));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, SettingsFile);
        var userIni = userProfile is not null && DirectoryOf(obsConfigDir, userProfile) is { } u
            ? Path.Combine(u, SettingsFile) : null;
        var text = Apply(File.Exists(file) ? File.ReadAllText(file) : "", config, os,
            userIni is not null && File.Exists(userIni) ? File.ReadAllText(userIni) : null);
        WriteFile(file, text);
        WriteFile(Path.Combine(dir, EncoderFile), EncoderSettings(Encoder(config, os)));
    }

    /// <summary>A new folder for Sakshi's profile: "Sakshi", unless another profile already lives there.</summary>
    private static string FreeDirectory(string root)
    {
        var dir = Path.Combine(root, Name);
        for (var n = 2; Directory.Exists(dir); n++) dir = Path.Combine(root, $"{Name}_{n}");
        return dir;
    }

    private static void WriteFile(string file, string text)
    {
        var tmp = file + ".sakshi.tmp";
        File.WriteAllText(tmp, text);
        File.Move(tmp, file, overwrite: true);
    }

    /// <summary>The profile's text with Sakshi's settings in it; everything else OBS keeps there is left alone.</summary>
    public static string Apply(string text, SakshiConfig c, HostOs os, string? userIni)
    {
        var (w, h) = Frame(c.RecordMaxWidth, c.RecordMaxHeight, c.RecordMaxWidth, c.RecordMaxHeight);
        string Num(long n) => n.ToString(CultureInfo.InvariantCulture);
        var values = new List<(string Section, string Key, string? Value)>
        {
            ("General", "Name", Name),
            ("Output", "Mode", "Advanced"),
            ("AdvOut", "RecType", "Standard"),
            ("AdvOut", "RecEncoder", Encoder(c, os)),   // its settings: recordEncoder.json
            ("AdvOut", "RecFormat2", c.RecordFormat),
            ("AdvOut", "RecFilePath", c.RecordFolder.Length > 0 ? c.RecordFolder : null),
            ("AdvOut", "RecUseRescale", "false"),
            ("Video", "FPSType", "2"),                  // a fraction: FPSNum / FPSDen
            ("Video", "FPSNum", Num(Math.Max(1, c.RecordFps))),
            ("Video", "FPSDen", "1"),
            ("Video", "BaseCX", Num(w)), ("Video", "BaseCY", Num(h)),
            ("Video", "OutputCX", Num(w)), ("Video", "OutputCY", Num(h)),
        };
        if (userIni is not null)
            foreach (var key in new[] { "SampleRate", "ChannelSetup" })
                if (IniText.Get(userIni, "Audio", key) is { } v) values.Add(("Audio", key, v));
        foreach (var (section, key, value) in values) text = IniText.Set(text, section, key, value);
        return text;
    }

    // OBS's own names for the encoders, as its simple mode names them (frontend/utility/SimpleOutput.cpp).
    private const string X264 = "obs_x264", Nvenc = "obs_nvenc_h264_tex", Amd = "h264_texture_amf",
        Qsv = "obs_qsv11_v2", Apple = "com.apple.videotoolbox.videoencoder.ave.avc";

    /// <summary>
    /// The encoder's OBS name: the config's (a short name as in OBS's simple mode, or an OBS encoder name), else
    /// Apple's own on a Mac (every Apple-silicon Mac has it) and x264 elsewhere. OBS has no fallback for an encoder
    /// it lacks (it warns and cannot record), and only x264 is in every OBS: a graphics card's encoder is the user's
    /// choice to make.
    /// </summary>
    public static string Encoder(SakshiConfig c, HostOs os) => c.RecordEncoder.Trim().ToLowerInvariant() switch
    {
        "" => os == HostOs.MacOS ? Apple : X264,
        "x264" => X264,
        "nvenc" => Nvenc,
        "amd" => Amd,
        "qsv" => Qsv,
        "apple_h264" => Apple,
        _ => c.RecordEncoder.Trim(),
    };

    /// <summary>
    /// recordEncoder.json: the quality OBS's simple mode gives a recording ("High quality, medium file size"), and a
    /// keyframe every <see cref="KeyframeSeconds"/>. An encoder Sakshi does not know gets the keyframes only.
    /// </summary>
    public static string EncoderSettings(string encoder)
    {
        var settings = new Dictionary<string, object> { ["keyint_sec"] = KeyframeSeconds };
        void Add(params (string Key, object Value)[] values) { foreach (var (k, v) in values) settings[k] = v; }
        switch (encoder)
        {
            case X264: Add(("rate_control", "CRF"), ("crf", 23), ("preset", "veryfast"), ("profile", "high")); break;
            case Nvenc or "ffmpeg_nvenc": Add(("rate_control", "CQP"), ("cqp", 23), ("profile", "high")); break;
            case Amd: Add(("rate_control", "CQP"), ("cqp", 23), ("profile", "high"), ("preset", "quality")); break;
            case Qsv: Add(("rate_control", "CQP"), ("cqp", 23), ("profile", "high")); break;
            case Apple: Add(("rate_control", "CRF"), ("quality", 50), ("profile", "high")); break;
        }
        return System.Text.Json.JsonSerializer.Serialize(settings, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// The recording's size for a window: the window's own size, scaled down to fit within the maximum, keeping its
    /// shape. The width is a multiple of 4 and the height even, as OBS rounds a canvas itself.
    /// </summary>
    public static (int Width, int Height) Frame(long width, long height, int maxWidth, int maxHeight)
    {
        (maxWidth, maxHeight) = (Math.Max(maxWidth, 16), Math.Max(maxHeight, 16));   // a nonsense setting still records
        (width, height) = (Math.Max(width, 1), Math.Max(height, 1));
        var scale = Math.Min(1.0, Math.Min(maxWidth / (double)width, maxHeight / (double)height));
        static int Round(double v, int step, int max) => Math.Clamp((int)Math.Round(v / step) * step, step, max / step * step);
        return (Round(width * scale, 4, maxWidth), Round(height * scale, 2, maxHeight));
    }
}
