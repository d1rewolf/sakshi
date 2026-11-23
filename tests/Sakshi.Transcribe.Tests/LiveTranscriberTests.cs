using System.Text.Json;
using Sakshi.Transcribe;

namespace Sakshi.Transcribe.Tests;

public sealed class LiveTranscriberTests
{
    private const int BytesPerSecond = 32000;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A recording that grows by <see cref="Grow"/> seconds per poll; reads return what exists past the offset.</summary>
    private sealed class Recording
    {
        public double Written;            // seconds on disk
        public double Grow = 5;
        public bool Readable = true;
        public bool Silent;
        public List<(double From, double Max)> Reads { get; } = [];

        public Task<byte[]?> Read(TimeSpan from, TimeSpan max, CancellationToken ct)
        {
            Reads.Add((from.TotalSeconds, max.TotalSeconds));
            if (!Readable) return Task.FromResult<byte[]?>(null);
            var seconds = Math.Max(0, Math.Min(Written - from.TotalSeconds, max.TotalSeconds));
            var pcm = new byte[(int)(seconds * BytesPerSecond)];
            if (!Silent) Array.Fill(pcm, (byte)0x10); // every sample 0x1010: clearly not silence
            return Task.FromResult<byte[]?>(pcm);
        }
    }

    /// <summary>One segment per piece, covering it, saying how long the piece was.</summary>
    private sealed class PieceWhisper : ILiveWhisper
    {
        public List<double> Pieces { get; } = [];
        public Task<IReadOnlyList<Segment>> TranscribeAsync(Stream wav, CancellationToken ct)
        {
            var seconds = (wav.Length - 44) / (double)BytesPerSecond;
            Pieces.Add(seconds);
            return Task.FromResult<IReadOnlyList<Segment>>([new Segment(TimeSpan.Zero, TimeSpan.FromSeconds(seconds), $" piece {seconds:0} ")]);
        }
    }

    private sealed record Run(int Code, List<JsonElement> Lines, int Waits);

