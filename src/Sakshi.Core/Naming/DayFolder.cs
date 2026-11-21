using System.Globalization;
using System.Text.RegularExpressions;

namespace Sakshi.Core.Naming;

public static partial class DayFolder
{
    [GeneratedRegex(@"^([0-9]{4})-([0-9]{2})-([0-9]{2})(-[A-Z][a-z]{2})?\z")]
    private static partial Regex NewFormat();

    [GeneratedRegex(@"^([0-9]{2})-([0-9]{2})-([0-9]{4})(-[A-Z][a-z]{2})?\z")]
    private static partial Regex OldFormat();

    /// <summary>YYYY-MM-DD-Ddd with an English weekday under every culture.</summary>
    public static string For(DateTimeOffset localTime) =>
        localTime.ToString("yyyy-MM-dd-ddd", CultureInfo.InvariantCulture);

    public static bool TryParse(string folderName, out DateOnly day)
    {
        day = default;
        int y, m, d;
        if (NewFormat().Match(folderName) is { Success: true } n)
            (y, m, d) = (Int(n, 1), Int(n, 2), Int(n, 3));
        else if (OldFormat().Match(folderName) is { Success: true } o)
            (m, d, y) = (Int(o, 1), Int(o, 2), Int(o, 3));
        else
            return false;

        if (y < 1 || m is < 1 or > 12 || d < 1 || d > DateTime.DaysInMonth(y, m)) return false;
        day = new DateOnly(y, m, d);
        return true;
    }

    private static int Int(Match m, int group) => int.Parse(m.Groups[group].Value, CultureInfo.InvariantCulture);
}
