using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sakshi.App.Tray;

/// <summary>
/// The tray's single-instance lock and the Unix domain socket its CLI commands arrive on. The lock
/// file (<c>tray.lock</c> in the state directory) is held open with <see cref="FileShare.None"/> for
/// the tray's whole life; only the holder may delete a stale socket file.
/// </summary>
public sealed class TrayInstance : IAsyncDisposable
{
    // sockaddr_un.sun_path is 108 bytes on Linux and 104 on macOS, including the terminating NUL.
    private const int MaxSocketPathBytes = 103;

    private FileStream? _lock;

    public string SocketPath { get; }

    private TrayInstance(FileStream lockFile, string socketPath)
    {
        _lock = lockFile;
        SocketPath = socketPath;
    }

    /// <summary>
    /// The socket path for <paramref name="stateDir"/>: <c>stateDir/tray.sock</c>, unless that is too long
    /// for a socket address; then <c>$XDG_RUNTIME_DIR/sakshi-tray.sock</c> (Linux) or a private
    /// <c>&lt;privateTempRoot&gt;/sakshi-&lt;user&gt;/tray.sock</c> (root defaults to /tmp), flagged
    /// <c>IsPrivateDir</c> so <see cref="TryAcquire"/> locks it down. Pure: nothing is created here.
    /// </summary>
    public static (string Path, bool IsPrivateDir) SocketPathFor(string stateDir, Func<string, string?> env,
        string? privateTempRoot = null)
    {
        var preferred = Path.Combine(stateDir, "tray.sock");
        if (Encoding.UTF8.GetByteCount(preferred) <= MaxSocketPathBytes) return (preferred, false);
        if (OperatingSystem.IsLinux() && env("XDG_RUNTIME_DIR") is { Length: > 0 } run && Path.IsPathRooted(run))
        {
            var viaRuntime = Path.Combine(run, "sakshi-tray.sock");
            if (Encoding.UTF8.GetByteCount(viaRuntime) <= MaxSocketPathBytes) return (viaRuntime, false);
        }
        var user = new string(Environment.UserName.Where(char.IsAsciiLetterOrDigit).ToArray());
        var root = privateTempRoot ?? (OperatingSystem.IsWindows() ? Path.GetTempPath() : "/tmp");
        return (Path.Combine(root, "sakshi-" + user, "tray.sock"), true);
    }

