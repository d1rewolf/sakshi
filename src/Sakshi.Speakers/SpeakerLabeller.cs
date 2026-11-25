using System.Text.RegularExpressions;
using Sakshi.Transcribe;

namespace Sakshi.Speakers;

/// <summary>
/// Labels a meeting's segments with speakers (docs/design/speakers.md, "How it works"): diarize, a voiceprint per
/// piece, join pieces that sound alike, suggest names from the voice library, then words to speakers and segments
/// split where the speaker changes. Writes speakers.json and speakers/Sn.wav into the meeting folder.
/// </summary>
public sealed partial class SpeakerLabeller(IDiarizer diarizer, IVoiceprinter printer, VoiceLibrary library, bool libraryWritable)
{
    private const int Rate = 16000;

    private sealed record Speaker(string Id, float[] Raw, Suggestion? Suggestion);

    public SpeakerOutcome Label(float[] samples, string outputDir, IReadOnlyList<Segment> segments,
        string source, IReadOnlyList<FrameRecord>? frames, TextWriter stdout, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (samples.Length == 0 || !segments.Any(s => s.Words.Count > 0)) return NoLabels(segments, outputDir);

        var turns = diarizer.Diarize(samples, ct)
            .Where(t => t.End > t.Start).OrderBy(t => t.Start).ThenBy(t => t.End).ToList();
        if (turns.Count == 0) return NoLabels(segments, outputDir);
        ct.ThrowIfCancellationRequested();

        var prints = PiecePrints(samples, turns, ct);
        if (prints.Count == 0) return NoLabels(segments, outputDir);
        ct.ThrowIfCancellationRequested();

        var mean = BackgroundMean();
        var (groupTurns, groups) = JoinPieces(turns, prints, mean);
        var labelled = WordsToSpeakers(segments, groupTurns);
        if (!Number(ref labelled, groupTurns, groups, out var speakerTurns, out var speakers))
            return NoLabels(segments, outputDir);
        ct.ThrowIfCancellationRequested();

        var suggestions = Suggest(speakers, mean);
        speakers = speakers.Select((s, i) => s with { Suggestion = suggestions[i] }).ToList();
        var entries = speakers.Select(s => Entry(s, speakerTurns, labelled)).ToList();
        var labels = entries.ToDictionary(e => e.Id, e => e.Label);
        labelled = labelled.Select(s => s with { SpeakerLabel = labels[s.Speaker!] }).ToList();
        ct.ThrowIfCancellationRequested();

        WriteClips(samples, outputDir, speakerTurns, entries);
        new SpeakersFile(1, printer.ModelName, source, frames, entries).Write(Path.Combine(outputDir, "speakers.json"));

        if (libraryWritable)
        {
            // Labelling takes minutes; the library may have changed on disk meanwhile (a name taught, a person
            // forgotten). Only the background update is applied, to the library as it is now.
            VoiceLibrary? current = null;
            try { current = library.Reload(); }
            catch (VoiceLibraryException e) { stdout.WriteLine($"Warning: {e.Message}; voices are not saved this time"); }
            if (current is not null)
            {
                current.AddToBackground(prints.Values);
                try { current.Save(); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    stdout.WriteLine($"Warning: voices not saved: {e.Message}");
                }
            }
        }

        return new SpeakerOutcome(labelled, entries.Count,
            entries.Count(e => e.Status == SpeakerStatus.Suggested), entries.Count(e => e.Status == SpeakerStatus.Unknown));
    }

    /// <summary>The input unchanged. An earlier run's speakers.json and clips are removed, so nothing relabels
    /// the meeting later from speakers that no longer match its transcript.</summary>
    private static SpeakerOutcome NoLabels(IReadOnlyList<Segment> segments, string outputDir)
    {
        var json = Path.Combine(outputDir, "speakers.json");
        if (File.Exists(json)) File.Delete(json);
        DeleteClips(Path.Combine(outputDir, "speakers"), keep: new HashSet<string>());
        return new SpeakerOutcome(segments, 0, 0, 0);
    }

