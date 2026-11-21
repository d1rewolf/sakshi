namespace Sakshi.Core.HandOff;

/// <summary>
/// Builds the cmd.exe argument string for running a .cmd/.bat. Every token is
/// double-quoted and the whole line wrapped for /s, so spaces and '&amp;' are
/// safe. Characters that cmd would still interpret inside quotes are refused.
/// </summary>
internal static class CmdExeCommandLine
{
    private static readonly char[] Refused = ['"', '%', '^', '\r', '\n'];

    /// <returns>The arguments for cmd.exe, or null with <paramref name="refusal"/> set.</returns>
    public static string? Build(string program, IEnumerable<string> args, out string? refusal)
    {
        var tokens = args.Prepend(program).ToList();
        foreach (var t in tokens)
        {
            var i = t.IndexOfAny(Refused);
            if (i >= 0)
            {
                var c = t[i];
                var shown = c switch { '\r' => "carriage return", '\n' => "line feed", _ => $"'{c}'" };
                refusal = $"refusing to run through cmd.exe: {shown} in \"{t.Replace("\r", "").Replace("\n", " ")}\"";
                return null;
            }
        }
        refusal = null;
        return "/d /s /c \"" + string.Join(' ', tokens.Select(t => "\"" + t + "\"")) + "\"";
    }
}
