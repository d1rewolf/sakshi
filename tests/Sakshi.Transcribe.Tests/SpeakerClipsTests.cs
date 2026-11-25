using Sakshi.Transcribe;

namespace Sakshi.Transcribe.Tests;

public sealed class SpeakerClipsTests
{
    [Theory]
    [InlineData("S1.wav", true)]
    [InlineData("S12.wav", true)]
    [InlineData("S3-2.wav", true)]
    [InlineData("notes.txt", false)]
    [InlineData("S.wav", false)]
    [InlineData("s1.wav", false)]
    [InlineData("S1-.wav", false)]
    [InlineData("S1.wav.tmp", false)]
    [InlineData(".S1.wav.0123.tmp", false)]
    public void KnowsAClipName(string name, bool clip) => Assert.Equal(clip, SpeakerClips.IsClipName(name));
}
