using Sakshi.Platform.MacOS;

namespace Sakshi.Platform.Tests;

public sealed class MacPathTests
{
    [Fact]
    public void Adds_homebrew_dirs_that_exist_and_are_missing() =>
        Assert.Equal("/usr/bin:/bin:/opt/homebrew/bin:/usr/local/bin",
            MacPath.WithHomebrew("/usr/bin:/bin", d => d is "/opt/homebrew/bin" or "/usr/local/bin"));

    [Fact]
    public void Keeps_a_path_that_already_has_them() =>
        Assert.Equal("/opt/homebrew/bin:/usr/bin:/usr/local/bin",
            MacPath.WithHomebrew("/opt/homebrew/bin:/usr/bin", _ => true));

    [Fact]
    public void Skips_dirs_that_do_not_exist() =>
        Assert.Equal("/usr/bin", MacPath.WithHomebrew("/usr/bin", _ => false));

    [Fact]
    public void Null_path_starts_empty() =>
        Assert.Equal("/usr/local/bin", MacPath.WithHomebrew(null, d => d == "/usr/local/bin"));
}
