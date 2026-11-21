using System.Text.RegularExpressions;

namespace Sakshi.Core.Text;

/// <summary>Puts meeting windows first in a window list; shared by the Linux share picker and the OBS window chooser.</summary>
public static class WindowPriority
{
    /// <summary>
    /// Items whose text matches <paramref name="priorityRegex"/> (case-insensitive) first, the original order otherwise.
    /// A regex that is invalid or too slow leaves the order as it came.
    /// </summary>
    public static IReadOnlyList<T> Order<T>(IReadOnlyList<T> items, Func<T, string> text, string priorityRegex) =>
        TryOrder(items, text, priorityRegex) ?? items;

    /// <summary>Like <see cref="Order{T}"/>, but null when the regex is invalid or too slow, so a caller can leave its input alone.</summary>
    public static IReadOnlyList<T>? TryOrder<T>(IReadOnlyList<T> items, Func<T, string> text, string priorityRegex)
    {
        try
        {
            var pattern = new Regex(priorityRegex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
            var isMatch = items.Select(i => pattern.IsMatch(text(i))).ToList();
            return [.. items.Where((_, n) => isMatch[n]), .. items.Where((_, n) => !isMatch[n])];
        }
        catch (Exception e) when (e is ArgumentException or RegexMatchTimeoutException)
        {
            return null;
        }
    }
}
