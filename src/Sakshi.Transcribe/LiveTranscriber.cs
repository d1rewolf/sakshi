using System.Text;
using System.Text.Json;

namespace Sakshi.Transcribe;

/// <summary>
/// Transcribes a recording while OBS is still writing it, for the live transcript window. Every
/// <see cref="Poll"/> it reads the audio written since it last looked (ending the piece at the quietest moment of
/// its last second, so a word is not cut in two; the rest waits for the next piece) and writes one JSON line per
/// spoken segment to <c>output</c>, with times from the start of the recording:
/// <c>{"start":12.4,"end":15.1,"text":"…"}</c>. After each piece it writes <c>{"readTo":15.6}</c>, how far
/// into the recording it has got, so a reader can show progress while it catches up.
///
/// Behind by more than <see cref="MaxChunk"/> (opened late in a meeting), it works through the backlog a
/// piece at a time without waiting, so lines appear as it goes. Silent pieces are skipped (Whisper invents
/// words in silence). When <c>stop</c> completes (the recording ended) it reads once more, transcribes that,
/// and returns 0 without working through any backlog: the full transcript is made after the meeting, and
/// the meeting file is about to be moved. A file that never becomes readable within <see cref="UnreadableFor"/>
/// (OBS recording to plain MP4, which has no index until it is finished) ends with
/// <c>{"error":"unreadable"}</c>, one that stops being readable for that long with <c>{"error":"lost"}</c>;
/// both exit 3.
/// </summary>
public sealed class LiveTranscriber(
    Func<TimeSpan, TimeSpan, CancellationToken, Task<byte[]?>> readAudio,
    ILiveWhisper whisper,
    TextWriter output,
    Func<TimeSpan, CancellationToken, Task> delay,
    Func<DateTimeOffset> now)
{
    public static readonly TimeSpan Poll = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan MaxChunk = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan UnreadableFor = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MinChunk = TimeSpan.FromSeconds(1);

    /// <summary>Peak sample level below which a piece counts as silence (about -40 dBFS).</summary>
    internal const short SilencePeak = 330;

    private const int SampleRate = 16000;
    private const int BytesPerSecond = SampleRate * 2;

    public async Task<int> RunAsync(Task stop, CancellationToken ct)
    {
        var offset = TimeSpan.Zero;
        var everRead = false;
        var failingSince = (DateTimeOffset?)now();
        while (true)
        {
            var stopping = stop.IsCompleted;
            var pcm = await readAudio(offset, MaxChunk, ct);
            if (pcm is null)
            {
                failingSince ??= now();
                if (now() - failingSince >= UnreadableFor)
                {
                    await WriteAsync(new { error = everRead ? "lost" : "unreadable" });
                    return 3;
                }
            }
            else
            {
                everRead = true;
                failingSince = null;
            }

            var got = TimeSpan.FromSeconds((pcm?.Length ?? 0) / (double)BytesPerSecond);
            if (pcm is not null && (got >= MinChunk || stopping && got > TimeSpan.Zero))
            {
                var backlog = got >= MaxChunk - MinChunk;
                if (!stopping && !backlog) pcm = pcm[..QuietEnd(pcm)];
                if (!Silent(pcm))
                    foreach (var s in await TranscribeAsync(pcm, ct))
                        await WriteAsync(new { start = Round(offset + s.Start), end = Round(offset + s.End), text = s.Text.Trim() });
                offset += TimeSpan.FromSeconds(pcm.Length / (double)BytesPerSecond);
                await WriteAsync(new { readTo = Round(offset) });
                if (!stopping && backlog) continue; // a backlog: keep going without waiting
            }

            if (stopping) return 0;
            await Task.WhenAny(delay(Poll, ct), stop);
            ct.ThrowIfCancellationRequested();
        }
    }

    private async Task<IReadOnlyList<Segment>> TranscribeAsync(byte[] pcm, CancellationToken ct)
    {
        using var wav = new MemoryStream(Wav(pcm), writable: false);
        return (await whisper.TranscribeAsync(wav, ct)).Where(s => !string.IsNullOrWhiteSpace(s.Text)).ToList();
    }

    private const int Window = BytesPerSecond / 10;   // 100 ms

    /// <summary>
    /// Where to end a live piece: in the middle of the quietest 100 ms of its last second, when that is a real pause
    /// (at most a quarter of the piece's loudest); otherwise, or for a piece of two seconds or less, at its end.
    /// </summary>
    public static int QuietEnd(byte[] pcm)
    {
        if (pcm.Length <= 2 * BytesPerSecond) return pcm.Length;
        var loudest = Peak(pcm, 0, pcm.Length);
        var (quietest, at) = (int.MaxValue, pcm.Length);
        for (var start = pcm.Length - BytesPerSecond; start + Window <= pcm.Length; start += Window / 2)
        {
            var peak = Peak(pcm, start, Window);
            if (peak < quietest) (quietest, at) = (peak, start + Window / 2);
        }
        return quietest * 4 <= loudest ? at & ~1 : pcm.Length;
    }

    private static int Peak(byte[] pcm, int from, int count)
    {
        var peak = 0;
        for (var i = from; i + 1 < from + count; i += 2) peak = Math.Max(peak, Math.Abs((int)BitConverter.ToInt16(pcm, i)));
        return peak;
    }

    internal static bool Silent(byte[] pcm)
    {
        for (var i = 0; i + 1 < pcm.Length; i += 2)
            if (Math.Abs((int)BitConverter.ToInt16(pcm, i)) >= SilencePeak) return false;
        return true;
    }

    private async Task WriteAsync(object line)
    {
        await output.WriteLineAsync(JsonSerializer.Serialize(line));
        await output.FlushAsync();
    }

    private static double Round(TimeSpan t) => Math.Round(t.TotalSeconds, 2);

    /// <summary>A 16 kHz mono 16-bit WAV file around <paramref name="pcm"/>.</summary>
    public static byte[] Wav(byte[] pcm)
    {
        using var ms = new MemoryStream(44 + pcm.Length);
        using (var w = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true))
        {
            w.Write("RIFF"u8);
            w.Write(36 + pcm.Length);
            w.Write("WAVEfmt "u8);
            w.Write(16);                 // fmt chunk size
            w.Write((short)1);           // PCM
            w.Write((short)1);           // mono
            w.Write(SampleRate);
            w.Write(BytesPerSecond);
            w.Write((short)2);           // block align
            w.Write((short)16);          // bits per sample
            w.Write("data"u8);
            w.Write(pcm.Length);
            w.Write(pcm);
        }
        return ms.ToArray();
    }
}