    /// <summary>
    /// Takes the tray lock. Null means another tray holds it. On success a stale socket file is removed
    /// (safe: we hold the lock, so no other tray is listening on it). <paramref name="env"/> and
    /// <paramref name="privateTempRoot"/> default to the process environment and /tmp; tests inject theirs.
    /// </summary>
    /// <exception cref="InvalidOperationException">The private socket directory cannot be made ours (0700).</exception>
    public static TrayInstance? TryAcquire(string stateDir, Func<string, string?>? env = null, string? privateTempRoot = null)
    {
        Directory.CreateDirectory(stateDir);
        FileStream lockFile;
        try
        {
            lockFile = new FileStream(Path.Combine(stateDir, "tray.lock"), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            return null;
        }
        try
        {
            var (socket, isPrivate) = SocketPathFor(stateDir, env ?? Environment.GetEnvironmentVariable, privateTempRoot);
            var dir = Path.GetDirectoryName(socket)!;
            try
            {
                Directory.CreateDirectory(dir);
                // Throws when the directory belongs to someone else, so a squatted one fails closed.
                if (isPrivate && !OperatingSystem.IsWindows())
                    File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            catch (Exception e) when (isPrivate && e is IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException(
                    $"private socket directory {dir} is not ours; refusing to use it ({e.Message})", e);
            }
            File.Delete(socket);
            return new TrayInstance(lockFile, socket);
        }
        catch
        {
            lockFile.Dispose();
            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _lock, null) is { } l)
        {
            try { File.Delete(SocketPath); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            l.Dispose();
        }
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// The CLI-to-tray protocol: the client sends one JSON line <c>{"args":[...]}</c> (UTF-8, '\n'), the
/// server answers one JSON line <c>{"exit":n,"stdout":"…","stderr":"…"}</c> and closes.
/// </summary>
public static class TrayIpc
{
    private const int MaxRequestBytes = 1 << 20;   // 1 MiB
    private const int MaxReplyBytes = 16 << 20;    // 16 MiB
    // Plain UTF-8 on the wire (no \uXXXX escapes), so the size caps count real bytes.
    private static readonly JsonSerializerOptions Json = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RequestReadTimeout = TimeSpan.FromSeconds(10);

    private sealed record Request([property: JsonPropertyName("args")] string[]? Args);

    private sealed record Reply(
        [property: JsonPropertyName("exit")] int Exit,
        [property: JsonPropertyName("stdout")] string StdOut,
        [property: JsonPropertyName("stderr")] string StdErr);

    /// <summary>
    /// Listens on <paramref name="socketPath"/> until <paramref name="ct"/> is cancelled (then returns
    /// normally, after the connections in flight have finished). Each connection runs concurrently.
    /// The caller must hold the <see cref="TrayInstance"/> lock, so any file at the path is stale.
    /// </summary>
    public static async Task ServeAsync(string socketPath,
        Func<IReadOnlyList<string>, CancellationToken, Task<CommandResult>> handler, CancellationToken ct)
    {
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(socketPath));
        // Between bind and listen nobody can connect, so the mode is fixed before the first client.
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(socketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        listener.Listen(32);

        var connections = new ConcurrentDictionary<Task, byte>();
        try
        {
            while (true)
            {
                Socket client;
                try { client = await listener.AcceptAsync(ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                var task = Task.Run(() => HandleAsync(client, handler, ct), CancellationToken.None);
                connections[task] = 0;
                _ = task.ContinueWith(t => connections.TryRemove(t, out _), CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        finally
        {
            listener.Dispose();
            await Task.WhenAll(connections.Keys).ConfigureAwait(false);
        }
    }

    private static async Task HandleAsync(Socket client,
        Func<IReadOnlyList<string>, CancellationToken, Task<CommandResult>> handler, CancellationToken ct)
    {
        using (client)
        {
            try
            {
                await using var stream = new NetworkStream(client, ownsSocket: false);
                Reply reply;
                string? line;
                using (var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    readCts.CancelAfter(RequestReadTimeout);
                    try { line = await ReadLineAsync(stream, MaxRequestBytes, readCts.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested) { line = null; }
                }
                if (ParseArgs(line) is not { } args) reply = new Reply(2, "", "bad request");
                else
                {
                    try
                    {
                        var r = await handler(args, ct).ConfigureAwait(false);
                        reply = new Reply(r.ExitCode, r.StdOut, r.StdErr);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                    catch (Exception e) { reply = new Reply(1, "", e.Message); }
                }
                var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(reply, Json) + "\n");
                await stream.WriteAsync(bytes, CancellationToken.None).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }   // quit: not a fault
            catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException) { }
        }
    }

    private static string[]? ParseArgs(string? line)
    {
        if (line is null) return null;
        try
        {
            var req = JsonSerializer.Deserialize<Request>(line);
            return req?.Args is { Length: > 0 } a && a.All(x => x is not null) ? a : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// One '\n'-terminated UTF-8 line of at most <paramref name="max"/> bytes; null at EOF before a
    /// newline or when it is too long. Reads in blocks; anything after the newline is dropped (one
    /// line per connection).
    /// </summary>
    private static async Task<string?> ReadLineAsync(Stream stream, int max, CancellationToken ct)
    {
        using var line = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var n = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (n == 0) return null;
            var nl = Array.IndexOf(buffer, (byte)'\n', 0, n);
            var take = nl >= 0 ? nl : n;
            if (line.Length + take > max) return null;
            line.Write(buffer, 0, take);
            if (nl >= 0) return Encoding.UTF8.GetString(line.GetBuffer(), 0, (int)line.Length);
        }
    }

    /// <summary>
    /// Sends <paramref name="args"/> to the tray and waits up to <paramref name="timeout"/> for the reply.
    /// Null means no tray is listening (nothing at the path, a stale file, or no connection within 2 s).
    /// A tray that connects but does not answer in time, or hangs up, gives an exit-1 result.
    /// </summary>
    public static async Task<CommandResult?> SendAsync(string socketPath, IReadOnlyList<string> args,
        TimeSpan timeout, CancellationToken ct)
    {
        using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            connectCts.CancelAfter(ConnectTimeout);
            try { await client.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), connectCts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
            catch (Exception e) when (e is SocketException or IOException or ArgumentException) { return null; }
        }

        using var replyCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        replyCts.CancelAfter(timeout);
        try
        {
            await using var stream = new NetworkStream(client, ownsSocket: false);
            var request = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new Request(args.ToArray()), Json) + "\n");
            await stream.WriteAsync(request, replyCts.Token).ConfigureAwait(false);
            var line = await ReadLineAsync(stream, MaxReplyBytes, replyCts.Token).ConfigureAwait(false);
            if (line is null)
                return new CommandResult(1, "", "sakshi: the tray closed the connection without answering (see sakshi.log).\n");
            var reply = JsonSerializer.Deserialize<Reply>(line);
            return reply is null ? new CommandResult(1, "", "sakshi: the tray sent an unreadable answer.\n")
                : new CommandResult(reply.Exit, reply.StdOut ?? "", reply.StdErr ?? "");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new CommandResult(1, "", "sakshi: the tray did not answer in time (see sakshi.log).\n");
        }
        catch (Exception e) when (e is IOException or SocketException or JsonException)
        {
            return new CommandResult(1, "", $"sakshi: lost the connection to the tray ({e.Message}).\n");
        }
    }
}
