namespace Sakshi.App.Tray;

/// <summary>
/// A keyboard shortcut from the config, such as "Ctrl+Alt+R", as Windows' RegisterHotKey takes it: modifier flags
/// and a virtual-key code. Keys are a letter, a digit or F1–F24; Ctrl, Alt or Win is required, so the shortcut never
/// takes a key away from typing.
/// </summary>
internal sealed record Hotkey(uint Modifiers, uint Key, string Text)
{
    // MOD_ALT, MOD_CONTROL, MOD_SHIFT, MOD_WIN
    public const uint Alt = 0x1, Ctrl = 0x2, Shift = 0x4, Win = 0x8;

    /// <summary>True when the setting is blank: the user wants no shortcut.</summary>
    public static bool IsOff(string text) => string.IsNullOrWhiteSpace(text);

    /// <summary>The shortcut, or null when the text is not one Sakshi can register.</summary>
    public static Hotkey? Parse(string text)
    {
        uint modifiers = 0, key = 0;
        string? keyName = null;
        foreach (var raw in text.Split('+'))
        {
            var part = raw.Trim();
            uint? modifier = part.ToLowerInvariant() switch
            {
                "ctrl" or "control" => Ctrl,
                "alt" => Alt,
                "shift" => Shift,
                "win" or "windows" => Win,
                _ => null,
            };
            if (modifier is { } m) modifiers |= m;
            else if (keyName is null && KeyCode(part) is { } code) (key, keyName) = (code, part.ToUpperInvariant());
            else return null;
        }
        if (keyName is null || (modifiers & (Ctrl | Alt | Win)) == 0) return null;
        return new Hotkey(modifiers, key, Name(modifiers, keyName));
    }

    // Virtual-key codes: letters and digits are their ASCII capitals; F1 is 0x70.
    private static uint? KeyCode(string part)
    {
        if (part.Length == 1 && char.IsAsciiLetterOrDigit(part[0])) return char.ToUpperInvariant(part[0]);
        if (part.Length is 2 or 3 && part[0] is 'F' or 'f' && int.TryParse(part.AsSpan(1), out var n) && n is >= 1 and <= 24
            && part[1] != '0')
            return (uint)(0x70 + n - 1);
        return null;
    }

    private static string Name(uint modifiers, string key)
    {
        var parts = new List<string>();
        if ((modifiers & Ctrl) != 0) parts.Add("Ctrl");
        if ((modifiers & Win) != 0) parts.Add("Win");
        if ((modifiers & Alt) != 0) parts.Add("Alt");
        if ((modifiers & Shift) != 0) parts.Add("Shift");
        parts.Add(key);
        return string.Join("+", parts);
    }
}
