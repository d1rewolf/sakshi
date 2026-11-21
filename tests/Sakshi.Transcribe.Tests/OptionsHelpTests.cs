namespace Sakshi.Transcribe.Tests;

public class OptionsHelpTests
{
    [Theory]
    [InlineData("-h")]
    [InlineData("--help")]
    public void Help_IsRecognised(string flag) => Assert.True(TranscribeOptions.Parse([flag]).Help);

    [Fact]
    public void Help_AsOutputValue_IsTheDirectory()
    {
        var o = TranscribeOptions.Parse(["-o", "-h", "in.mp4"]);
        Assert.False(o.Help);
        Assert.Equal("-h", o.OutputDir);
    }

    [Fact]
    public void Help_AfterDoubleDash_IsTheInput()
    {
        var o = TranscribeOptions.Parse(["--", "-h"]);
        Assert.False(o.Help);
        Assert.Equal("-h", o.Input);
    }

    [Fact]
    public void Help_StopsTheScan_ButEarlierUnknownOptionsStillFire() =>
        Assert.Throws<UsageException>(() => TranscribeOptions.Parse(["-x", "-h"]).Help);
}
