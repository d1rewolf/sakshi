using System.Text.Json.Serialization;
using Sakshi.Core.Paths;

namespace Sakshi.Core.Config;

public sealed record SakshiConfig
{
    public string MeetingsDir { get; init; } = "";
    public string ObsScene { get; init; } = "Scene";
    public string ObsSource { get; init; } = "Screen Capture (PipeWire)";
    public bool ObsReselectWindow { get; init; } = true;
    /// <summary>
    /// How Sakshi starts OBS: in the tray, without the questions that would stop it before its remote control opens
    /// (an update offer, missing files). OBS still asks them when the user starts it.
    /// </summary>
    public string ObsLaunchArgs { get; init; } = "--minimize-to-tray --disable-updater --disable-missing-files-check";
    public string ObsWsHost { get; init; } = "127.0.0.1";
    public int ObsWsPort { get; init; } = 4455;
    public string ObsWsPassword { get; init; } = "";
    /// <summary>
    /// OBS's remote control listens on every network interface, so it must ask for a password. When true, Sakshi turns
    /// the password on (with a new random one) if it finds it off, before it starts OBS. False leaves OBS's setting alone.
    /// </summary>
    public bool ObsWsRequirePassword { get; init; } = true;
    public string TranscribeArgs { get; init; } = "-F changes -D 8";
    public string Model { get; init; } = "";
    public IReadOnlyList<string> HandOffPath { get; init; } = [];
    public bool FramesPrompt { get; init; } = true;
    public int FramesPromptTimeout { get; init; } = 900;
    public IReadOnlyList<FrameMode> FramesModes { get; init; } =
    [
        new("\uf03e", "Changes", "-F changes -D 8", "Slides and screen shares (usual)", null),
        new("\uf03d", "Scene cuts", "-F scene", "Video that keeps moving", null),
        new("\uf044", "None", "-F none", "Transcript only, fastest", null),
        new("\uf030", "All", "-F all", "Every frame, debugging only", null),
    ];
    public int NamePromptTimeout { get; init; } = 900;
    public bool KeepPrompt { get; init; } = true;
    public int KeepPromptTimeout { get; init; } = 900;
    public int KeepDays { get; init; } = 14;
    public string ArchiveDir { get; init; } = "";
    public int RemindInterval { get; init; } = 1800;
    public int RemindPromptTimeout { get; init; } = 600;
    public string PickerBin { get; init; } = "hyprland-preview-share-picker";
    public string PickerRealBin { get; init; } = "hyprland-preview-share-picker";
    public string PickerPriorityRegex { get; init; } = @"zoom|teams|meet\.google|webex|gotomeeting";
    public string PickerDefaultPage { get; init; } = "windows";
    /// <summary>The key that starts or stops a recording, held by the tray on Windows; blank for none.</summary>
    public string Shortcut { get; init; } = "Ctrl+Alt+R";
    // OBS settings for Sakshi's own OBS profile (never the user's). See SakshiObsProfile.
    /// <summary>Frames per second: 15 is plenty for slides and faces, and half OBS's usual work.</summary>
    public int RecordFps { get; init; } = 15;
    /// <summary>The largest recording: a window is recorded at its own size, a bigger one scaled down to fit.</summary>
    public int RecordMaxWidth { get; init; } = 1920;
    public int RecordMaxHeight { get; init; } = 1080;
    /// <summary>OBS's file format; hybrid MP4 can be read while OBS is still writing it (the live transcript).</summary>
    public string RecordFormat { get; init; } = "hybrid_mp4";
    /// <summary>OBS's encoder name ("x264", "nvenc", "amd", "qsv", "apple_h264"); blank lets Sakshi choose.</summary>
    public string RecordEncoder { get; init; } = "";
    /// <summary>Where OBS writes the recording before Sakshi moves it; blank for OBS's usual folder.</summary>
    public string RecordFolder { get; init; } = "";
    /// <summary>
    /// Record sound only (no window, no picker) when a recording starts from the shortcut, a click on the bar icon or
    /// <c>sakshi toggle</c>; the menu has an item for each kind either way. The meeting keeps a .m4a, not a video.
    /// </summary>
    public bool RecordAudioOnly { get; init; }
    /// <summary>Open the live transcript window by itself when a recording starts.</summary>
    public bool LiveTranscript { get; init; }

    [JsonIgnore]
    public string EffectiveArchiveDir =>
        ArchiveDir.Length > 0 ? ArchiveDir : Path.Combine(MeetingsDir, "archives");

    public static SakshiConfig Defaults(HostOs os, string home) => new()
    {
        MeetingsDir = Path.Combine(home, "meetings"),
        // On Linux the name matches the source an Omarchy OBS setup already has; elsewhere Sakshi makes its own.
        ObsSource = os == HostOs.Linux ? "Screen Capture (PipeWire)" : "Sakshi Window Capture",
        HandOffPath = os == HostOs.Windows ? [] : [Path.Combine(home, "bin")],
    };
}
