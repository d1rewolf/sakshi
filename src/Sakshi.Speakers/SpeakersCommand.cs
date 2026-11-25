using System.Globalization;
using Sakshi.Transcribe;

namespace Sakshi.Speakers;

/// <summary>sakshi speakers: list a meeting's voices, name them, join them, accept suggestions.</summary>
public static class SpeakersCommand
{
    public const string Usage = """
        usage:
          sakshi speakers MEETING                 list the speakers
          sakshi speakers MEETING S3=Ada ...      name speakers; the same name on two speakers joins them
          sakshi speakers MEETING S3=             take a name off (back to unknown); nothing is removed from the library
          sakshi speakers MEETING --accept        accept every suggestion
          sakshi speakers --add MEETING ...       label a meeting transcribed without speaker labels
        """;

    private sealed class Stop(int code, string message) : Exception(message) { public int Code { get; } = code; }

    /// <param name="whisper">With <paramref name="model"/> and <paramref name="step"/>: what --add needs; without
    /// them --add is not available.</param>
    public static async Task<int> RunAsync(IReadOnlyList<string> args, IReadOnlyList<string> roots, string voicesFile,
        TextWriter stdout, TextWriter stderr, DateTimeOffset now, IWhisper? whisper = null, string? model = null,
        ISpeakerStep? step = null, CancellationToken ct = default)
    {
        if (args.Count == 0)
        {
            stderr.WriteLine(Usage);
            return 2;
        }
        if (args.Any(a => a is "-h" or "--help"))
        {
            stdout.WriteLine(Usage);
            return 0;
        }
        if (args.Contains("--add"))
        {
            if (whisper is null || model is null || step is null)
            {
                stderr.WriteLine("sakshi speakers: --add is not available yet");
                return 2;
            }
            var meetings = args.Where(a => a != "--add").ToList();
            var bad = meetings.FirstOrDefault(a => a.StartsWith('-') || a.Contains('='));
            if (meetings.Count == 0 || bad is not null)
            {
                stderr.WriteLine(bad is null ? Usage : $"sakshi speakers: cannot make sense of {bad} with --add\n{Usage}");
                return 2;
            }
            return await AddSpeakersCommand.RunAsync(meetings, roots, whisper, model, step, stdout, stderr, ct);
        }
        try
        {
            var meetingArg = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal) && !a.Contains('='));
            if (meetingArg is null) throw new Stop(2, Usage);
            var rest = new List<string>(args);
            rest.Remove(meetingArg);
            var steps = new List<(string Id, string? Name)>(); // Name null: accept
            foreach (var a in rest)
            {
                if (a == "--accept") steps.Add(("", null));
                else if (a.StartsWith("--", StringComparison.Ordinal) || !a.Contains('='))
                    throw new Stop(2, $"sakshi speakers: cannot make sense of {a}\n{Usage}");
                else
                {
                    var eq = a.IndexOf('=');
                    steps.Add((a[..eq].Trim(), a[(eq + 1)..]));
                }
            }

            string dir;
            try { dir = MeetingLocator.Find(meetingArg, roots); }
            catch (UsageException e) { throw new Stop(2, e.Message); }

            var speakersPath = Path.Combine(dir, "speakers.json");
            if (!File.Exists(speakersPath))
                throw new Stop(1, $"{dir} has no speaker labels; run: sakshi speakers --add {dir}");
            SpeakersFile file;
            try { file = SpeakersFile.Read(speakersPath); }
            catch (Exception e)
            {
                var why = e is InvalidDataException && e.InnerException is not null ? e.InnerException.Message : e.Message;
                throw new Stop(1, $"Error: cannot read {speakersPath}: {why}");
            }

            if (steps.Count == 0)
            {
                List(file, dir, stdout);
                return 0;
            }

            var ids = file.Speakers.Select(s => s.Id).ToList();
            foreach (var (id, name) in steps)
                if (name is not null && !ids.Any(i => string.Equals(i, id, StringComparison.OrdinalIgnoreCase)))
                    throw new Stop(2, $"no speaker {id} in {dir} ({IdRange(ids)})");

            var transcriptPath = Path.Combine(dir, "transcript.json");
            IReadOnlyList<Segment> segments;
            string language;
            try { (segments, language) = TranscriptFiles.ReadJson(transcriptPath); }
            catch (Exception e) { throw new Stop(1, $"Error: cannot read {transcriptPath}: {e.Message}"); }

