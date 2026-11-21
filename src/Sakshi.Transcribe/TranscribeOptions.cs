using System.Globalization;

namespace Sakshi.Transcribe;

public enum FramesMode { Scene, Changes, All, None }

public sealed record TranscribeOptions(
    string Input,
    string? OutputDir,
    double Threshold = 0.2,
    double Strong = 0.45,
    int MinGap = 8,
    FramesMode Frames = FramesMode.Scene,
    int? Dedupe = null,
    string? Model = null,
    int MaxWidth = 1280,
    string Language = "en",
    int MaxContext = 0,
    bool MarkdownOnly = false,
    bool Help = false,
    string? LogFile = null)
{
    /// <summary>
    /// Parses transcribe-meeting's command line. Does not check that files exist. -h/--help is honoured
    /// where the sequential scan reaches it, as in bash: the result has Help set and nothing else is read.
    /// </summary>
    public static TranscribeOptions Parse(IReadOnlyList<string> args)
    {
        string? output = null, model = null, logFile = null;
        double threshold = 0.2, strong = 0.45;
        int minGap = 8, maxWidth = 1280;
        string framesArg = "scene", language = "en", maxContextArg = "0";
        int? dedupe = null;
        bool mdOnly = false;
        var positional = new List<string>();

        string Value(ref int i)
        {
            if (i + 1 >= args.Count || args[i + 1].Length == 0)
                throw new UsageException($"Error: {args[i]} requires a value.");
            return args[++i];
        }

        double Number(string flag, string text) =>
            double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            && double.IsFinite(d)
                ? d
                : throw new UsageException($"Error: {flag} takes a number, not \"{text}\".");

        int Whole(string flag, string text) =>
            int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
                ? n
                : throw new UsageException($"Error: {flag} takes a whole number, not \"{text}\".");

        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            switch (a)
            {
                case "-o" or "--output": output = Value(ref i); break;
                case "-t" or "--threshold": threshold = Number(a, Value(ref i)); break;
                case "-T" or "--strong": strong = Number(a, Value(ref i)); break;
                case "-g" or "--min-gap": minGap = Whole(a, Value(ref i)); break;
                case "-F" or "--frames": framesArg = Value(ref i); break;
                case "-D" or "--dedupe":
                    if (i + 1 < args.Count && IsDigits(args[i + 1]))
                        dedupe = int.Parse(args[++i], CultureInfo.InvariantCulture);
                    else
                        dedupe = 8;
                    break;
                case "-m" or "--model": model = Value(ref i); break;
                case "-w" or "--max-width": maxWidth = Whole(a, Value(ref i)); break;
                case "-l" or "--language": language = Value(ref i); break;
                case "-c" or "--max-context": maxContextArg = Value(ref i); break;
                case "--log": logFile = Value(ref i); break;
                case "-M" or "--markdown-only": mdOnly = true; break;
                case "-h" or "--help": return new TranscribeOptions("", null, Help: true);
                case "--":
                    for (i++; i < args.Count; i++) positional.Add(args[i]);
                    break;
                default:
                    if (a.StartsWith('-')) throw new UsageException($"Unknown option: {a}");
                    positional.Add(a);
                    break;
            }
        }

        FramesMode frames = framesArg switch
        {
            "scene" => FramesMode.Scene,
            "changes" => FramesMode.Changes,
            "all" => FramesMode.All,
            "none" => FramesMode.None,
            _ => throw new UsageException(
                $"Error: invalid --frames mode: {framesArg} (expected scene, changes, all, or none)"),
        };

        if (!int.TryParse(maxContextArg, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var maxContext))
            throw new UsageException(
                $"Error: --max-context takes a whole number of tokens, not \"{maxContextArg}\".\n"
                + "Nothing was transcribed. Use 0 to transcribe each audio window on its\n"
                + "own, or -1 to carry the full context.");

        if (positional.Count > 1)
            throw new UsageException(
                $"Error: unexpected extra arguments: {string.Join(' ', positional.Skip(1))}");
        if (positional.Count == 0 || positional[0].Length == 0)
            throw new UsageException("Error: input video path is required.");

        return new TranscribeOptions(positional[0], output, threshold, strong, minGap, frames,
            dedupe, model, maxWidth, language, maxContext, mdOnly, LogFile: logFile);
    }

    private static readonly HashSet<string> TakesValue =
    [
        "-o", "--output", "-t", "--threshold", "-T", "--strong", "-g", "--min-gap", "-F", "--frames",
        "-m", "--model", "-w", "--max-width", "-l", "--language", "-c", "--max-context",
    ];

    /// <summary>
    /// The --log file, found the way <see cref="Parse"/> would find it but without failing on the
    /// rest, so a usage error can be written to the log too. Null when there is none, or when
    /// Parse would stop first (-h, --) or reject the --log itself (no value).
    /// </summary>
    public static string? FindLogFile(IReadOnlyList<string> args)
    {
        string? log = null;
        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            if (a is "-h" or "--help") return null;
            if (a == "--") break;
            if (a == "--log")
            {
                if (i + 1 >= args.Count || args[i + 1].Length == 0) return null;
                log = args[++i];
            }
            else if (TakesValue.Contains(a)) i++;
            else if (a is "-D" or "--dedupe" && i + 1 < args.Count && IsDigits(args[i + 1])) i++;
        }
        return log;
    }

    private static bool IsDigits(string s) => s.Length > 0 && s.All(c => c is >= '0' and <= '9');
}
