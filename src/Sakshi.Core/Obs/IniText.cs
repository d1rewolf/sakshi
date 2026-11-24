namespace Sakshi.Core.Obs;

/// <summary>
/// Reads and edits OBS's INI files (a profile's basic.ini) as text: "[Section]" lines and "Key=Value" lines.
/// Everything else in the file is kept as it is.
/// </summary>
public static class IniText
{
    /// <summary>The value of <paramref name="key"/> in <paramref name="section"/>, or null when it is not there.</summary>
    public static string? Get(string text, string section, string key)
    {
        string? current = null;
        foreach (var raw in Lines(text))
        {
            var line = raw.Trim();
            if (IsSection(line, out var name)) current = name;
            else if (current == section && KeyOf(line) == key) return line[(line.IndexOf('=') + 1)..];
        }
        return null;
    }

    /// <summary>
    /// The text with <paramref name="key"/> set to <paramref name="value"/> in <paramref name="section"/>, added at
    /// the end of the section (or a new section at the end) when missing. A null value removes the key.
    /// </summary>
    public static string Set(string text, string section, string key, string? value)
    {
        var lines = Lines(text).ToList();
        int start = -1, end = lines.Count;
        for (var i = 0; i < lines.Count; i++)
        {
            if (!IsSection(lines[i].Trim(), out var name)) continue;
            if (start >= 0) { end = i; break; }
            if (name == section) start = i;
        }
        if (start < 0)
        {
            if (value is null) return text;
            if (lines.Count > 0 && lines[^1].Length > 0) lines.Add("");
            lines.Add($"[{section}]");
            lines.Add($"{key}={value}");
            return Join(lines);
        }
        for (var i = start + 1; i < end; i++)
        {
            if (KeyOf(lines[i].Trim()) != key) continue;
            if (value is null) lines.RemoveAt(i);
            else lines[i] = $"{key}={value}";
            return Join(lines);
        }
        if (value is null) return text;
        // After the section's last setting, before any blank lines that close it.
        var at = end;
        while (at - 1 > start && lines[at - 1].Trim().Length == 0) at--;
        lines.Insert(at, $"{key}={value}");
        return Join(lines);
    }

    private static IEnumerable<string> Lines(string text) =>
        text.Length == 0 ? [] : text.ReplaceLineEndings("\n").TrimEnd('\n').Split('\n');

    private static string Join(List<string> lines) => string.Join("\n", lines) + "\n";

    private static bool IsSection(string line, out string name)
    {
        var ok = line.StartsWith('[') && line.EndsWith(']');
        name = ok ? line[1..^1] : "";
        return ok;
    }

    private static string? KeyOf(string line) =>
        line.IndexOf('=') is > 0 and var eq && !line.StartsWith(';') && !line.StartsWith('#') ? line[..eq].Trim() : null;
}
