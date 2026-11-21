using System.Globalization;
using System.Text.RegularExpressions;

namespace Sakshi.Core.Naming;

public static partial class MeetingName
{
    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"[^A-Za-z0-9._-]")]
    private static partial Regex Disallowed();

    [GeneratedRegex(@"^[._-]+")]
    private static partial Regex LeadingPunct();

    [GeneratedRegex(@"_+")]
    private static partial Regex Underscores();

    [GeneratedRegex(@"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?=\.|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DeviceName();

    /// <summary>
    /// bash sanitize_name (bin/sakshi), then rules that keep the name legal on
    /// every OS: no trailing dots, no leading [._-], no Windows device names.
    /// </summary>
    public static string Sanitize(string raw)
    {
        var n = raw.Replace('/', '-');
        n = Whitespace().Replace(n, "_");
        n = Disallowed().Replace(n, "");
        n = LeadingPunct().Replace(n, "");
        n = Underscores().Replace(n, "_");

        n = n.TrimEnd('.');
        n = LeadingPunct().Replace(n, "");
        return DeviceName().Replace(n, "$1_");
    }

    public static string DefaultFor(DateTimeOffset stoppedLocal) =>
        stoppedLocal.ToString("HH-mm", CultureInfo.InvariantCulture);

    public static string ImportDefault(string fileName, DateTimeOffset mtimeLocal)
    {
        var stem = Sanitize(Path.GetFileNameWithoutExtension(fileName));
        return stem.Length > 0 ? stem : mtimeLocal.ToString("yyyy-MM-dd_HH-mm", CultureInfo.InvariantCulture);
    }
}
