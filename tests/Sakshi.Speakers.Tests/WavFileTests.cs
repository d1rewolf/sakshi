using Sakshi.Speakers;

namespace Sakshi.Speakers.Tests;

public sealed class WavFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sakshi-wav-" + Guid.NewGuid().ToString("N"));
    public WavFileTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void ToneRoundTrips()
    {
        var path = Path.Combine(_dir, "a.wav");
        var tone = Enumerable.Range(0, 16000).Select(i => (float)(0.5 * Math.Sin(2 * Math.PI * 440 * i / 16000))).ToArray();
        WavFile.Write(path, 16000, tone);
        var (rate, samples) = WavFile.Read(path);
        Assert.Equal(16000, rate);
        Assert.Equal(tone.Length, samples.Length);
        for (var i = 0; i < tone.Length; i++) Assert.True(Math.Abs(tone[i] - samples[i]) <= 1.0 / 32768, $"sample {i}");
    }

    [Fact]
    public void ClampsOutOfRange()
    {
        var path = Path.Combine(_dir, "c.wav");
        WavFile.Write(path, 8000, [2f, -2f]);
        var (_, s) = WavFile.Read(path);
        Assert.InRange(s[0], 0.999f, 1f);
        Assert.InRange(s[1], -1f, -0.999f);
    }

    [Fact]
    public void SkipsUnknownChunksAndTakesFirstChannel()
    {
        var path = Path.Combine(_dir, "s.wav");
        using (var ms = new MemoryStream())
        using (var w = new BinaryWriter(ms))
        {
            w.Write("RIFF"u8); w.Write(0); w.Write("WAVE"u8);
            w.Write("LIST"u8); w.Write(3); w.Write(new byte[] { 1, 2, 3, 0 }); // odd size + pad
            w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)2); w.Write(8000); w.Write(32000); w.Write((short)4); w.Write((short)16);
            w.Write("data"u8); w.Write(8);
            w.Write((short)16384); w.Write((short)-16384); w.Write((short)-32768); w.Write((short)0);
            File.WriteAllBytes(path, ms.ToArray());
        }
        var (rate, s) = WavFile.Read(path);
        Assert.Equal(8000, rate);
        Assert.Equal(2, s.Length);
        Assert.Equal(0.5f, s[0], 4);
        Assert.Equal(-1f, s[1], 4);
    }

    [Fact]
    public void Non16BitThrows()
    {
        var path = Path.Combine(_dir, "f.wav");
        using (var ms = new MemoryStream())
        using (var w = new BinaryWriter(ms))
        {
            w.Write("RIFF"u8); w.Write(0); w.Write("WAVE"u8);
            w.Write("fmt "u8); w.Write(16); w.Write((short)3); w.Write((short)1); w.Write(8000); w.Write(32000); w.Write((short)4); w.Write((short)32);
            w.Write("data"u8); w.Write(4); w.Write(0f);
            File.WriteAllBytes(path, ms.ToArray());
        }
        Assert.Throws<InvalidDataException>(() => WavFile.Read(path));
    }
}
