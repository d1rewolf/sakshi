using System.Text.Json;
using Sakshi.Core.Obs;
using Sakshi.TestSupport;

namespace Sakshi.Core.Tests;

public class ObsClientTests
{
    private static (bool, object?) Ok(string type, JsonElement data) => (true, new { });

    [Fact]
    public void Auth_KnownVector()
    {
        // Produced with bin/sakshi's pipeline: printf '%s%s' ... | openssl dgst -sha256 -binary | base64 -w0 (twice).
        Assert.Equal("sQBlPUYd9mki/3XVFBp4Pt08FCMWdMVIqnFWdEitUME=",
            ObsAuth.Response("supersecret", "lM1GncleQOaCu9lT1yeUZhFYnqhsLLP1G5lAGo3ixaI=", "+IxH4CnCiqpX1rM9scsNynZzbOe4KhDeYcTNS3PDaeY="));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("supersecret")]
    public async Task Connect_WithAndWithoutPassword(string? password)
    {
        await using var server = new FakeObsServer(Ok, password);
        await using var client = await ObsClient.ConnectAsync("127.0.0.1", server.Port, password, TestContext.Current.CancellationToken);
        var r = await client.RequestAsync("GetVersion", null, TestContext.Current.CancellationToken);
        Assert.Equal(JsonValueKind.Object, r.ValueKind);
        Assert.Equal(["GetVersion"], server.Requests);
    }

