namespace Sakshi.App.Tests;

[Collection("Environment")]
public sealed class ProgramTests
{
    [Fact]
    public void Version_prints_the_assembly_version()
    {
        var o = new StringWriter();
        var code = Program.Run(["--version"], o, new StringWriter());
        Assert.Equal(0, code);
        Assert.Equal("0.1.0", o.ToString().Trim());
    }
}