    /// <summary>Raw voiceprint per piece from its longest turns of at least MinTurnForPrint, up to MaxPrintSpeech in
    /// all (the last cut to fit). A piece with less than MinPrintSpeech of such turns, or no audio in them, gets none.</summary>
    private SortedDictionary<int, float[]> PiecePrints(float[] samples, List<PieceTurn> turns, CancellationToken ct)
    {
        var prints = new SortedDictionary<int, float[]>();
        foreach (var piece in turns.GroupBy(t => t.Piece))
        {
            ct.ThrowIfCancellationRequested();
            var spans = new List<(double Start, double End)>();
            double total = 0;
            foreach (var t in piece.Where(t => t.End - t.Start >= SpeakerSettings.MinTurnForPrint)
                         .OrderByDescending(t => t.End - t.Start).ThenBy(t => t.Start))
            {
                var take = Math.Min(t.End - t.Start, SpeakerSettings.MaxPrintSpeech - total);
                if (take <= 0) break;
                spans.Add((t.Start, t.Start + take));
                total += take;
            }
            if (total < SpeakerSettings.MinPrintSpeech) continue;
            try { prints[piece.Key] = printer.Compute(samples, spans); }
            catch (ArgumentException) { } // no audio inside the turns: treated like a piece too short for a print
        }
        return prints;
    }

    private float[] BackgroundMean()
    {
        var (sum, count) = library.BackgroundSum;
        return Background.Combine(Background.Seed(), sum, count);
    }

    /// <summary>Gives each turn of a print-less piece to the printed piece of the nearest turn before it (after it,
    /// for leading turns) and joins printed pieces that sound alike. Turns carry a provisional group id ("g0");
    /// a group's raw voiceprint is the mean of its pieces' raw prints.</summary>
    private static (List<SpeakerTurn> Turns, Dictionary<string, Speaker> Groups) JoinPieces(
        List<PieceTurn> turns, SortedDictionary<int, float[]> prints, float[] mean)
    {
        var owner = new int?[turns.Count];
        int? last = null;
        for (var i = 0; i < turns.Count; i++)
        {
            if (prints.ContainsKey(turns[i].Piece)) last = turns[i].Piece;
            owner[i] = last;
        }
        int? next = null;
        for (var i = turns.Count - 1; i >= 0; i--)
        {
            if (prints.ContainsKey(turns[i].Piece)) next = turns[i].Piece;
            owner[i] ??= next;
        }

        var pieces = prints.Keys.ToList();
        var group = PieceJoiner.Join(pieces.Select(p => Voiceprint.Normalize(prints[p], mean)).ToList(),
            SpeakerSettings.JoinThreshold);
        var groupOfPiece = pieces.Select((p, i) => (p, g: group[i])).ToDictionary(x => x.p, x => x.g);

        var groupTurns = turns.Select((t, i) => new SpeakerTurn(t.Start, t.End, "g" + groupOfPiece[owner[i]!.Value])).ToList();
        var groups = group.Distinct().ToDictionary(g => "g" + g, g => new Speaker("g" + g,
            MeanOf(pieces.Where(p => groupOfPiece[p] == g).Select(p => prints[p]).ToList()), null));
        return (groupTurns, groups);
    }

    /// <summary>Numbers the groups S1, S2, … by first appearance in the final segments. A group left with no words
    /// is dropped, with its turns. False when no group has any words.</summary>
    private static bool Number(ref List<Segment> segments, List<SpeakerTurn> groupTurns, Dictionary<string, Speaker> groups,
        out List<SpeakerTurn> turns, out List<Speaker> speakers)
    {
        var ids = new Dictionary<string, string>(); // group id -> "Sn"; insertion order is first appearance
        foreach (var seg in segments)
            if (seg.Speaker is { } g && !ids.ContainsKey(g)) ids[g] = "S" + (ids.Count + 1);
        segments = segments.Select(s => s with { Speaker = s.Speaker is { } g ? ids[g] : null }).ToList();
        turns = groupTurns.Where(t => ids.ContainsKey(t.Speaker)).Select(t => t with { Speaker = ids[t.Speaker] }).ToList();
        speakers = ids.Select(kv => groups[kv.Key] with { Id = kv.Value }).ToList();
        return ids.Count > 0;
    }

    private static float[] MeanOf(IReadOnlyList<float[]> vectors)
    {
        var r = new float[vectors[0].Length];
        foreach (var v in vectors)
            for (var i = 0; i < r.Length; i++) r[i] += v[i];
        for (var i = 0; i < r.Length; i++) r[i] /= vectors.Count;
        return r;
    }

