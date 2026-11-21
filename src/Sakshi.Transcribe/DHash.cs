using System.Numerics;

namespace Sakshi.Transcribe;

/// <summary>256-bit difference hash over 17x16 gray pixels: bit = left &lt; right, row-major, MSB first.</summary>
public static class DHash
{
    public const int BytesPerFrame = 17 * 16;

    public static BigInteger FromGray(ReadOnlySpan<byte> px, int offset = 0)
    {
        var bytes = new byte[32];
        var k = 0;
        for (var row = 0; row < 16; row++)
        {
            var b = offset + row * 17;
            for (var col = 0; col < 16; col++, k++)
                if (px[b + col] < px[b + col + 1])
                    bytes[k / 8] |= (byte)(0x80 >> (k % 8));
        }
        return new BigInteger(bytes, isUnsigned: true, isBigEndian: true);
    }

    public static int Distance(BigInteger a, BigInteger b)
    {
        var x = a ^ b;
        var count = 0;
        foreach (var by in x.ToByteArray(isUnsigned: true)) count += BitOperations.PopCount(by);
        return count;
    }
}
