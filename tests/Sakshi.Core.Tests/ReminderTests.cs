using Sakshi.Core.Reminder;

namespace Sakshi.Core.Tests;

public class ReminderTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 19, 9, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Thirty = TimeSpan.FromMinutes(30);

    [Fact]
    public void FirstDueAtInterval()
    {
        var s = new ReminderSchedule(Thirty, Start);
        Assert.True(s.IsDue(Start + Thirty, out var label));
        Assert.Equal("30 min", label);
        Assert.False(s.IsDue(Start + Thirty + TimeSpan.FromMinutes(1), out _));
    }

    [Fact]
    public void NotDueBefore()
    {
        var s = new ReminderSchedule(Thirty, Start);
        Assert.False(s.IsDue(Start, out _));
        Assert.False(s.IsDue(Start + Thirty - TimeSpan.FromSeconds(1), out _));
    }

    [Fact]
    public void SuspendOverThreeMarks_OnePrompt_ThenNextMark()
    {
        var s = new ReminderSchedule(Thirty, Start);
        Assert.True(s.IsDue(Start + TimeSpan.FromMinutes(95), out var label));
        Assert.Equal("1 h 30 min", label);
        Assert.False(s.IsDue(Start + TimeSpan.FromMinutes(96), out _));
        Assert.False(s.IsDue(Start + TimeSpan.FromMinutes(119), out _));
        Assert.True(s.IsDue(Start + TimeSpan.FromMinutes(120), out label));
        Assert.Equal("2 h", label);
    }

    [Fact]
    public void FormatDuration_Cases()
    {
        Assert.Equal("0 min", ReminderSchedule.FormatDuration(TimeSpan.Zero));
        Assert.Equal("0 min", ReminderSchedule.FormatDuration(TimeSpan.FromSeconds(59)));
        Assert.Equal("30 min", ReminderSchedule.FormatDuration(TimeSpan.FromMinutes(30)));
        Assert.Equal("1 h", ReminderSchedule.FormatDuration(TimeSpan.FromMinutes(60)));
        Assert.Equal("1 h 30 min", ReminderSchedule.FormatDuration(TimeSpan.FromMinutes(90)));
        Assert.Equal("2 h 5 min", ReminderSchedule.FormatDuration(TimeSpan.FromMinutes(125)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void IntervalZero_NeverDue(int minutes)
    {
        var s = new ReminderSchedule(TimeSpan.FromMinutes(minutes), Start);
        Assert.False(s.IsDue(Start + TimeSpan.FromHours(10), out _));
    }

    [Fact]
    public void ClockGoesBackwards_NotDue()
    {
        var s = new ReminderSchedule(Thirty, Start);
        Assert.False(s.IsDue(Start - TimeSpan.FromHours(1), out _));
        Assert.True(s.IsDue(Start + Thirty, out _));
    }
}
