using System.Text.RegularExpressions;

namespace Sakshi.Platform.Doctor;

public static partial class ObsVersion
{
    /// <summary>The oldest OBS sakshi supports.</summary>
    public const int MinimumMajor = 32;

    [GeneratedRegex(@"\d+\.\d+(\.\d+)?")]
    private static partial Regex Number();

    /// <summary>The first dotted version in <paramref name="text"/> ("OBS Studio - 32.0.2 (linux)", "33.0.1"), or null.</summary>
    public static Version? Parse(string text) =>
        Number().Match(text) is { Success: true } m && Version.TryParse(m.Value, out var v) ? v : null;
}
