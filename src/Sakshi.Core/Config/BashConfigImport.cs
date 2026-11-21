using System.Text;
using Sakshi.Core.Paths;

namespace Sakshi.Core.Config;

/// <summary>One-time import of the bash config. Handles only the syntax config/config uses.</summary>
public static class BashConfigImport
{
    private const string WhisperModel = "/usr/local/share/whisper.cpp/models/ggml-base.en.bin";

    public static SakshiConfig Import(string bashText, HostOs os, string home,
        Func<string, bool> fileExists, Action<string>? warn = null)
    {
        // bash's own default for TRANSCRIBE_PATH, used when the config doesn't set it
        var cfg = SakshiConfig.Defaults(os, home) with
        {
            HandOffPath = os == HostOs.Windows ? [] : [home + "/bin/omarchy", home + "/bin"],
        };
        var lines = bashText.Replace("\r\n", "\n").Split('\n');

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line[0] == '#') continue;

            var eq = line.IndexOf('=');
            var key = eq > 0 ? line[..eq] : "";
            if (key.Length == 0 || !key.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
            {
                warn?.Invoke($"bash config: line {i + 1} skipped: {line}");
                continue;
            }
            var rest = line[(eq + 1)..].TrimStart();

            if (rest.StartsWith('('))
            {
                var body = new StringBuilder(rest[1..]);
                while (!ArrayClosed(body.ToString()) && i + 1 < lines.Length)
                    body.Append('\n').Append(lines[++i]);
                var items = ParseArray(body.ToString(), home);
                if (items is null) { warn?.Invoke($"bash config: array {key} skipped"); continue; }
                if (key == "FRAMES_MODES") cfg = cfg with { FramesModes = items.Select(r => ParseRow(r, warn)).OfType<FrameMode>().ToList() };
                else warn?.Invoke($"bash config: unknown array {key} ignored");
                continue;
            }

            var value = ParseScalar(rest, home);
            if (value is null) { warn?.Invoke($"bash config: line {i + 1} skipped: {line}"); continue; }
            cfg = Apply(cfg, key, value, home, warn);
        }

