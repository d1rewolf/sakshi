namespace Sakshi.Speakers;

public sealed record Candidate(string PersonId, string Name, IReadOnlyList<float[]> Prints);

public sealed record Suggestion(string PersonId, string Name, double Score);

public static class Suggester
{
    /// <summary>Suggests a known person for each speaker, or null. Each person is suggested for at most one speaker.</summary>
    public static Suggestion?[] Suggest(IReadOnlyList<float[]> speakers, IReadOnlyList<Candidate> people)
    {
        var result = new Suggestion?[speakers.Count];
        var usable = people.Where(p => p.Prints.Count > 0).ToList();
        var scores = new double[speakers.Count][];
        for (var s = 0; s < speakers.Count; s++)
            scores[s] = usable.Select(p => p.Prints.Max(pr => Voiceprint.Cosine(speakers[s], pr))).ToArray();

        var taken = new HashSet<int>();
        var pending = Enumerable.Range(0, speakers.Count).ToList();
        while (pending.Count > 0)
        {
            // Each pending speaker's qualifying best person among those not yet taken.
            var picks = new List<(int Speaker, int Person, double Score)>();
            foreach (var s in pending)
            {
                int bp = -1;
                double best = double.NegativeInfinity, second = -1;
                for (var p = 0; p < usable.Count; p++)
                {
                    if (taken.Contains(p)) continue;
                    var sc = scores[s][p];
                    if (sc > best) { second = best == double.NegativeInfinity ? -1 : best; best = sc; bp = p; }
                    else if (sc > second) second = sc;
                }
                if (bp >= 0 && best >= SpeakerSettings.SuggestThreshold && best - second >= SpeakerSettings.Margin)
                    picks.Add((s, bp, best));
            }
            if (picks.Count == 0) break;

            // The highest-scoring pick overall is safe: nobody can outscore it for that person.
            var winners = picks.GroupBy(x => x.Person)
                .Select(g => g.OrderByDescending(x => x.Score).ThenBy(x => x.Speaker).First())
                .ToList();
            var winnerSpeakers = winners.Select(w => w.Speaker).ToHashSet();
            // Only speakers that lost a conflict are re-evaluated; one that never qualified stays null.
            var losers = picks.Where(x => !winnerSpeakers.Contains(x.Speaker)).Select(x => x.Speaker).ToList();
            foreach (var w in winners)
            {
                var person = usable[w.Person];
                result[w.Speaker] = new Suggestion(person.PersonId, person.Name, w.Score);
                taken.Add(w.Person);
            }
            // Losers are re-evaluated next round against the people still free.
            pending = losers;
        }
        return result;
    }
}
