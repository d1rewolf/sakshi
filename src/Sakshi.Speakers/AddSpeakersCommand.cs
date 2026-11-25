using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Sakshi.Transcribe;

namespace Sakshi.Speakers;

/// <summary>sakshi speakers --add: transcribe a meeting's audio again with word times, find its speakers, and
/// rewrite its transcript with labels. A meeting where nothing works is left exactly as it was.</summary>
public static partial class AddSpeakersCommand
{
    private static readonly string[] RecordingExtensions = [".mp4", ".mkv", ".m4a", ".mov"];

    [GeneratedRegex(@"^frame_(\d+)-(\d+)-(\d+)(?:_\d+)?\.jpg$", RegexOptions.IgnoreCase)]
    private static partial Regex FrameName();

    [GeneratedRegex(@"^- Source: `(.+)`\s*$", RegexOptions.Multiline)]
    private static partial Regex SourceLine();

    public static async Task<int> RunAsync(IReadOnlyList<string> meetings, IReadOnlyList<string> roots, IWhisper whisper,
        string model, ISpeakerStep step, TextWriter stdout, TextWriter stderr, CancellationToken ct)
    {
        var dirs = new List<string>();
        foreach (var m in meetings)
        {
            try { dirs.Add(MeetingLocator.Find(m, roots)); }
            catch (UsageException e)
            {
                stderr.WriteLine(e.Message);
                return 2;
            }
        }

        var failed = false;
        foreach (var dir in dirs)
        {
            ct.ThrowIfCancellationRequested();
            stdout.WriteLine($"==> {dir}");
            try
            {
                if (!await AddOneAsync(dir, whisper, model, step, stdout, stderr, ct)) failed = true;
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                stderr.WriteLine($"Warning: {dir}: speaker labels skipped: {e.Message}");
                failed = true;
            }
        }
        return failed ? 1 : 0;
    }

    private static async Task<bool> AddOneAsync(string dir, IWhisper whisper, string model, ISpeakerStep step,
        TextWriter stdout, TextWriter stderr, CancellationToken ct)
    {
        var wav = Path.Combine(dir, "audio.wav");
        var recording = Directory.EnumerateFiles(dir)
            .Where(f => RecordingExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal)
            .FirstOrDefault();
        var hasWav = File.Exists(wav);
        if (!hasWav && recording is null)
        {
            stderr.WriteLine($"{dir} has no audio.wav or recording to label");
            return false;
        }

        var speakersPath = Path.Combine(dir, "speakers.json");
        SpeakersFile? existing = null;
        if (File.Exists(speakersPath))
        {
            stdout.WriteLine("   replacing the existing speaker labels");
            try { existing = SpeakersFile.Read(speakersPath); } catch (InvalidDataException) { }
        }

        var language = "en";
        try { language = TranscriptFiles.ReadJson(Path.Combine(dir, "transcript.json")).Language; }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException or KeyNotFoundException
            or InvalidOperationException or UnauthorizedAccessException) { }

        var source = recording is not null ? Path.GetFileName(recording) : SourceFromMeetingMd(dir) ?? "audio.wav";
        IReadOnlyList<FrameRecord>? frames = existing is not null ? existing.Frames : ReadFrames(dir);

        if (!hasWav)
        {
            stdout.WriteLine("==> Extracting audio");
            await Ffmpeg.ExtractAudioAsync(recording!, wav, ct);
        }

        stdout.WriteLine("==> Transcribing with Whisper.net");
        var segments = await whisper.TranscribeAsync(wav, model, language, 0, ct, words: true);

        stdout.WriteLine("==> Finding speakers");
        var outcome = await step.LabelAsync(wav, dir, segments, source, frames, stdout, ct);
        if (outcome.Speakers <= 0)
        {
            stdout.WriteLine($"{dir}: no speakers found");
            return true;
        }

        var labelled = outcome.Segments;
        var md = MeetingMarkdown.Render(source, frames ?? [], labelled, frames is null);
        TranscribeCommand.ReplaceFiles(
        [
            (Path.Combine(dir, "transcript.json"), p => TranscriptFiles.WriteJson(p, labelled, language)),
            (Path.Combine(dir, "transcript.srt"), p => TranscriptFiles.WriteSrt(p, labelled)),
            (Path.Combine(dir, "meeting.md"), p => File.WriteAllText(p, md, new UTF8Encoding(false))),
        ]);
        stdout.WriteLine(
            $"   found {outcome.Speakers} {(outcome.Speakers == 1 ? "speaker" : "speakers")} ({outcome.Suggested + outcome.Unknown} to name)");
        return true;
    }

    private static string? SourceFromMeetingMd(string dir)
    {
        try
        {
            var md = Path.Combine(dir, "meeting.md");
            if (!File.Exists(md)) return null;
            var m = SourceLine().Match(File.ReadAllText(md));
            return m.Success ? m.Groups[1].Value : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    private static List<FrameRecord>? ReadFrames(string dir)
    {
        var folder = Path.Combine(dir, "frames");
        if (!Directory.Exists(folder)) return null;
        var records = new List<FrameRecord>();
        foreach (var f in Directory.EnumerateFiles(folder))
        {
            var name = Path.GetFileName(f);
            if (FrameName().Match(name) is not { Success: true } m) continue;
            var seconds = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * 3600.0
                + int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) * 60
                + int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
            records.Add(new FrameRecord(seconds, name));
        }
        return records.OrderBy(r => r.Seconds).ThenBy(r => r.FileName, StringComparer.Ordinal).ToList();
    }
}
