using System.Text;
using Sakshi.Transcribe;

namespace Sakshi.Speakers;

public static class MeetingRewriter
{
    /// <summary>
    /// Sets every segment's label from <paramref name="file"/> and writes transcript.json, transcript.srt, meeting.md
    /// and speakers.json: all to temp files first, then renamed over the old ones.
    /// </summary>
    public static void Write(string dir, SpeakersFile file, IReadOnlyList<Segment> segments, string language)
    {
        var labels = file.Speakers.ToDictionary(s => s.Id, s => s.Label);
        var labelled = segments.Select(s =>
            s.Speaker is not null && labels.TryGetValue(s.Speaker, out var l) ? s with { SpeakerLabel = l } : s).ToList();
        var noFrames = file.Frames is null;
        var md = MeetingMarkdown.Render(file.Source, file.Frames ?? [], labelled, noFrames);
        TranscribeCommand.ReplaceFiles(
        [
            (Path.Combine(dir, "transcript.json"), p => TranscriptFiles.WriteJson(p, labelled, language)),
            (Path.Combine(dir, "transcript.srt"), p => TranscriptFiles.WriteSrt(p, labelled)),
            (Path.Combine(dir, "meeting.md"), p => File.WriteAllText(p, md, new UTF8Encoding(false))),
            (Path.Combine(dir, "speakers.json"), file.Write),
        ]);
    }
}
