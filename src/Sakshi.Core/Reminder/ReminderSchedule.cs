namespace Sakshi.Core.Reminder;

/// <summary>
/// Wall-clock reminder schedule: due at started + k * interval. After a prompt the next mark
/// advances past <c>now</c> in whole intervals, so a suspend across several marks yields one prompt.
/// </summary>
public sealed class ReminderSchedule(TimeSpan interval, DateTimeOffset started)
{
    private DateTimeOffset _next = started + interval;

    public bool IsDue(DateTimeOffset now, out string label)
    {
        label = "";
        if (interval <= TimeSpan.Zero || now < _next)
        {
            return false;
        }

        var marks = (now - started).Ticks / interval.Ticks;
        label = FormatDuration(TimeSpan.FromTicks(marks * interval.Ticks));
        _next = started + TimeSpan.FromTicks((marks + 1) * interval.Ticks);
        return true;
    }

    public static string FormatDuration(TimeSpan t)
    {
        var m = (long)(t.TotalSeconds / 60);
        var h = m / 60;
        m %= 60;
        if (h > 0 && m > 0)
        {
            return $"{h} h {m} min";
        }

        return h > 0 ? $"{h} h" : $"{m} min";
    }
}
