using Sakshi.App;

namespace Sakshi.Core.Tests;

public sealed class ConsoleEncodingTests
{
    [Theory]
    [InlineData(true, false, false, ConsoleMode.WindowsCodePage)]
    [InlineData(true, true, false, ConsoleMode.WindowsCodePage)]
    [InlineData(true, false, true, ConsoleMode.WindowsCodePage)]
    [InlineData(true, true, true, ConsoleMode.Utf8Streams)]
    [InlineData(false, false, false, ConsoleMode.Utf8Streams)]
    [InlineData(false, true, true, ConsoleMode.Utf8Streams)]
    public void Choose_DecisionTable(bool isWindows, bool outRedirected, bool errRedirected, ConsoleMode expected) =>
        Assert.Equal(expected, ConsoleEncoding.Choose(isWindows, outRedirected, errRedirected));
}
