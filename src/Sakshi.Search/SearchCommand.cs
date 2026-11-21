namespace Sakshi.Search;

/// <summary>`find` and `index` commands.</summary>
public static class SearchCommand
{
    private const string Prog = "sakshi";

    private static int Usage(TextWriter stderr, string? command, string message)
    {
        stderr.Write(UsageLine(command) + "\n");
        stderr.Write($"{Prog}: {message}\n");
        return 2;
    }

    private static string UsageLine(string? command) =>
        command switch
        {
            "find" => $"usage: {Prog} find [-h] [--all] WORD [WORD ...]",
            "index" => $"usage: {Prog} index [-h] [--rebuild]",
            _ => $"usage: {Prog} [-h] {{find,index}} ...",
        };

    public static int Run(string[] args, IReadOnlyList<string> roots, string indexPath,
        TextWriter stdout, TextWriter stderr, bool isTty)
    {
        if (args.Length == 0)
            return Usage(stderr, null, "the following arguments are required: command");
        if (args.Contains("-h") || args.Contains("--help"))
        {
            stdout.Write(UsageLine(args[0] is "find" or "index" ? args[0] : null) + "\n");
            return 0;
        }
        var command = args[0];
        if (command is not ("find" or "index"))
            return Usage(stderr, null, $"argument command: invalid choice: '{command}' (choose from 'find', 'index')");

        var all = false;
        var rebuild = false;
        var words = new List<string>();
        var unknown = new List<string>();
        var onlyWords = false;
        foreach (var a in args.Skip(1))
        {
            if (onlyWords)
                words.Add(a);
            else if (a == "--")
                onlyWords = true;
            else if (command == "find" && a == "--all")
                all = true;
            else if (command == "index" && a == "--rebuild")
                rebuild = true;
            else if (a.Length > 1 && a[0] == '-')
                unknown.Add(a);
            else
                words.Add(a);
        }
        if (command == "find" && words.Count == 0 && unknown.Count == 0)
            return Usage(stderr, command, "the following arguments are required: WORD");
        if (command == "index")
            unknown.AddRange(words);
        if (unknown.Count > 0)
            return Usage(stderr, command, $"unrecognized arguments: {string.Join(" ", unknown)}");

        if (rebuild)
            foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
            {
                try { File.Delete(indexPath + suffix); }
                catch (DirectoryNotFoundException) { } // no index directory yet
            }

        using var index = SearchIndex.Open(indexPath);
        var stats = index.Refresh(roots, m => stderr.Write($"{Prog}: {m}\n"));
        if (command == "index")
        {
            stdout.Write($"Indexed {index.MeetingCount()} meetings ({stats.Added} new, {stats.Updated} changed, "
                + $"{stats.Removed} gone, {stats.Skipped} skipped).\n");
            return 0;
        }
        var (results, nearUsed) = Finder.Search(index, words);
        var text = ResultFormatter.Format(results, nearUsed, words, all, color: isTty);
        stdout.Write(text.TrimEnd('\n') + "\n");
        return results.Count > 0 ? 0 : 1;
    }
}
