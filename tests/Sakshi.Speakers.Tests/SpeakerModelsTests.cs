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

    [Fact]
    public async Task EnsureReturnsNullWhenFilesExist()
    {
        var p = SpeakerModels.In(_dir);
        Directory.CreateDirectory(Path.GetDirectoryName(p.Segmentation)!);
        File.WriteAllBytes(p.Segmentation, new byte[] { 1 });
        File.WriteAllBytes(p.Embedding, new byte[] { 1 });
        var reason = await SpeakerModels.EnsureAsync(_dir, TextWriter.Null, CancellationToken.None,
            (u, s, c) => throw new InvalidOperationException("must not download"));
        Assert.Null(reason);
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