    /// <summary>Suggests library people for the speakers. Library prints of another length (another model) are ignored.</summary>
    private Suggestion?[] Suggest(List<Speaker> speakers, float[] mean)
    {
        var people = library.People
            .Select(p => new Candidate(p.Id, p.Name, p.Prints.Where(v => v.Vector.Length == mean.Length)
                .Select(v => Voiceprint.Normalize(v.Vector, mean)).ToList()))
            .Where(c => c.Prints.Count > 0).ToList();
        return Suggester.Suggest(speakers.Select(s => Voiceprint.Normalize(s.Raw, mean)).ToList(), people);
    }

    /// <summary>Words to turns, boundaries moved to sentence ends, segments split where the speaker changes.</summary>
    private static List<Segment> WordsToSpeakers(IReadOnlyList<Segment> segments, List<SpeakerTurn> turns)
    {
        var words = segments.SelectMany(s => s.Words).ToList();
        var bySpeaker = WordAligner.Snap(words, WordAligner.Assign(words, turns));
        return SegmentSplitter.Split(segments, bySpeaker).ToList();
    }

    private static SpeakerEntry Entry(Speaker s, List<SpeakerTurn> turns, List<Segment> segments)
    {
        var seconds = turns.Where(t => t.Speaker == s.Id).Sum(t => t.End - t.Start);
        // The longest segment with words (a wordless one only if there is no other); OrderBy is stable, so of
        // equally long segments the earlier one wins.
        var longest = segments.Where(x => x.Speaker == s.Id)
            .OrderByDescending(x => x.Words.Count > 0).ThenByDescending(x => x.End - x.Start).FirstOrDefault();
        var sample = longest?.Text.Trim() ?? "";
        if (sample.Length > SpeakerSettings.SampleChars) sample = sample[..SpeakerSettings.SampleChars];
        var status = s.Suggestion is null ? SpeakerStatus.Unknown : SpeakerStatus.Suggested;
        return new SpeakerEntry(s.Id, status, null, s.Suggestion?.PersonId, s.Suggestion?.Name, s.Suggestion?.Score,
            seconds, sample, $"speakers/{s.Id}.wav", s.Raw);
    }

    /// <summary>ClipSeconds from the middle of each speaker's longest turn, kept inside that turn and the audio.
    /// Clips of speakers from an earlier run that no longer exist are removed.</summary>
    private static void WriteClips(float[] samples, string outputDir, List<SpeakerTurn> turns, List<SpeakerEntry> entries)
    {
        var dir = Path.Combine(outputDir, "speakers");
        Directory.CreateDirectory(dir);
        var audioEnd = samples.Length / (double)Rate;
        foreach (var e in entries)
        {
            var t = turns.Where(x => x.Speaker == e.Id).OrderByDescending(x => x.End - x.Start).First();
            var mid = (t.Start + t.End) / 2;
            var from = Math.Max(0, Math.Max(t.Start, mid - SpeakerSettings.ClipSeconds / 2));
            var to = Math.Min(audioEnd, Math.Min(t.End, mid + SpeakerSettings.ClipSeconds / 2));
            var a = (int)Math.Clamp(Math.Round(from * Rate), 0, samples.Length);
            var b = (int)Math.Clamp(Math.Round(to * Rate), a, samples.Length);
            WriteAtomically(Path.Combine(dir, e.Id + ".wav"), samples.AsSpan(a, b - a));
        }

        DeleteClips(dir, keep: entries.Select(e => e.Id + ".wav").ToHashSet());
    }

    /// <summary>Removes speaker clips (S&lt;n&gt;.wav) not in keep; other files are left alone.</summary>
    private static void DeleteClips(string dir, IReadOnlySet<string> keep)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var old in Directory.EnumerateFiles(dir))
        {
            var name = Path.GetFileName(old);
            if (ClipName().IsMatch(name) && !keep.Contains(name)) File.Delete(old);
        }
    }

    private static void WriteAtomically(string path, ReadOnlySpan<float> clip)
    {
        var tmp = Path.Combine(Path.GetDirectoryName(path)!, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            WavFile.Write(tmp, Rate, clip);
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch { }
            throw;
        }
    }

    [GeneratedRegex(@"^S\d+\.wav$")]
    private static partial Regex ClipName();
}