        return cfg with { Model = fileExists(WhisperModel) ? WhisperModel : "" };
    }

    private static SakshiConfig Apply(SakshiConfig c, string key, string v, string home, Action<string>? warn)
    {
        bool Flag() => v == "1";
        int? Num()
        {
            if (int.TryParse(v, out var n)) return n;
            warn?.Invoke($"bash config: {key} is not a number: {v}");
            return null;
        }
        switch (key)
        {
            case "MEETINGS_DIR": return c with { MeetingsDir = v };
            case "OBS_SCENE": return c with { ObsScene = v };
            case "OBS_SOURCE": return c with { ObsSource = v };
            case "OBS_RESELECT_WINDOW": return c with { ObsReselectWindow = Flag() };
            case "OBS_LAUNCH_ARGS": return c with { ObsLaunchArgs = v };
            case "OBS_WS_HOST": return c with { ObsWsHost = v };
            case "OBS_WS_PORT": return Num() is { } p ? c with { ObsWsPort = p } : c;
            case "OBS_WS_PASSWORD": return c with { ObsWsPassword = v };
            case "TRANSCRIBE_ARGS": return c with { TranscribeArgs = v };
            case "TRANSCRIBE_PATH":
                return c with { HandOffPath = v.Split(':', StringSplitOptions.RemoveEmptyEntries).ToList() };
            case "TRANSCRIBE_CMD":
                warn?.Invoke("bash config: TRANSCRIBE_CMD ignored (transcription is built in)");
                return c; // replaced by the built-in transcriber
            case "FRAMES_PROMPT": return c with { FramesPrompt = Flag() };
            case "FRAMES_PROMPT_TIMEOUT": return Num() is { } a ? c with { FramesPromptTimeout = a } : c;
            case "NAME_PROMPT_TIMEOUT": return Num() is { } b ? c with { NamePromptTimeout = b } : c;
            case "KEEP_PROMPT": return c with { KeepPrompt = Flag() };
            case "KEEP_PROMPT_TIMEOUT": return Num() is { } d ? c with { KeepPromptTimeout = d } : c;
            case "KEEP_DAYS": return Num() is { } e ? c with { KeepDays = e } : c;
            case "ARCHIVE_DIR": return c with { ArchiveDir = v };
            case "REMIND_INTERVAL": return Num() is { } f ? c with { RemindInterval = f } : c;
            case "REMIND_PROMPT_TIMEOUT": return Num() is { } g ? c with { RemindPromptTimeout = g } : c;
            case "PICKER_BIN": return c with { PickerBin = v };
            case "PICKER_REAL_BIN": return c with { PickerRealBin = v };
            case "PICKER_PRIORITY_REGEX": return c with { PickerPriorityRegex = v };
            case "PICKER_DEFAULT_PAGE": return c with { PickerDefaultPage = v };
            default:
                warn?.Invoke($"bash config: unknown key {key} ignored");
                return c;
        }
    }

    private static FrameMode? ParseRow(string row, Action<string>? warn)
    {
        var f = row.Split('|', 5);
        if (f.Length < 3)
        {
            warn?.Invoke($"bash config: FRAMES_MODES row skipped: {row}");
            return null;
        }
        var handOff = f.Length == 5 && f[4].Trim().Length > 0 ? f[4].Trim() : null;
        return new FrameMode(f[0], f[1], f[2], f.Length > 3 ? f[3] : "", handOff);
    }

    // Does the text (everything after the opening paren) contain the closing paren outside quotes?
    private static bool ArrayClosed(string body)
    {
        var inQuote = false;
        for (var i = 0; i < body.Length; i++)
        {
            var ch = body[i];
            if (inQuote && ch == '\\') i++;
            else if (ch == '"') inQuote = !inQuote;
            else if (!inQuote && ch == '#') { while (i < body.Length && body[i] != '\n') i++; }
            else if (!inQuote && ch == ')') return true;
        }
        return false;
    }

    private static List<string>? ParseArray(string body, string home)
    {
        var items = new List<string>();
        var i = 0;
        while (i < body.Length)
        {
            var ch = body[i];
            if (char.IsWhiteSpace(ch)) { i++; continue; }
            if (ch == ')') return items;
            if (ch == '#') { while (i < body.Length && body[i] != '\n') i++; continue; }
            if (ch != '"') return null;
            var s = ReadQuoted(body, ref i, home);
            if (s is null) return null;
            items.Add(s);
        }
        return null;
    }

    private static string? ParseScalar(string rest, string home)
    {
        if (rest.StartsWith('"'))
        {
            var i = 0;
            var s = ReadQuoted(rest, ref i, home);
            if (s is null) return null;
            var tail = rest[i..].Trim();
            return tail.Length == 0 || tail[0] == '#' ? s : null;
        }
        var end = rest.IndexOfAny([' ', '\t', '#']);
        var raw = end < 0 ? rest : rest[..end];
        if (raw.Contains('"') || raw.Contains('\'') || raw.Contains('(')) return null;
        return Expand(raw, home);
    }

    // Reads a double-quoted string starting at body[i] == '"'; leaves i after the closing quote.
    private static string? ReadQuoted(string body, ref int i, string home)
    {
        var sb = new StringBuilder();
        i++;
        while (i < body.Length)
        {
            var ch = body[i];
            if (ch == '"') { i++; return Expand(sb.ToString(), home); }
            if (ch == '\\' && i + 1 < body.Length && body[i + 1] is '"' or '\\' or '$' or '`')
            {
                sb.Append(body[i + 1] == '$' ? '\u0001' : body[i + 1]); // an escaped $ must not expand
                i += 2;
                continue;
            }
            sb.Append(ch);
            i++;
        }
        return null;
    }

    private static string Expand(string v, string home)
    {
        v = v.Replace("${HOME}", home).Replace("$HOME", home);
        if (v == "~") v = home;
        else if (v.StartsWith("~/", StringComparison.Ordinal)) v = home + v[1..];
        return v.Replace('\u0001', '$');
    }
}
