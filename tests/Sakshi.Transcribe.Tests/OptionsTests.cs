using System.Globalization;

namespace Sakshi.Transcribe.Tests;

public class OptionsTests
{
    private static TranscribeOptions P(params string[] a) => TranscribeOptions.Parse(a);

    [Fact]
    public void Parse_Defaults()
    {
        var o = P("m.mp4");
        Assert.Equal(new TranscribeOptions("m.mp4", null), o);
        Assert.Equal(0.2, o.Threshold);
        Assert.Equal(0.45, o.Strong);
        Assert.Equal(8, o.MinGap);
        Assert.Equal(FramesMode.Scene, o.Frames);
        Assert.Null(o.Dedupe);
        Assert.Equal(1280, o.MaxWidth);
        Assert.Equal("en", o.Language);
        Assert.Equal(0, o.MaxContext);
    }

    [Fact] public void Parse_Log() => Assert.Equal("t.log", P("--log", "t.log", "m.mp4").LogFile);
    [Fact] public void Parse_LogNeedsAValue() => Assert.Throws<UsageException>(() => P("m.mp4", "--log"));
    [Theory]
    [InlineData("t.log", "--log", "t.log", "m.mp4")]
    [InlineData("t.log", "-F", "bogus", "--log", "t.log", "m.mp4")] // found even when the rest is a usage error
    [InlineData("b.log", "--log", "a.log", "--log", "b.log", "m.mp4")] // the last one wins, as in Parse
    [InlineData("t.log", "-D", "--log", "t.log", "m.mp4")] // -D takes no value unless it is a number
    [InlineData("t.log", "--log", "t.log", "--bogus")]
    [InlineData(null, "m.mp4", "--log")]
    [InlineData(null, "--log", "", "m.mp4")]
    [InlineData(null, "-o", "--log", "m.mp4")] // the value of -o
    [InlineData(null, "--", "--log", "t.log")] // a file name after --
    [InlineData(null, "-h", "--log", "t.log")] // help stops the scan, as in Parse
    public void FindLogFile_MirrorsParse(string? expected, params string[] args) =>
        Assert.Equal(expected, TranscribeOptions.FindLogFile(args));

    [Fact] public void Parse_Output() => Assert.Equal("out", P("-o", "out", "m.mp4").OutputDir);
    [Fact] public void Parse_OutputLong() => Assert.Equal("out", P("--output", "out", "m.mp4").OutputDir);
    [Fact] public void Parse_Threshold() => Assert.Equal(0.3, P("-t", "0.3", "m.mp4").Threshold);
    [Fact] public void Parse_Strong() => Assert.Equal(0.6, P("--strong", "0.6", "m.mp4").Strong);
    [Fact] public void Parse_MinGap() => Assert.Equal(3, P("-g", "3", "m.mp4").MinGap);
    [Fact] public void Parse_Model() => Assert.Equal("x.bin", P("-m", "x.bin", "m.mp4").Model);
    [Fact] public void Parse_MaxWidth() => Assert.Equal(640, P("-w", "640", "m.mp4").MaxWidth);
    [Fact] public void Parse_Language() => Assert.Equal("auto", P("-l", "auto", "m.mp4").Language);
    [Fact] public void Parse_MarkdownOnly() => Assert.True(P("-M", "m.mp4").MarkdownOnly);

    [Theory]
    [InlineData("scene", FramesMode.Scene)]
    [InlineData("changes", FramesMode.Changes)]
    [InlineData("all", FramesMode.All)]
    [InlineData("none", FramesMode.None)]
    public void Parse_Frames(string mode, FramesMode expected) =>
        Assert.Equal(expected, P("-F", mode, "m.mp4").Frames);

    [Fact] public void Parse_DedupeWithNumber() => Assert.Equal(12, P("-D", "12", "m.mp4").Dedupe);
    [Fact] public void Parse_DedupeWithoutNumberDefaultsToEight() => Assert.Equal(8, P("-D", "m.mp4").Dedupe);
    [Fact] public void Parse_DedupeAtEnd() => Assert.Equal(8, P("m.mp4", "--dedupe").Dedupe);

    [Fact] public void Parse_MaxContext() => Assert.Equal(-1, P("-c", "-1", "m.mp4").MaxContext);

    [Fact]
    public void Parse_MaxContextNotANumber_UsesTodaysText()
    {
        var e = Assert.Throws<UsageException>(() => P("-c", "x", "m.mp4"));
        Assert.Equal(
            "Error: --max-context takes a whole number of tokens, not \"x\".\n"
            + "Nothing was transcribed. Use 0 to transcribe each audio window on its\n"
            + "own, or -1 to carry the full context.", e.Message);
    }

    [Fact]
    public void Parse_InvalidFramesMode()
    {
        var e = Assert.Throws<UsageException>(() => P("-F", "X", "m.mp4"));
        Assert.Equal("Error: invalid --frames mode: X (expected scene, changes, all, or none)", e.Message);
    }

    [Fact]
    public void Parse_ExtraArguments()
    {
        var e = Assert.Throws<UsageException>(() => P("a.mp4", "b.mp4", "c.mp4"));
        Assert.Equal("Error: unexpected extra arguments: b.mp4 c.mp4", e.Message);
    }

    [Fact]
    public void Parse_MissingInput()
    {
        var e = Assert.Throws<UsageException>(() => P("-M"));
        Assert.Equal("Error: input video path is required.", e.Message);
    }

    [Fact]
    public void Parse_UnknownOption()
    {
        var e = Assert.Throws<UsageException>(() => P("-x", "m.mp4"));
        Assert.Equal("Unknown option: -x", e.Message);
    }

    [Fact]
    public void Parse_DoubleDashTakesRestAsPositional() =>
        Assert.Equal("-weird.mp4", P("--", "-weird.mp4").Input);

    [Fact]
    public void Parse_DoesNotCheckFileExistence() =>
        Assert.Equal("/no/such/file.mp4", P("/no/such/file.mp4").Input);
}
