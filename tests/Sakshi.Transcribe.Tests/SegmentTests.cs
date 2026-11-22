using Sakshi.Transcribe;

namespace Sakshi.Transcribe.Tests;

public sealed class SegmentTests
{
    [Theory]
    [InlineData("[BLANK_AUDIO]", true)]
    [InlineData(" [BLANK_AUDIO] ", true)]
    [InlineData("(door opens)", false)]
    [InlineData("We left it [BLANK_AUDIO] there", false)]
    public void Only_whispers_no_sound_marker_is_blank(string text, bool blank) => Assert.Equal(blank, Segment.IsBlank(text));
}