            return Apply(dir, file, segments, language, steps, voicesFile, stdout, now);
        }
        catch (Stop s)
        {
            stderr.WriteLine(s.Message);
            return s.Code;
        }
    }

    private static int Apply(string dir, SpeakersFile file, IReadOnlyList<Segment> segments, string language,
        List<(string Id, string? Name)> steps, string voicesFile, TextWriter stdout, DateTimeOffset now)
    {
        var parts = Path.GetFullPath(dir).TrimEnd('/', '\\').Split('/', '\\');
        var meeting = string.Join("/", parts.Skip(Math.Max(0, parts.Length - 2)));
        var entries = file.Speakers.ToList();
        var segs = segments.ToList();
        var lines = new List<string>();
        VoiceLibrary? library = null;
        var taught = false;
        var joinedAway = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var clips = new List<string>();

        void Name(string id, string text)
        {
            if (joinedAway.TryGetValue(id, out var into))
                throw new Stop(2, $"{id.ToUpperInvariant()} was joined into {into} earlier in this command");
            var i = entries.FindIndex(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));
            if (i < 0) throw new Stop(2, $"no speaker {id} in {dir} ({IdRange(entries.Select(e => e.Id).ToList())})");
            var e = entries[i];
            text = text.Trim();
            if (text.Length == 0)
            {
                entries[i] = e with { Status = SpeakerStatus.Unknown, Name = null, PersonId = null, Suggestion = null, Score = null };
                lines.Add($"{e.Id} is now unknown");
                return;
            }
            try { library ??= VoiceLibrary.Load(voicesFile); }
            catch (VoiceLibraryException ex) { throw new Stop(1, "Error: " + ex.Message); }
            Person person;
            try { person = library.Teach(text, e.Voiceprint, meeting, e.Id, now); }
            catch (VoiceLibraryException ex) { throw new Stop(1, "Error: " + ex.Message); }
            taught = true;
            var named = e with
            {
                Status = SpeakerStatus.Named, Name = person.Name, PersonId = person.Id, Suggestion = null, Score = null,
            };
            var j = entries.FindIndex(o => o.Id != e.Id && o.Status == SpeakerStatus.Named
                && (o.PersonId is not null ? o.PersonId == person.Id
                    : string.Equals(o.Name, person.Name, StringComparison.OrdinalIgnoreCase)));
            if (j < 0)
            {
                entries[i] = named;
                lines.Add($"{e.Id} is now {person.Name}");
                return;
            }
            var other = entries[j];
            var (keep, drop) = IdNumber(e.Id) <= IdNumber(other.Id) ? (named, other) : (other, named);
            var seconds = keep.Seconds + drop.Seconds;
            var print = keep.Voiceprint;
            if (keep.Voiceprint.Length == drop.Voiceprint.Length)
            {
                var wk = seconds > 0 ? keep.Seconds / seconds : 0.5;
                print = keep.Voiceprint.Select((v, k) => (float)(v * wk + drop.Voiceprint[k] * (1 - wk))).ToArray();
            }
            var merged = keep with { Name = person.Name, Seconds = seconds, Voiceprint = print };
            entries.RemoveAll(o => o.Id == keep.Id || o.Id == drop.Id);
            entries.Add(merged);
            entries = entries.OrderBy(o => IdNumber(o.Id)).ToList();
            for (var k = 0; k < segs.Count; k++)
                if (segs[k].Speaker == drop.Id) segs[k] = segs[k] with { Speaker = keep.Id };
            joinedAway[drop.Id] = keep.Id;
            clips.Add(Path.Combine(dir, drop.Clip));
            lines.Add($"{drop.Id} joined {keep.Id} ({person.Name})");
        }

        foreach (var (id, text) in steps)
        {
            if (text is not null) { Name(id, text); continue; }
            foreach (var s in entries.Where(e => e.Status == SpeakerStatus.Suggested && e.Suggestion is not null).ToList())
                if (entries.Any(o => o.Id == s.Id)) Name(s.Id, s.Suggestion!);
        }

        if (lines.Count == 0)
        {
            stdout.WriteLine("No suggestions to accept.");
            return 0;
        }

        if (taught)
            try { library!.Save(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or VoiceLibraryException)
            {
                throw new Stop(1, $"Error: cannot write the voice library {voicesFile}: {e.Message}");
            }
        try { MeetingRewriter.Write(dir, file with { Speakers = entries }, segs, language); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new Stop(1, $"Error: cannot write the meeting files in {dir}: {e.Message}");
        }
        foreach (var clip in clips)
            try { File.Delete(clip); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        foreach (var l in lines) stdout.WriteLine(l);
        return 0;
    }

    private static void List(SpeakersFile file, string dir, TextWriter stdout)
    {
        var rows = file.Speakers.Select(s => (s, Who: s.Status switch
        {
            SpeakerStatus.Named => s.Name ?? "",
            SpeakerStatus.Suggested => $"{s.Suggestion}? ({(s.Score ?? 0).ToString("0.00", CultureInfo.InvariantCulture)})",
            _ => "unknown",
        }, Time: Duration(s.Seconds))).ToList();
        var nameWidth = rows.Select(r => r.Who.Length).DefaultIfEmpty(0).Max();
        var timeWidth = rows.Select(r => r.Time.Length).DefaultIfEmpty(0).Max();
        foreach (var (s, who, time) in rows)
        {
            stdout.WriteLine($"{s.Id}  {who.PadRight(nameWidth)} {time.PadLeft(timeWidth)}  \"{s.Sample}\"");
            stdout.WriteLine("    " + Path.GetFullPath(Path.Combine(dir, s.Clip)));
        }
    }

    private static string Duration(double seconds)
    {
        var t = (long)Math.Round(seconds);
        return t >= 3600
            ? string.Create(CultureInfo.InvariantCulture, $"{t / 3600}:{t / 60 % 60:00}:{t % 60:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{t / 60}:{t % 60:00}");
    }

    private static int IdNumber(string id) =>
        int.TryParse(id.TrimStart('S', 's'), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : int.MaxValue;

    private static string IdRange(List<string> ids)
    {
        var sorted = ids.OrderBy(IdNumber).ToList();
        if (sorted.Count == 0) return "none";
        if (sorted.Count == 1) return sorted[0];
        var contiguous = sorted.Select(IdNumber).Zip(sorted.Skip(1).Select(IdNumber), (a, b) => b == a + 1).All(x => x);
        return contiguous ? $"{sorted[0]}–{sorted[^1]}" : string.Join(", ", sorted);
    }
}
