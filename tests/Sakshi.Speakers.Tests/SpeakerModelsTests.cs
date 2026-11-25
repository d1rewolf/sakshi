using System.Security.Cryptography;
using Sakshi.Speakers;

namespace Sakshi.Speakers.Tests;

public sealed class SpeakerModelsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sakshi-models-" + Guid.NewGuid().ToString("N"));
    public SpeakerModelsTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void InGivesTheTwoPaths()
    {
        var p = SpeakerModels.In(_dir);
        Assert.Equal(Path.Combine(_dir, "speakers", "pyannote-segmentation-3-0.onnx"), p.Segmentation);
        Assert.Equal(Path.Combine(_dir, "speakers", "wespeaker_en_voxceleb_resnet34_LM.onnx"), p.Embedding);
    }

    [Fact]
    public async Task EnsureDownloadsBothAndChecksHashes()
    {
        var calls = 0;
        var reason = await SpeakerModels.EnsureAsync(_dir, TextWriter.Null, CancellationToken.None, async (uri, s, ct) =>
        {
            calls++;
            await s.WriteAsync(new byte[] { 1, 2, 3 }, ct);
        });
        Assert.NotNull(reason);
        Assert.Contains("SHA-256", reason);
        Assert.Equal(1, calls);
        Assert.Empty(Directory.GetFiles(Path.Combine(_dir, "speakers")));
    }

    [Fact]
    public async Task EnsureKeepsFilesWhoseHashMatches()
    {
        var bytes = new byte[] { 9, 8, 7 };
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var table = new Dictionary<string, string> {
            ["pyannote-segmentation-3-0.onnx"] = hash,
            ["wespeaker_en_voxceleb_resnet34_LM.onnx"] = hash };
        var output = new StringWriter();
        var reason = await SpeakerModels.EnsureAsync(_dir, output, CancellationToken.None,
            async (uri, s, ct) => await s.WriteAsync(bytes, ct), table);
        Assert.Null(reason);
        var p = SpeakerModels.In(_dir);
        Assert.True(File.Exists(p.Segmentation));
        Assert.True(File.Exists(p.Embedding));
        Assert.Equal(2, Directory.GetFiles(Path.Combine(_dir, "speakers")).Length);
        Assert.Contains("==> Downloading speaker model pyannote-segmentation-3-0.onnx to", output.ToString());
    }

    private static Dictionary<string, string> TableFor(byte[] bytes)
    {
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return new Dictionary<string, string> {
            ["pyannote-segmentation-3-0.onnx"] = hash,
            ["wespeaker_en_voxceleb_resnet34_LM.onnx"] = hash };
    }

    [Fact]
    public async Task EnsureReturnsNullWhenFilesExist()
    {
        var bytes = new byte[] { 1, 2 };
        var p = SpeakerModels.In(_dir);
        Directory.CreateDirectory(Path.GetDirectoryName(p.Segmentation)!);
        File.WriteAllBytes(p.Segmentation, bytes);
        File.WriteAllBytes(p.Embedding, bytes);
        var reason = await SpeakerModels.EnsureAsync(_dir, TextWriter.Null, CancellationToken.None,
            (u, s, c) => throw new InvalidOperationException("must not download"), TableFor(bytes));
        Assert.Null(reason);
    }

    [Fact]
    public async Task ExistingFileWithWrongHashIsReplaced()
    {
        var good = new byte[] { 5, 5, 5 };
        var p = SpeakerModels.In(_dir);
        Directory.CreateDirectory(Path.GetDirectoryName(p.Segmentation)!);
        File.WriteAllBytes(p.Segmentation, new byte[] { 0 });
        File.WriteAllBytes(p.Embedding, good);
        var calls = 0;
        var reason = await SpeakerModels.EnsureAsync(_dir, TextWriter.Null, CancellationToken.None,
            async (u, s, c) => { calls++; await s.WriteAsync(good, c); }, TableFor(good));
        Assert.Null(reason);
        Assert.Equal(1, calls);
        Assert.Equal(good, File.ReadAllBytes(p.Segmentation));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(p.Segmentation)!, "*.part"));
    }

    [Fact]
    public async Task EnsureReportsDownloadFailure()
    {
        var reason = await SpeakerModels.EnsureAsync(_dir, TextWriter.Null, CancellationToken.None,
            (u, s, c) => throw new HttpRequestException("offline"));
        Assert.NotNull(reason);
        Assert.Contains("offline", reason);
        Assert.Empty(Directory.GetFiles(Path.Combine(_dir, "speakers")));
    }

    [Fact]
    public async Task CancellationDeletesPartAndPropagates()
    {
        using var cts = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SpeakerModels.EnsureAsync(_dir, TextWriter.Null, cts.Token,
            async (u, s, c) => { await s.WriteAsync(new byte[] { 1 }, c); cts.Cancel(); c.ThrowIfCancellationRequested(); }));
        Assert.Empty(Directory.GetFiles(Path.Combine(_dir, "speakers")));
    }
}
