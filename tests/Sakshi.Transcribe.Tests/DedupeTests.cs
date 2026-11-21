using System.Numerics;

namespace Sakshi.Transcribe.Tests;

public class DedupeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sakshi-dedupe-" + Guid.NewGuid().ToString("N"));

    public DedupeTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, true);

    private static byte[] Fixture() => Enumerable.Range(0, 272).Select(i => (byte)((i * 37 + 11) % 256)).ToArray();

    // Gray 17x16 frame with a left<right pattern controlled by `flipRows` rows inverted.
    private static byte[] Frame(int flipRows, bool invertAll = false)
    {
        var b = new byte[272];
        for (var row = 0; row < 16; row++)
            for (var col = 0; col < 17; col++)
            {
                var asc = (byte)(col * 10);
                var inv = (byte)(250 - col * 10);
                b[row * 17 + col] = (row < flipRows) != invertAll ? inv : asc;
            }
        return b;
    }

    private FrameRecord[] MakeFiles(int n)
    {
        var recs = new FrameRecord[n];
        for (var i = 0; i < n; i++)
        {
            var name = $"frame_00-00-{i:00}.jpg";
            File.WriteAllText(Path.Combine(_dir, name), "x");
            recs[i] = new FrameRecord(i, name);
        }
        return recs;
    }

    [Fact]
    public void DHash_MatchesPythonForFixture() =>
        Assert.Equal(
            BigInteger.Parse("114880284847644585674607518446320356000420327228284351045492417814306794471165"),
            DHash.FromGray(Fixture()));

    [Fact]
    public void DHash_OffsetReadsLaterFrame()
    {
        var two = Fixture().Concat(Frame(0)).ToArray();
        Assert.Equal(DHash.FromGray(Frame(0)), DHash.FromGray(two, 272));
    }

    [Fact]
    public void DHash_DistanceIsPopcountOfXor()
    {
        Assert.Equal(0, DHash.Distance(DHash.FromGray(Frame(0)), DHash.FromGray(Frame(0))));
        Assert.Equal(16 * 3, DHash.Distance(DHash.FromGray(Frame(0)), DHash.FromGray(Frame(3))));
        Assert.Equal(256, DHash.Distance(DHash.FromGray(Frame(0)), DHash.FromGray(Frame(0, invertAll: true))));
    }

    [Fact]
    public async Task Dedupe_DropsNearDuplicates_KeepsUndecodable()
    {
        var recs = MakeFiles(4);
        var px = new List<byte[]> { Frame(0), Frame(0), Frame(8), Frame(0) };
        // frame 2 differs by 128 bits from 0 -> kept; frame 3 identical to 0 -> dropped; frame 1 identical -> dropped.
        var log = new StringWriter();
        var kept = await Dedupe.RunAsync(_dir, recs, 8, log, TestContext.Current.CancellationToken,
            batchSource: (files, _) => Task.FromResult<byte[]?>(px.SelectMany(p => p).ToArray()));

        Assert.Equal(new[] { "frame_00-00-00.jpg", "frame_00-00-02.jpg" }, kept.Select(r => r.FileName));
        Assert.True(File.Exists(Path.Combine(_dir, "frame_00-00-00.jpg")));
        Assert.False(File.Exists(Path.Combine(_dir, "frame_00-00-01.jpg")));
        Assert.True(File.Exists(Path.Combine(_dir, "frame_00-00-02.jpg")));
        Assert.False(File.Exists(Path.Combine(_dir, "frame_00-00-03.jpg")));
        Assert.Equal(
            "   hashing 4 frames\n   dedupe 1/4 (dropped 0)\n   dedupe 2/4 (dropped 1)\n   dedupe 3/4 (dropped 1)\n   deduped 2 of 4 frames\n",
            log.ToString().Replace("\r\n", "\n"));
    }

    [Fact]
    public async Task Dedupe_BatchFails_FallsBackPerFrame_UndecodableKept()
    {
        var recs = MakeFiles(4);
        var bad = Path.Combine(_dir, recs[1].FileName);
        var kept = await Dedupe.RunAsync(_dir, recs, 8, new StringWriter(), TestContext.Current.CancellationToken,
            batchSource: (_, _) => Task.FromResult<byte[]?>(null),
            singleSource: (f, _) => Task.FromResult<byte[]?>(
                f == bad ? null : f.EndsWith("02.jpg") ? Frame(8) : Frame(0)));

        // 0 kept, 1 undecodable -> kept, 2 distinct -> kept, 3 dup of 0 -> dropped.
        Assert.Equal(new[] { 0.0, 1.0, 2.0 }, kept.Select(r => r.Seconds));
        Assert.False(File.Exists(Path.Combine(_dir, recs[3].FileName)));
        Assert.True(File.Exists(bad));
    }

    [Fact]
    public async Task Dedupe_WrongSizedBatch_FallsBack()
    {
        var recs = MakeFiles(2);
        var singles = 0;
        var kept = await Dedupe.RunAsync(_dir, recs, 8, new StringWriter(), TestContext.Current.CancellationToken,
            batchSource: (_, _) => Task.FromResult<byte[]?>(new byte[10]),
            singleSource: (_, _) => { singles++; return Task.FromResult<byte[]?>(Frame(0)); });
        Assert.Equal(2, singles);
        Assert.Single(kept);
    }

    [Fact]
    public async Task Dedupe_Empty_ReturnsEmptyWithoutOutput()
    {
        var log = new StringWriter();
        var kept = await Dedupe.RunAsync(_dir, [], 8, log, TestContext.Current.CancellationToken);
        Assert.Empty(kept);
        Assert.Equal("", log.ToString());
    }

    [Fact]
    public async Task Dedupe_RealFfmpeg_IdenticalFramesCollapse()
    {
        Assert.SkipUnless(FrameTests.OnPath("ffmpeg"), "ffmpeg is not on PATH");
        var src = Path.Combine(_dir, "a.jpg");
        var gen = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ffmpeg")
        {
            ArgumentList = { "-y", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc=size=160x120:duration=1:rate=1", "-frames:v", "1", src },
            RedirectStandardError = true,
        })!;
        await gen.WaitForExitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, gen.ExitCode);
        var recs = new[] { new FrameRecord(0, "frame_00-00-00.jpg"), new FrameRecord(1, "frame_00-00-01.jpg") };
        foreach (var r in recs) File.Copy(src, Path.Combine(_dir, r.FileName));

        var kept = await Dedupe.RunAsync(_dir, recs, 8, new StringWriter(), TestContext.Current.CancellationToken);
        Assert.Single(kept);
    }
}
