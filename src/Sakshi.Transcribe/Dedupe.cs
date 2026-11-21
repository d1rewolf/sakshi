using System.Numerics;

namespace Sakshi.Transcribe;

public static class Dedupe
{
    /// <summary>
    /// Drops frames whose dHash is within maxDistance of any earlier kept frame (deleting the files).
    /// Batch decode first, per-frame fallback if that fails; undecodable frames are always kept.
    /// </summary>
    public static async Task<IReadOnlyList<FrameRecord>> RunAsync(
        string framesDir,
        IReadOnlyList<FrameRecord> records,
        int maxDistance,
        TextWriter log,
        CancellationToken ct,
        Func<IReadOnlyList<string>, CancellationToken, Task<byte[]?>>? batchSource = null,
        Func<string, CancellationToken, Task<byte[]?>>? singleSource = null)
    {
        if (records.Count == 0) return records;
        batchSource ??= Ffmpeg.GrayPixelsAsync;
        singleSource ??= Ffmpeg.GrayPixelsAsync;

        var total = records.Count;
        var paths = records.Select(r => Path.Combine(framesDir, r.FileName)).ToList();
        await log.WriteLineAsync($"   hashing {total} frames");
        await log.FlushAsync(ct);

        BigInteger?[]? batch = null;
        var px = await batchSource(paths, ct);
        if (px is not null && px.Length == DHash.BytesPerFrame * total)
        {
            batch = new BigInteger?[total];
            for (var i = 0; i < total; i++) batch[i] = DHash.FromGray(px, i * DHash.BytesPerFrame);
        }

        var kept = new List<FrameRecord>();
        var hashes = new List<BigInteger>();
        var interval = Math.Max(1, Math.Min(2000, total / 4));
        for (var i = 0; i < total; i++)
        {
            BigInteger? h;
            if (batch is not null) h = batch[i];
            else
            {
                var one = await singleSource(paths[i], ct);
                h = one is { Length: DHash.BytesPerFrame } ? DHash.FromGray(one) : null;
            }

            if (h is { } hv && hashes.Any(k => DHash.Distance(hv, k) <= maxDistance))
                File.Delete(paths[i]);
            else
            {
                if (h is { } kv) hashes.Add(kv);
                kept.Add(records[i]);
            }

            if ((i + 1) % interval == 0 && i + 1 < total)
            {
                await log.WriteLineAsync($"   dedupe {i + 1}/{total} (dropped {i + 1 - kept.Count})");
                await log.FlushAsync(ct);
            }
        }
        await log.WriteLineAsync($"   deduped {total - kept.Count} of {total} frames");
        return kept;
    }
}
