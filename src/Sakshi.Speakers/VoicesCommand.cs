namespace Sakshi.Speakers;

/// <summary>sakshi voices: list, rename and forget the people in the voice library.</summary>
public static class VoicesCommand
{
    public const string Usage = """
        usage:
          sakshi voices                    list the people the voice library knows
          sakshi voices rename OLD NEW     rename a person (meetings keep the old name until relabelled)
          sakshi voices forget NAME        remove a person and their voiceprints
        """;

    public static int Run(IReadOnlyList<string> args, string voicesFile, TextWriter stdout, TextWriter stderr)
    {
        if (args.Any(a => a is "-h" or "--help"))
        {
            stdout.WriteLine(Usage);
            return 0;
        }
        var ok = args.Count == 0 || (args[0] == "rename" && args.Count == 3 && args[2].Trim().Length > 0)
            || (args[0] == "forget" && args.Count == 2);
        if (!ok)
        {
            stderr.WriteLine(Usage);
            return 2;
        }
        try
        {
            var library = VoiceLibrary.Load(voicesFile);
            if (args.Count == 0)
            {
                if (library.People.Count == 0)
                {
                    stdout.WriteLine("No voices yet. Name a speaker with: sakshi speakers MEETING S1=NAME");
                    return 0;
                }
                var width = library.People.Max(p => p.Name.Length);
                foreach (var p in library.People)
                {
                    var meetings = p.Prints.Select(v => v.Meeting).Distinct().Count();
                    stdout.WriteLine($"{p.Name.PadRight(width)}  {Count(p.Prints.Count, "voiceprint")}  {Count(meetings, "meeting")}");
                }
                return 0;
            }
            if (args[0] == "rename")
            {
                if (library.Find(args[1]) is null)
                {
                    stderr.WriteLine($"No voice named {args[1]}.");
                    return 1;
                }
                var newName = args[2].Trim();
                library.Rename(args[1], newName);
                library.Save();
                stdout.WriteLine($"Renamed {args[1]} to {newName}");
                return 0;
            }
            var person = library.Find(args[1]);
            if (person is null)
            {
                stderr.WriteLine($"No voice named {args[1]}.");
                return 1;
            }
            library.Forget(args[1]);
            library.Save();
            stdout.WriteLine($"Forgot {person.Name} and {Count(person.Prints.Count, "voiceprint")}");
            return 0;
        }
        catch (Exception e) when (e is VoiceLibraryException or IOException or UnauthorizedAccessException)
        {
            stderr.WriteLine("Error: " + e.Message);
            return 1;
        }
    }

    private static string Count(int n, string noun) => $"{n} {noun}{(n == 1 ? "" : "s")}";
}
