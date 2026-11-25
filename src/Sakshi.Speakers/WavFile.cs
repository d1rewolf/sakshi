using System.Buffers.Binary;

namespace Sakshi.Speakers;

public static class WavFile
{
    public static (int SampleRate, float[] Samples) Read(string path)
    {
        var b = File.ReadAllBytes(path);
        if (b.Length < 12 || !b.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !b.AsSpan(8, 4).SequenceEqual("WAVE"u8))
            throw new InvalidDataException($"Not a WAV file: {path}");
        int format = 0, channels = 0, rate = 0, bits = 0;
        var haveFmt = false;
        var pos = 12;
        while (pos + 8 <= b.Length)
        {
            var id = b.AsSpan(pos, 4);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(pos + 4, 4));
            var body = pos + 8;
            if (id.SequenceEqual("fmt "u8) && size >= 16 && body + 16 <= b.Length)
            {
                format = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(body, 2));
                channels = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(body + 2, 2));
                rate = (int)BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(body + 4, 4));
                bits = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(body + 14, 2));
                haveFmt = true;
            }
            else if (id.SequenceEqual("data"u8))
            {
                if (!haveFmt) throw new InvalidDataException($"WAV data before format in {path}");
                if (format != 1 || bits != 16 || channels < 1)
                    throw new InvalidDataException($"Only 16-bit PCM WAV is supported: {path}");
                var len = (int)Math.Min(size, (uint)(b.Length - body));
                var frames = len / (2 * channels);
                var samples = new float[frames];
                for (var i = 0; i < frames; i++)
                    samples[i] = BinaryPrimitives.ReadInt16LittleEndian(b.AsSpan(body + i * 2 * channels, 2)) / 32768f;
                return (rate, samples);
            }
            var next = (long)body + size + (size & 1);
            if (next > b.Length) break;
            pos = (int)next;
        }
        throw new InvalidDataException($"No audio data in {path}");
    }

    public static void Write(string path, int sampleRate, ReadOnlySpan<float> samples)
    {
        var dataLen = samples.Length * 2;
        var b = new byte[44 + dataLen];
        var s = b.AsSpan();
        "RIFF"u8.CopyTo(s);
        BinaryPrimitives.WriteInt32LittleEndian(s[4..], 36 + dataLen);
        "WAVEfmt "u8.CopyTo(s[8..]);
        BinaryPrimitives.WriteInt32LittleEndian(s[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(s[20..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(s[22..], 1);
        BinaryPrimitives.WriteInt32LittleEndian(s[24..], sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(s[28..], sampleRate * 2);
        BinaryPrimitives.WriteInt16LittleEndian(s[32..], 2);
        BinaryPrimitives.WriteInt16LittleEndian(s[34..], 16);
        "data"u8.CopyTo(s[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(s[40..], dataLen);
        for (var i = 0; i < samples.Length; i++)
        {
            var v = float.IsNaN(samples[i]) ? 0f : Math.Clamp(samples[i], -1f, 1f);
            BinaryPrimitives.WriteInt16LittleEndian(s[(44 + i * 2)..], (short)Math.Round(v * 32767f));
        }
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(path, b);
    }
}
