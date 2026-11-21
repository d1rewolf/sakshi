using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Sakshi.TestSupport;

/// <summary>
/// In-process obs-websocket v5 server on 127.0.0.1 (free port): a TcpListener, a hand-rolled HTTP upgrade and
/// WebSocket.CreateFromStream. Never talks to a real OBS.
/// </summary>
public sealed class FakeObsServer : IAsyncDisposable
{
    private const string Guid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
    private const string Salt = "lM1GncleQOaCu9lT1yeUZhFYnqhsLLP1G5lAGo3ixaI=";
    private const string Challenge = "+IxH4CnCiqpX1rM9scsNynZzbOe4KhDeYcTNS3PDaeY=";

    private readonly Func<string, JsonElement, (bool ok, object? data)> _handler;
    private readonly string? _password;
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();
    private readonly List<string> _requests = [];
    private readonly Task _acceptLoop;

    public FakeObsServer(Func<string, JsonElement, (bool ok, object? data)> handler, string? password = null)
    {
        _handler = handler;
        _password = password;
        _listener.Start();
        _acceptLoop = Task.Run(AcceptLoop);
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>Request types received, in order.</summary>
    public IReadOnlyList<string> Requests
    {
        get { lock (_requests) return [.. _requests]; }
    }

    /// <summary>Send an event and a response for another requestId before each real response.</summary>
    public bool SendNoiseBeforeResponse { get; set; }

    /// <summary>Request types that are received but never answered.</summary>
    public HashSet<string> NeverAnswer { get; } = [];

    /// <summary>When set, the server closes the socket right after answering this request type.</summary>
    public string? DisconnectAfter { get; set; }

    /// <summary>When set, responses are sent in fragments of this many bytes.</summary>
    public int? FragmentSize { get; set; }

    /// <summary>Request types answered by a graceful Close frame instead of a response.</summary>
    public HashSet<string> CloseOnRequest { get; } = [];

    private async Task AcceptLoop()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var tcp = await _listener.AcceptTcpClientAsync(_cts.Token);
                _ = Task.Run(() => Serve(tcp));
            }
        }
        catch (Exception) when (_cts.IsCancellationRequested) { }
    }

    private async Task Serve(TcpClient tcp)
    {
        using var _ = tcp;
        try
        {
            var stream = tcp.GetStream();
            var key = await Handshake(stream);
            using var ws = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions { IsServer = true });

            object hello = _password is null
                ? new { op = 0, d = new { rpcVersion = 1 } }
                : new { op = 0, d = new { rpcVersion = 1, authentication = new { challenge = Challenge, salt = Salt } } };
            await Send(ws, hello);

            var identify = await Receive(ws);
            if (identify is null) return;
            var d = identify.Value.GetProperty("d");
            if (_password is not null)
            {
                var expected = Expected(_password);
                if (!d.TryGetProperty("authentication", out var a) || a.GetString() != expected)
                {
                    await ws.CloseAsync((WebSocketCloseStatus)4009, "Authentication failed", _cts.Token);
                    return;
                }
            }
            await Send(ws, new { op = 2, d = new { negotiatedRpcVersion = 1 } });

            while (await Receive(ws) is { } msg)
            {
                if (msg.GetProperty("op").GetInt32() != 6) continue;
                var rd = msg.GetProperty("d");
                var type = rd.GetProperty("requestType").GetString()!;
                var id = rd.GetProperty("requestId").GetString()!;
                var data = rd.TryGetProperty("requestData", out var rq) ? rq : default;
                lock (_requests) _requests.Add(type);
                if (CloseOnRequest.Contains(type))
                {
                    await ws.CloseAsync(WebSocketCloseStatus.EndpointUnavailable, "going away", _cts.Token);
                    return;
                }
                if (NeverAnswer.Contains(type)) continue;

                if (SendNoiseBeforeResponse)
                {
                    await Send(ws, new { op = 5, d = new { eventType = "Noise", eventIntent = 0 } });
                    await Send(ws, new
                    {
                        op = 7,
                        d = new { requestType = type, requestId = "other", requestStatus = new { result = true, code = 100 }, responseData = new { wrong = true } },
                    });
                }

                var (ok, result) = _handler(type, data);
                if (ok)
                    await Send(ws, new { op = 7, d = new { requestType = type, requestId = id, requestStatus = new { result = true, code = 100 }, responseData = result ?? new { } } });
                else
                    await Send(ws, new { op = 7, d = new { requestType = type, requestId = id, requestStatus = new { result = false, code = result is ValueTuple<int, string> e ? e.Item1 : 600, comment = result is ValueTuple<int, string> e2 ? e2.Item2 : result as string } } });

                if (DisconnectAfter == type)
                {
                    tcp.Client.Close();
                    return;
                }
            }
        }
        catch (Exception) { /* client went away or server disposing */ }
    }

    // Independent of production code on purpose: the fake must not share a bug with ObsAuth.
    private static string Expected(string password)
    {
        var secret = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(password + Salt)));
        return Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(secret + Challenge)));
    }

    private static async Task<string> Handshake(NetworkStream stream)
    {
        var sb = new StringBuilder();
        var one = new byte[1];
        while (!sb.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            if (await stream.ReadAsync(one) == 0) throw new IOException("closed during handshake");
            sb.Append((char)one[0]);
        }
        var line = sb.ToString().Split("\r\n").First(l => l.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase));
        var key = line[(line.IndexOf(':') + 1)..].Trim();
        var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + Guid)));
        var resp = "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
                   $"Sec-WebSocket-Accept: {accept}\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(resp));
        return key;
    }

    private async Task Send(WebSocket ws, object message)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message);
        var size = FragmentSize ?? bytes.Length;
        for (var off = 0; off < bytes.Length; off += size)
        {
            var n = Math.Min(size, bytes.Length - off);
            await ws.SendAsync(bytes.AsMemory(off, n), WebSocketMessageType.Text, off + n >= bytes.Length, _cts.Token);
        }
    }

    private async Task<JsonElement?> Receive(WebSocket ws)
    {
        var buf = new byte[8192];
        using var ms = new MemoryStream();
        while (true)
        {
            var r = await ws.ReceiveAsync(buf, _cts.Token);
            if (r.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buf, 0, r.Count);
            if (r.EndOfMessage)
                return JsonDocument.Parse(Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length)).RootElement;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _listener.Stop();
        try { await _acceptLoop; } catch (Exception) { }
        _cts.Dispose();
    }
}
