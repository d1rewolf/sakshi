using System.Globalization;

namespace Sakshi.Transcribe.Tests;

public class FrameTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sakshi-frames-" + Guid.NewGuid().ToString("N"));

    public FrameTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, true);

    private void Touch(string name) => File.WriteAllText(Path.Combine(_dir, name), name);
    private string[] Names() => Directory.GetFiles(_dir).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray()!;

    private static TranscribeOptions Opts(FramesMode m) => new("m.mp4", null, Frames: m);

    [Fact]
    public void VideoFilter_Scene() =>
        Assert.Equal(
            "select='isnan(prev_selected_t)+gt(scene\\,0.45)+gt(scene\\,0.2)*gte(t-prev_selected_t\\,8)',"
            + "scale=w='min(1280\\,iw)':h=-2,metadata=print:file=/tmp/m.txt",
            FrameFilters.VideoFilter(Opts(FramesMode.Scene), "/tmp/m.txt"));

    [Fact]
    public void VideoFilter_Scene_UsesCustomValues() =>
        Assert.Equal(
            "select='isnan(prev_selected_t)+gt(scene\\,0.6)+gt(scene\\,0.3)*gte(t-prev_selected_t\\,3)',"
            + "scale=w='min(640\\,iw)':h=-2,metadata=print:file=f",
            FrameFilters.VideoFilter(
                new TranscribeOptions("m.mp4", null, Threshold: 0.3, Strong: 0.6, MinGap: 3, MaxWidth: 640), "f"));

    [Fact]
    public void VideoFilter_Changes() =>
        Assert.Equal(
            "freezedetect=n=-50dB:d=1,metadata=mode=select:key=lavfi.freezedetect.freeze_start,metadata=mode=add:key=keep:value=1,scale=w='min(1280\\,iw)':h=-2,metadata=print:file=f",
            FrameFilters.VideoFilter(Opts(FramesMode.Changes), "f"));

    [Fact]
    public void VideoFilter_All() =>
        Assert.Equal(
            "metadata=mode=add:key=keep:value=1,scale=w='min(1280\\,iw)':h=-2,metadata=print:file=f",
            FrameFilters.VideoFilter(Opts(FramesMode.All), "f"));

    [Fact]
    public void VideoFilter_IsCultureInvariant()
    {
        var old = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var f = FrameFilters.VideoFilter(Opts(FramesMode.Scene), "f");
            Assert.Contains("gt(scene\\,0.45)", f);
            Assert.Contains("gt(scene\\,0.2)", f);
            Assert.DoesNotContain("0,45", f);
        }
        finally { CultureInfo.CurrentCulture = old; }
    }

    [Fact]
    public void ParseMetadata_ReadsPtsTimes()
    {
        const string text =
            "frame:0    pts:0       pts_time:0\n"
            + "lavfi.keep=1\n"
            + "frame:1    pts:15360   pts_time:5.12\n"
            + "lavfi.scene_score=0.5\n"
            + "frame:2    pts:92160   pts_time:3601.5\n";
        Assert.Equal(new[] { 0.0, 5.12, 3601.5 }, FrameFiles.ParseMetadata(text));
    }

    [Fact]
    public void ParseMetadata_IsCultureInvariant()
    {
        var old = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal(new[] { 5.12 }, FrameFiles.ParseMetadata("pts_time:5.12"));
        }
        finally { CultureInfo.CurrentCulture = old; }
    }

    [Fact]
    public void RenameRaw_NamesBySecondAndSuffixesClashes()
    {
        Touch("raw_0001.jpg"); Touch("raw_0002.jpg"); Touch("raw_0003.jpg"); Touch("raw_0004.jpg");
        var recs = FrameFiles.RenameRaw(_dir, [5.1, 5.9, 5.95, 3725.0]);
        Assert.Equal(
            new[] { "frame_00-00-05.jpg", "frame_00-00-05_1.jpg", "frame_00-00-05_2.jpg", "frame_01-02-05.jpg" },
            recs.Select(r => r.FileName));
        Assert.Equal(new[] { 5.1, 5.9, 5.95, 3725.0 }, recs.Select(r => r.Seconds));
        Assert.Equal(
            new[] { "frame_00-00-05.jpg", "frame_00-00-05_1.jpg", "frame_00-00-05_2.jpg", "frame_01-02-05.jpg" },
            Names());
    }

    [Fact]
    public void RenameRaw_SortsRecordsByTime()
    {
        Touch("raw_0001.jpg"); Touch("raw_0002.jpg");
        var recs = FrameFiles.RenameRaw(_dir, [9.0, 2.0]);
        Assert.Equal(new[] { "frame_00-00-02.jpg", "frame_00-00-09.jpg" }, recs.Select(r => r.FileName));
    }

    [Fact]
    public void RenameRaw_ExtraRawFilesDeleted()
    {
        Touch("raw_0001.jpg"); Touch("raw_0002.jpg"); Touch("raw_0003.jpg");
        var recs = FrameFiles.RenameRaw(_dir, [1.0]);
        Assert.Single(recs);
        Assert.Equal(new[] { "frame_00-00-01.jpg" }, Names());
    }

    [Fact]
    public void RenameRaw_MissingDirectory_ReturnsEmpty() =>
        Assert.Empty(FrameFiles.RenameRaw(Path.Combine(_dir, "nope"), [1.0]));

    private async Task<string> ClipAsync(string source)
    {
        var clip = Path.Combine(_dir, "clip.mp4");
        var gen = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ffmpeg")
        {
            ArgumentList = { "-y", "-loglevel", "error", "-f", "lavfi", "-i", source, clip },
            RedirectStandardError = true,
        })!;
        await gen.WaitForExitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, gen.ExitCode);
        return clip;
    }

    private async Task<IReadOnlyList<FrameRecord>> ChangesFramesAsync(string clip)
    {
        var frames = Path.Combine(_dir, "frames");
        Directory.CreateDirectory(frames);
        // Like TranscribeCommand: the metadata path inside -vf is relative and ffmpeg runs from the
        // output dir, because a drive-letter path (C:\...) breaks ffmpeg's filter parser on Windows.
        var meta = Path.Combine(frames, ".metadata.txt");
        File.WriteAllText(meta, "");
        var vf = FrameFilters.VideoFilter(Opts(FramesMode.Changes), "frames/.metadata.txt");
        await Ffmpeg.ExtractFramesAsync(clip, frames, vf, TestContext.Current.CancellationToken, workingDirectory: _dir);
        var recs = FrameFiles.RenameRaw(frames, FrameFiles.ParseMetadata(File.ReadAllText(meta)));
        Assert.All(recs, r => Assert.True(File.Exists(Path.Combine(frames, r.FileName))));
        return recs;
    }

    [Fact]
    public async Task ExtractFrames_RealFfmpeg_ChangesKeepsAPictureThatHoldsStill()
    {
        Assert.SkipUnless(OnPath("ffmpeg"), "ffmpeg is not on PATH");
        var clip = await ClipAsync("color=c=blue:duration=3:size=320x240:rate=10");
        Assert.Single(await ChangesFramesAsync(clip));
    }

    [Fact]
    public async Task ExtractFrames_RealFfmpeg_ChangesWithAPictureThatNeverHoldsStillGivesNoFramesAndNoError()
    {
        Assert.SkipUnless(OnPath("ffmpeg"), "ffmpeg is not on PATH");
        var clip = await ClipAsync("testsrc=duration=3:size=320x240:rate=10");
        Assert.Empty(await ChangesFramesAsync(clip));
    }

    [Fact]
    public async Task ExtractFrames_BadInput_ThrowsWithStderr()
    {
        Assert.SkipUnless(OnPath("ffmpeg"), "ffmpeg is not on PATH");
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Ffmpeg.ExtractFramesAsync(Path.Combine(_dir, "missing.mp4"), _dir, "null", TestContext.Current.CancellationToken));
        Assert.Contains("missing.mp4", e.Message);
    }

    internal static bool OnPath(string exe)
    {
        var names = OperatingSystem.IsWindows() ? new[] { exe + ".exe", exe } : new[] { exe };
        return (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(d => names.Any(n => File.Exists(Path.Combine(d, n))));
    }
}
