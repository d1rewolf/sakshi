using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Sakshi.Core.Obs;

public sealed class ObsClient : IAsyncDisposable
{
    private static readonly TimeSpan DefaultHandshakeTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(15);

    private readonly ClientWebSocket _ws;
    private readonly TimeSpan _requestTimeout;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly JsonElement EmptyObject = JsonDocument.Parse("{}").RootElement;

    private int _nextId;
    private bool _broken;

    private ObsClient(ClientWebSocket ws, TimeSpan requestTimeout)
    {
        _ws = ws;
        _requestTimeout = requestTimeout;
    }

    public static async Task<ObsClient> ConnectAsync(
        string host, int port, string? password, CancellationToken ct,
        TimeSpan? handshakeTimeout = null, TimeSpan? requestTimeout = null)
    {
        var hs = handshakeTimeout ?? DefaultHandshakeTimeout;
        var ws = new ClientWebSocket();
        try
        {
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                cts.CancelAfter(hs);
                try
                {
                    await ws.ConnectAsync(new Uri($"ws://{host}:{port}"), cts.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new TimeoutException($"Timed out connecting to obs-websocket on {host}:{port}");
                }
                catch (Exception e) when (e is WebSocketException or HttpRequestException or System.Net.Sockets.SocketException or IOException)
                {
                    var reason = e.InnerException?.Message ?? e.Message;
                    throw new InvalidOperationException($"Could not connect to obs-websocket on {host}:{port}: {reason}", e);
                }
            }

            var hello = await ReceiveAsync(ws, hs, ct, "Hello from obs-websocket");
            if (Op(hello) != 0)
                throw new InvalidOperationException($"Unexpected first message from OBS: {hello.GetRawText()}");

            var d = new Dictionary<string, object?> { ["rpcVersion"] = 1, ["eventSubscriptions"] = 0 };
            if (hello.GetProperty("d").TryGetProperty("authentication", out var auth)
                && auth.ValueKind == JsonValueKind.Object)
            {
                if (string.IsNullOrEmpty(password))
                    throw new InvalidOperationException("OBS requires a WebSocket password but none was found");
                d["authentication"] = ObsAuth.Response(
                    password, auth.GetProperty("salt").GetString()!, auth.GetProperty("challenge").GetString()!);
            }

            await SendAsync(ws, new { op = 1, d }, ct);
            var identified = await ReceiveAsync(ws, hs, ct, "Identify answer from OBS", isIdentify: true);
            if (Op(identified) != 2)
                throw new InvalidOperationException("OBS rejected the WebSocket handshake (wrong password?)");

            return new ObsClient(ws, requestTimeout ?? DefaultRequestTimeout);
        }
        catch
        {
            ws.Dispose();
            throw;
        }
    }

    public async Task<JsonElement> RequestAsync(string requestType, object? requestData = null, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_broken)
                throw new InvalidOperationException("OBS connection is unusable after a timeout; reconnect");
            try
            {
                return await RequestCoreAsync(requestType, requestData, ct);
            }
            catch (ObsRequestException)
            {
                throw;
            }
            catch
            {
                _broken = true;
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<JsonElement> RequestCoreAsync(string requestType, object? requestData, CancellationToken ct)
    {
        {
            var id = "r" + ++_nextId;
            await SendAsync(_ws, new { op = 6, d = new { requestType, requestId = id, requestData = requestData ?? new { } } }, ct);

            var deadline = DateTime.UtcNow + _requestTimeout;
            while (true)
            {
                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero)
                    throw new TimeoutException($"OBS request {requestType} timed out");
                var msg = await ReceiveAsync(_ws, remaining, ct, $"OBS request {requestType}");
                if (Op(msg) != 7) continue;
                var d = msg.GetProperty("d");
                if (!d.TryGetProperty("requestId", out var rid) || rid.GetString() != id) continue;

                var status = d.GetProperty("requestStatus");
                if (status.GetProperty("result").GetBoolean())
                    return d.TryGetProperty("responseData", out var rd) ? rd.Clone() : EmptyObject;
                throw new ObsRequestException(
                    status.TryGetProperty("code", out var c) && c.TryGetInt32(out var code) ? code : 0,
                    status.TryGetProperty("comment", out var cm) ? cm.GetString() : null);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, cts.Token);
            }
        }
        catch
        {
            // The server may already be gone; closing must never fail the caller.
        }
        finally
        {
            _ws.Dispose();
            _gate.Dispose();
        }
    }

    private static int Op(JsonElement msg) =>
        msg.ValueKind == JsonValueKind.Object && msg.TryGetProperty("op", out var o) && o.TryGetInt32(out var n) ? n : -1;

    private static async Task SendAsync(ClientWebSocket ws, object message, CancellationToken ct)
    {
        try
        {
            await ws.SendAsync(JsonSerializer.SerializeToUtf8Bytes(message), WebSocketMessageType.Text, true, ct);
        }
        catch (Exception e) when (e is WebSocketException or ObjectDisposedException or IOException)
        {
            throw new InvalidOperationException($"OBS connection lost: {e.InnerException?.Message ?? e.Message}", e);
        }
    }

    private static async Task<JsonElement> ReceiveAsync(ClientWebSocket ws, TimeSpan timeout, CancellationToken ct, string what, bool isIdentify = false)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            var buf = new byte[8192];
            using var ms = new MemoryStream();
            while (true)
            {
                var r = await ws.ReceiveAsync(buf, cts.Token);
                if (r.MessageType == WebSocketMessageType.Close)
                {
                    var detail = $"close {(int?)r.CloseStatus}" +
                                 (string.IsNullOrEmpty(r.CloseStatusDescription) ? "" : $": {r.CloseStatusDescription}");
                    throw new InvalidOperationException(isIdentify
                        ? $"OBS rejected the WebSocket handshake (wrong password?) ({detail})"
                        : $"OBS connection lost: server closed the connection ({detail}) while waiting for: {what}");
                }
                ms.Write(buf, 0, r.Count);
                if (!r.EndOfMessage) continue;
                if (r.MessageType != WebSocketMessageType.Text) { ms.SetLength(0); continue; }
                using var doc = JsonDocument.Parse(ms.GetBuffer().AsMemory(0, (int)ms.Length));
                return doc.RootElement.Clone();
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"Timed out waiting for: {what}");
        }
        catch (Exception e) when (e is WebSocketException or ObjectDisposedException or IOException)
        {
            throw new InvalidOperationException($"OBS connection lost: {e.InnerException?.Message ?? e.Message}", e);
        }
    }
}
