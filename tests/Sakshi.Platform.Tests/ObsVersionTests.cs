using Sakshi.Platform.Doctor;

namespace Sakshi.Platform.Tests;

public sealed class ObsVersionTests
{
    [Theory]
    [InlineData("OBS Studio - 32.0.2 (linux)", "32.0.2")]
    [InlineData("33.0.1", "33.0.1")]
    [InlineData("Version: 31.1", "31.1")]
    public void Parses_the_first_dotted_version(string text, string expected) =>
        Assert.Equal(Version.Parse(expected), ObsVersion.Parse(text));

    [Theory]
    [InlineData("garbage")]
    [InlineData("")]
    public void Garbage_is_null(string text) => Assert.Null(ObsVersion.Parse(text));
}
