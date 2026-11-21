namespace Sakshi.Transcribe.Tests;

public sealed class FfmpegExecutableTests
{
    [Fact]
    public void The_ffmpeg_next_to_sakshi_is_used_when_it_is_there()
    {
        var expected = Path.Combine("/app", FfmpegExecutable.FileName);
        Assert.Equal(expected, FfmpegExecutable.Bundled("/app", p => p == expected));
    }

    [Fact]
    public void Without_one_next_to_sakshi_there_is_no_bundled_ffmpeg() =>
        Assert.Null(FfmpegExecutable.Bundled("/app", _ => false));

    [Fact]
    public void The_file_name_follows_the_os() =>
        Assert.Equal(OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg", FfmpegExecutable.FileName);
}