    [Fact]
    public async Task Connect_WrongPassword_Throws()
    {
        await using var server = new FakeObsServer(Ok, "supersecret");
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ObsClient.ConnectAsync("127.0.0.1", server.Port, "wrong", TestContext.Current.CancellationToken));
        Assert.Contains("OBS rejected the WebSocket handshake (wrong password?)", e.Message);
        Assert.Contains("4009", e.Message);
    }

    [Fact]
    public async Task Connect_PasswordRequiredButMissing_Throws()
    {
        await using var server = new FakeObsServer(Ok, "supersecret");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ObsClient.ConnectAsync("127.0.0.1", server.Port, null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Request_SkipsUnrelatedMessages()
    {
        await using var server = new FakeObsServer((t, _) => (true, new { answer = 42 })) { SendNoiseBeforeResponse = true };
        await using var client = await ObsClient.ConnectAsync("127.0.0.1", server.Port, null, TestContext.Current.CancellationToken);
        var r = await client.RequestAsync("Anything", null, TestContext.Current.CancellationToken);
        Assert.Equal(42, r.GetProperty("answer").GetInt32());
        Assert.False(r.TryGetProperty("wrong", out _));
    }

    [Fact]
    public async Task Request_Failure_ThrowsWithComment()
    {
        await using var server = new FakeObsServer((t, _) => (false, "No such scene"));
        await using var client = await ObsClient.ConnectAsync("127.0.0.1", server.Port, null, TestContext.Current.CancellationToken);
        var e = await Assert.ThrowsAsync<ObsRequestException>(() => client.RequestAsync("SetScene", null, TestContext.Current.CancellationToken));
        Assert.Equal(600, e.Code);
        Assert.Equal("No such scene", e.Comment);
    }

    [Fact]
    public async Task Request_Timeout_Throws()
    {
        await using var server = new FakeObsServer(Ok);
        server.NeverAnswer.Add("Hang");
        await using var client = await ObsClient.ConnectAsync("127.0.0.1", server.Port, null, TestContext.Current.CancellationToken,
            requestTimeout: TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAsync<TimeoutException>(() => client.RequestAsync("Hang", null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Connect_Refused_ThrowsInvalidOperation()
    {
        int port;
        using (var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0))
        {
            l.Start();
            port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        }
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ObsClient.ConnectAsync("127.0.0.1", port, null, TestContext.Current.CancellationToken));
        Assert.StartsWith($"Could not connect to obs-websocket on 127.0.0.1:{port}:", e.Message);
    }

    [Fact]
    public async Task Request_ServerGoneBetweenRequests_ThrowsInvalidOperation()
    {
        var server = new FakeObsServer(Ok);
        await using var client = await ObsClient.ConnectAsync("127.0.0.1", server.Port, null, TestContext.Current.CancellationToken);
        await client.RequestAsync("One", null, TestContext.Current.CancellationToken);
        await server.DisposeAsync();
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.RequestAsync("Two", null, TestContext.Current.CancellationToken));
        Assert.StartsWith("OBS connection lost", e.Message);
    }

    [Fact]
    public async Task Request_ServerCloseDuringPendingRequest_FailsFast()
    {
        await using var server = new FakeObsServer(Ok);
        server.CloseOnRequest.Add("Bye");
        await using var client = await ObsClient.ConnectAsync("127.0.0.1", server.Port, null, TestContext.Current.CancellationToken,
            requestTimeout: TimeSpan.FromSeconds(10));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.RequestAsync("Bye", null, TestContext.Current.CancellationToken));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"took {sw.Elapsed}");
        Assert.StartsWith("OBS connection lost", e.Message);
    }

    [Fact]
    public async Task Request_AfterTimeout_ClientIsUnusable()
    {
        await using var server = new FakeObsServer(Ok);
        server.NeverAnswer.Add("Hang");
        await using var client = await ObsClient.ConnectAsync("127.0.0.1", server.Port, null, TestContext.Current.CancellationToken,
            requestTimeout: TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAsync<TimeoutException>(() => client.RequestAsync("Hang", null, TestContext.Current.CancellationToken));
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.RequestAsync("Other", null, TestContext.Current.CancellationToken));
        Assert.Contains("unusable after a timeout", e.Message);
    }

    [Fact]
    public async Task Request_LargeFragmentedResponse_IsReassembled()
    {
        var big = new string('x', 100_000);
        await using var server = new FakeObsServer((t, _) => (true, new { blob = big })) { FragmentSize = 7000 };
        await using var client = await ObsClient.ConnectAsync("127.0.0.1", server.Port, null, TestContext.Current.CancellationToken);
        var r = await client.RequestAsync("Big", null, TestContext.Current.CancellationToken);
        Assert.Equal(big, r.GetProperty("blob").GetString());
    }

    [Fact]
    public async Task Request_DataIsSent()
    {
        string? seen = null;
        await using var server = new FakeObsServer((t, d) => { seen = d.GetRawText(); return (true, new { }); });
        await using var client = await ObsClient.ConnectAsync("127.0.0.1", server.Port, null, TestContext.Current.CancellationToken);
        await client.RequestAsync("CreateSceneCollection", new { sceneCollectionName = "x" }, TestContext.Current.CancellationToken);
        Assert.Equal("""{"sceneCollectionName":"x"}""", seen);
    }

    [Fact]
    public async Task Dispose_ClosesCleanly_ThenStoreContinues()
    {
        var video = Path.Combine(Path.GetTempPath(), "rec", "2026-09-24 08-02-10.mp4");
        await using var server = new FakeObsServer((t, _) => t switch
        {
            "GetRecordStatus" => (true, new { outputActive = true }),
            "StopRecord" => (true, new { outputPath = video }),
            "GetRecordDirectory" => (true, new { recordDirectory = "/rec" }),
            _ => (true, new { }),
        });
        string? path;
        var client = await ObsClient.ConnectAsync("127.0.0.1", server.Port, null, TestContext.Current.CancellationToken);
        IObs obs = new ObsRecording(client);
        Assert.True(await obs.IsRecordingAsync(TestContext.Current.CancellationToken));
        path = await obs.StopRecordAsync(TestContext.Current.CancellationToken);
        Assert.Equal("/rec", await obs.GetRecordDirectoryAsync(TestContext.Current.CancellationToken));
        await client.DisposeAsync();
        await client.DisposeAsync();
        Assert.Equal(video, path);
    }

    [Fact]
    public async Task Dispose_AfterServerGone_DoesNotThrow()
    {
        var server = new FakeObsServer(Ok) { DisconnectAfter = "Ping" };
        var client = await ObsClient.ConnectAsync("127.0.0.1", server.Port, null, TestContext.Current.CancellationToken);
        await client.RequestAsync("Ping", null, TestContext.Current.CancellationToken);
        await server.DisposeAsync();
        await client.DisposeAsync();
    }

    [Fact]
    public async Task ObsRecording_OwnsItsClient_DisposingItClosesTheConnection()
    {
        await using var server = new FakeObsServer(Ok);
        var client = await ObsClient.ConnectAsync("127.0.0.1", server.Port, null, TestContext.Current.CancellationToken);
        IObs obs = new ObsRecording(client);

        await ((IAsyncDisposable)obs).DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.RequestAsync("Ping", null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task StopRecord_EmptyPath_IsNull()
    {
        await using var server = new FakeObsServer((t, _) => (true, new { outputPath = "" }));
        await using var client = await ObsClient.ConnectAsync("127.0.0.1", server.Port, null, TestContext.Current.CancellationToken);
        Assert.Null(await new ObsRecording(client).StopRecordAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void ObsPassword_Read()
    {
        var dir = Path.Combine(Path.GetTempPath(), "obspw-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.Null(ObsPassword.Read(dir));
            var cfg = Path.Combine(dir, "plugin_config", "obs-websocket");
            Directory.CreateDirectory(cfg);
            var file = Path.Combine(cfg, "config.json");

            File.WriteAllText(file, """{"server_password":"pw"}""");
            Assert.Equal("pw", ObsPassword.Read(dir));
            File.WriteAllText(file, """{"server_password":""}""");
            Assert.Null(ObsPassword.Read(dir));
            File.WriteAllText(file, """{"other":1}""");
            Assert.Null(ObsPassword.Read(dir));
            File.WriteAllText(file, "{not json");
            Assert.Null(ObsPassword.Read(dir));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }
}