    private static async Task<Run> RunAsync(Recording rec, PieceWhisper whisper, Func<int, bool> stopAfterWaits, DateTimeOffset[]? clock = null)
    {
        var output = new StringWriter();
        var now = new DateTimeOffset(2026, 11, 20, 9, 0, 0, TimeSpan.Zero);
        var waits = 0;
        var stop = new TaskCompletionSource();
        Task Delay(TimeSpan d, CancellationToken ct)
        {
            waits++;
            now += d;
            rec.Written += rec.Grow;
            if (stopAfterWaits(waits)) stop.TrySetResult();
            return Task.CompletedTask;
        }
        var live = new LiveTranscriber(rec.Read, whisper, output, Delay, () => now);
        var code = await live.RunAsync(stop.Task, Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct);
        var lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => JsonDocument.Parse(l).RootElement.Clone()).ToList();
        return new Run(code, lines, waits);
    }

    private static IEnumerable<(double Start, string Text)> Segments(Run r) =>
        r.Lines.Where(l => l.TryGetProperty("text", out _)).Select(l => (l.GetProperty("start").GetDouble(), l.GetProperty("text").GetString()!));

    [Fact]
    public async Task Each_new_stretch_is_transcribed_with_times_from_the_start_of_the_recording()
    {
        var rec = new Recording { Written = 8 };
        var run = await RunAsync(rec, new PieceWhisper(), waits => waits == 3);

        Assert.Equal(0, run.Code);
        Assert.Equal([(0.0, "piece 8"), (8.0, "piece 5"), (13.0, "piece 5"), (18.0, "piece 5")], Segments(run));
        Assert.Equal(23.0, run.Lines.Last(l => l.TryGetProperty("readTo", out _)).GetProperty("readTo").GetDouble());
    }

    [Fact]
    public async Task A_backlog_is_worked_through_in_pieces_without_waiting()
    {
        // Opened 95 s into the meeting: three full pieces back to back, then the rest, then it waits.
        var rec = new Recording { Written = 95, Grow = 0 };
        var whisper = new PieceWhisper();
        var run = await RunAsync(rec, whisper, waits => waits == 1);

        Assert.Equal([30.0, 30.0, 30.0, 5.0], whisper.Pieces);
        Assert.Equal(1, run.Waits);
    }

    [Fact]
    public async Task Less_than_a_second_waits_for_more_unless_the_recording_has_ended()
    {
        var rec = new Recording { Written = 0.5, Grow = 0 };
        var whisper = new PieceWhisper();
        var run = await RunAsync(rec, whisper, waits => waits == 2);

        // Two polls with half a second each time: nothing; then the recording ends and the half second is used.
        Assert.Equal([0.5], whisper.Pieces);
        Assert.Equal(0, run.Code);
    }

    [Fact]
    public async Task A_file_that_never_becomes_readable_ends_with_an_error()
    {
        // OBS recording to plain MP4: no index until the recording ends.
        var rec = new Recording { Readable = false };
        var run = await RunAsync(rec, new PieceWhisper(), _ => false);

        Assert.Equal(3, run.Code);
        Assert.Equal("unreadable", run.Lines.Single().GetProperty("error").GetString());
        Assert.Equal(10, run.Waits); // 30 s at one look every 3 s
    }

    [Fact]
    public async Task A_file_readable_after_a_few_seconds_is_fine()
    {
        // OBS's first fragment takes about 8 s.
        var rec = new Recording { Readable = false };
        var whisper = new PieceWhisper();
        var run = await RunAsync(rec, whisper, waits =>
        {
            if (waits == 2) rec.Readable = true;
            return waits == 4;
        });

        Assert.Equal(0, run.Code);
        Assert.NotEmpty(whisper.Pieces);
        Assert.DoesNotContain(run.Lines, l => l.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task Silence_is_not_sent_to_whisper_but_still_counts_as_read()
    {
        var rec = new Recording { Written = 10, Grow = 0, Silent = true };
        var whisper = new PieceWhisper();
        var run = await RunAsync(rec, whisper, waits => waits == 1);
        Assert.Empty(whisper.Pieces);
        Assert.Equal(10.0, run.Lines.Last().GetProperty("readTo").GetDouble());
    }

    [Fact]
    public async Task After_the_recording_ends_it_reads_once_more_and_does_not_work_through_a_backlog()
    {
        // The meeting file is about to be moved, and the full transcript is coming: no point catching up.
        var rec = new Recording { Written = 5, Grow = 100 };
        var whisper = new PieceWhisper();
        var run = await RunAsync(rec, whisper, waits => waits == 1);
        Assert.Equal([5.0, 30.0], whisper.Pieces);
        Assert.Equal(0, run.Code);
    }

    [Fact]
    public async Task A_file_that_stops_being_readable_ends_with_lost()
    {
        var rec = new Recording { Written = 8 };
        var run = await RunAsync(rec, new PieceWhisper(), waits =>
        {
            if (waits == 1) rec.Readable = false;
            return false;
        });
        Assert.Equal(3, run.Code);
        Assert.Equal("lost", run.Lines.Last().GetProperty("error").GetString());
    }

    /// <summary>Loud sound, with quiet stretches (from, to) in seconds.</summary>
    private static byte[] Sound(double seconds, params (double From, double To)[] quiet)
    {
        var pcm = new byte[(int)(seconds * 16000) * 2];
        for (var i = 0; i < pcm.Length / 2; i++)
        {
            var t = i / 16000.0;
            short v = quiet.Any(q => t >= q.From && t < q.To) ? (short)20 : (short)8000;
            BitConverter.GetBytes(v).CopyTo(pcm, i * 2);
        }
        return pcm;
    }

    [Fact]
    public void A_piece_ends_in_the_pause_of_its_last_second()
    {
        var end = LiveTranscriber.QuietEnd(Sound(3, (2.4, 2.6)));
        Assert.InRange(end / (double)BytesPerSecond, 2.4, 2.6);
        Assert.Equal(0, end % 2);   // whole samples
    }

    [Fact]
    public void A_piece_with_no_pause_near_its_end_is_used_whole()
    {
        Assert.Equal(3 * BytesPerSecond, LiveTranscriber.QuietEnd(Sound(3, (0.5, 0.8))));   // the pause is too early
        Assert.Equal(3 * BytesPerSecond, LiveTranscriber.QuietEnd(Sound(3)));
    }

    [Fact]
    public void A_short_piece_is_never_cut()
    {
        Assert.Equal(2 * BytesPerSecond, LiveTranscriber.QuietEnd(Sound(2, (1.5, 1.7))));
    }

    [Fact]
    public async Task What_follows_the_pause_starts_the_next_piece()
    {
        var sound = Sound(6, (2.4, 2.6));
        var written = 3.0;
        var reads = new List<double>();
        Task<byte[]?> Read(TimeSpan from, TimeSpan max, CancellationToken ct)
        {
            reads.Add(from.TotalSeconds);
            var start = (int)(from.TotalSeconds * 16000) * 2;
            var end = Math.Min((int)(written * 16000) * 2, sound.Length);
            return Task.FromResult<byte[]?>(sound[start..Math.Max(start, end)]);
        }
        var whisper = new PieceWhisper();
        var stop = new TaskCompletionSource();
        Task Delay(TimeSpan d, CancellationToken ct) { written += 3; stop.TrySetResult(); return Task.CompletedTask; }
        await new LiveTranscriber(Read, whisper, new StringWriter(), Delay, () => DateTimeOffset.Now).RunAsync(stop.Task, Ct);

        Assert.InRange(whisper.Pieces[0], 2.4, 2.6);
        Assert.InRange(reads[1], 2.4, 2.6);
        Assert.InRange(whisper.Pieces[0] + whisper.Pieces[1], 5.99, 6.01);   // nothing lost, nothing twice
    }

    [Fact]
    public void The_wav_header_describes_16_kHz_mono_16_bit()
    {
        var wav = LiveTranscriber.Wav(new byte[BytesPerSecond]);
        Assert.Equal(44 + BytesPerSecond, wav.Length);
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal(16000, BitConverter.ToInt32(wav, 24));
        Assert.Equal(1, BitConverter.ToInt16(wav, 22));
        Assert.Equal(BytesPerSecond, BitConverter.ToInt32(wav, 40));
    }
}
