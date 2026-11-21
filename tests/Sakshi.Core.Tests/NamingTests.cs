using System.Globalization;
using Sakshi.Core.Naming;

namespace Sakshi.Core.Tests;

[Collection("Environment")]
public class NamingTests
{
    private static string ForWithCulture(string culture, DateTimeOffset t)
    {
        var saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(culture);
            return DayFolder.For(t);
        }
        finally { CultureInfo.CurrentCulture = saved; }
    }

    [Theory]
    [InlineData("de-DE")]
    [InlineData("ja-JP")]
    [InlineData("en-US")]
    public void DayFolder_For_UsesEnglishWeekday(string culture)
    {
        var t = new DateTimeOffset(2026, 10, 28, 10, 0, 0, TimeSpan.FromHours(2));
        Assert.Equal("2026-10-28-Wed", ForWithCulture(culture, t));
    }

    [Theory]
    [InlineData("2026-03-05", 2026, 3, 5)]
    [InlineData("2026-03-05-Thu", 2026, 3, 5)]
    [InlineData("03-06-2026", 2026, 3, 6)]
    [InlineData("03-05-2026-Thu", 2026, 3, 5)]
    public void DayFolder_TryParse_AcceptsNewOldAndSuffixed(string name, int y, int m, int d)
    {
        Assert.True(DayFolder.TryParse(name, out var day));
        Assert.Equal(new DateOnly(y, m, d), day);
    }

    [Theory]
    [InlineData("Flat_A")]
    [InlineData("2026-13-40")]
    [InlineData("2026-03-05-thu")]
    [InlineData("")]
    [InlineData("٢٠٢٦-٠٣-٠٥")]
    [InlineData("2026-03-05\n")]
    [InlineData("03-05-2026\n")]
    public void DayFolder_TryParse_RejectsOthers(string name)
    {
        Assert.False(DayFolder.TryParse(name, out _));
    }

    [Theory]
    [InlineData("Weekly sync", "Weekly_sync")]
    [InlineData("a/b", "a-b")]
    [InlineData("  ..x  y", "x_y")]
    [InlineData("a   b", "a_b")]
    [InlineData("Café", "Caf")]
    public void Sanitize_MatchesBash(string raw, string expected)
    {
        Assert.Equal(expected, MeetingName.Sanitize(raw));
    }

    [Theory]
    [InlineData("con", "con_")]
    [InlineData("LPT1", "LPT1_")]
    [InlineData("Q3 review.", "Q3_review")]
    [InlineData("日本", "")]
    [InlineData("nul.txt", "nul_.txt")]
    [InlineData("console", "console")]
    [InlineData("CON.", "CON_")]
    [InlineData("._.", "")]
    [InlineData("Com1.txt", "Com1_.txt")]
    public void Sanitize_SafeOnEveryOs(string raw, string expected)
    {
        Assert.Equal(expected, MeetingName.Sanitize(raw));
    }

    [Fact]
    public void DefaultFor_IsHourMinute()
    {
        Assert.Equal("09-05", MeetingName.DefaultFor(new DateTimeOffset(2026, 1, 2, 9, 5, 59, TimeSpan.Zero)));
    }

    [Fact]
    public void ImportDefault_UsesSanitizedStemElseMtime()
    {
        var mtime = new DateTimeOffset(2026, 2, 3, 13, 6, 0, TimeSpan.Zero);
        Assert.Equal("2026-09-23_13-06-16", MeetingName.ImportDefault("2026-09-23 13-06-16.mp4", mtime));
        Assert.Equal("2026-02-03_13-06", MeetingName.ImportDefault("日本.mp4", mtime));
    }
}
