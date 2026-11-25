using System.Security.Cryptography;

namespace Sakshi.Speakers;

public sealed record SpeakerModelPaths(string Segmentation, string Embedding);

public static class SpeakerModels
{
    private const string SegmentationName = "pyannote-segmentation-3-0.onnx";
    private const string EmbeddingName = "wespeaker_en_voxceleb_resnet34_LM.onnx";

    private static readonly Uri SegmentationUrl = new("https://huggingface.co/csukuangfj/sherpa-onnx-pyannote-segmentation-3-0/resolve/main/model.onnx");
    private static readonly Uri EmbeddingUrl = new("https://github.com/k2-fsa/sherpa-onnx/releases/download/speaker-recongition-models/wespeaker_en_voxceleb_resnet34_LM.onnx");

    private static readonly IReadOnlyDictionary<string, string> Sha256 = new Dictionary<string, string>
    {
        [SegmentationName] = "220ad67ca923bef2fa91f2390c786097bf305bceb5e261d4af67b38e938e1079",
        [EmbeddingName] = "e9848563da86f263117134dfd7ad63c92355b37de492b55e325400c9d9c39012",
    };

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

    public static SpeakerModelPaths In(string modelsDir)
    {
        var dir = Path.Combine(modelsDir, "speakers");
        return new SpeakerModelPaths(Path.Combine(dir, SegmentationName), Path.Combine(dir, EmbeddingName));
    }

    /// <summary>Null when both models are present, otherwise the reason one could not be had.</summary>
    public static Task<string?> EnsureAsync(string modelsDir, TextWriter stdout, CancellationToken ct,
        Func<Uri, Stream, CancellationToken, Task>? download = null) =>
        EnsureAsync(modelsDir, stdout, ct, download, Sha256);

    internal static async Task<string?> EnsureAsync(string modelsDir, TextWriter stdout, CancellationToken ct,
        Func<Uri, Stream, CancellationToken, Task>? download, IReadOnlyDictionary<string, string> hashes)
    {
        download ??= DownloadAsync;
        var paths = In(modelsDir);
        Directory.CreateDirectory(Path.GetDirectoryName(paths.Segmentation)!);
        foreach (var (path, url) in new[] { (paths.Segmentation, SegmentationUrl), (paths.Embedding, EmbeddingUrl) })
        {
            var name = Path.GetFileName(path);
            // sherpa kills the process on a bad model, so an existing file must match its hash too.
            if (File.Exists(path))
            {
                if (await HashOfAsync(path, ct) == hashes[name].ToLowerInvariant()) continue;
                TryDelete(path);
            }
            var part = path + ".part";
            stdout.WriteLine($"==> Downloading speaker model {name} to {path}");
            try
            {
                string actual;
                await using (var fs = new FileStream(part, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
                {
                    await download(url, fs, ct);
                    await fs.FlushAsync(ct);
                    fs.Position = 0;
                    actual = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct)).ToLowerInvariant();
                }
                if (!string.Equals(actual, hashes[name], StringComparison.OrdinalIgnoreCase))
                {
                    TryDelete(part);
                    return $"speaker model {name}: SHA-256 mismatch";
                }
                File.Move(part, path, overwrite: true);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                TryDelete(part);
                throw;
            }
            catch (Exception e)
            {
                TryDelete(part);
                return $"speaker model {name}: {e.Message}";
            }
        }
        return null;
    }

    private static async Task DownloadAsync(Uri url, Stream destination, CancellationToken ct)
    {
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await using var body = await response.Content.ReadAsStreamAsync(ct);
        await body.CopyToAsync(destination, ct);
    }

    private static async Task<string> HashOfAsync(string path, CancellationToken ct)
    {
        await using var fs = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(fs, ct)).ToLowerInvariant();
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }
}
