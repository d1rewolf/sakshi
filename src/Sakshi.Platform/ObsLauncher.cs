using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Sakshi.Core.Config;
using Sakshi.Core.Obs;
using Sakshi.Core.Paths;
using Sakshi.Core.Ports;

namespace Sakshi.Platform;

public sealed class ObsLauncher(
    SakshiConfig config, SakshiPaths paths, IObsInstall install, IProcessRunner runner, ILog log,
    Func<string, int, CancellationToken, Task<bool>>? portOpen = null,
    Func<TimeSpan, CancellationToken, Task>? delay = null) : IObsLauncher
{
    private readonly Func<string, int, CancellationToken, Task<bool>> _portOpen = portOpen ?? TcpOpen;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;

    private string ConfigFile => ObsWebSocketConfig.FileIn(paths.ObsConfigDir);

    public string? Password() =>
        config.ObsWsPassword.Length > 0 ? config.ObsWsPassword : ObsPassword.Read(paths.ObsConfigDir);

    public async Task EnsureRunningAsync(CancellationToken ct)
    {
        var running = runner.IsRunning(install.ProcessName);
        EnsureServerEnabledInConfig(running);
        if (!running)
        {
            log.Info("launching OBS");
            var extra = config.ObsLaunchArgs.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            var launch = await install.LaunchAsync(extra, ct).ConfigureAwait(false);
            runner.StartDetached(launch.File, launch.Args, launch.WorkingDirectory);
        }
        for (var i = 0; i < 60; i++)
        {
            if (await _portOpen(config.ObsWsHost, config.ObsWsPort, ct).ConfigureAwait(false)) return;
            await _delay(TimeSpan.FromMilliseconds(500), ct).ConfigureAwait(false);
        }
        throw new InvalidOperationException(NotAnswering(config.ObsWsPort));
    }

    /// <summary>
    /// OBS can stop at a question before it opens the port: after it was closed unexpectedly (OBS 32 and later no
    /// longer skip that one), an update, or its first-run checks. Say where to look.
    /// </summary>
    internal static string NotAnswering(int port) =>
        $"OBS started but did not answer on port {port}. It may be waiting for you to answer a question in an OBS window " +
        "(after it closed unexpectedly, an update, or its first start): answer it, then try again.";

    /// <summary>
    /// Before OBS starts, makes sure its remote control is on and, unless the user opted out, asks for a password: OBS
    /// cannot be told to listen on this computer only, so without one anyone on the network could control it.
    /// While OBS runs it owns the file, so nothing is written then.
    /// </summary>
    private void EnsureServerEnabledInConfig(bool obsRunning)
    {
        var file = ConfigFile;
        var exists = File.Exists(file);
        if (!exists && obsRunning) return;   // OBS's own defaults apply: the server off is reported on connecting
        var root = exists ? ObsWebSocketConfig.Parse(file, File.ReadAllText(file)) : new JsonObject();
        var serverOff = !ObsWebSocketConfig.ServerEnabled(root);
        // A new file gets its password from Sakshi too, so it never depends on what OBS does at its first start.
        var passwordOff = config.ObsWsRequirePassword && (!exists || !ObsWebSocketConfig.AuthRequired(root));
        if (!serverOff && !passwordOff) return;
        if (obsRunning)
        {
            if (serverOff) throw new InvalidOperationException(ObsWebSocketConfig.ServerOffMessage);
            log.Info("obs-websocket asks for no password; Sakshi turns it on the next time it starts OBS");
            return;
        }
        if (serverOff)
        {
            root["server_enabled"] = true;
            root["server_port"] = config.ObsWsPort;
        }
        if (passwordOff)
        {
            root["auth_required"] = true;
            if (root["server_password"] is not JsonValue p || !p.TryGetValue<string>(out var pw) || pw.Length == 0)
                root["server_password"] = config.ObsWsPassword.Length > 0 ? config.ObsWsPassword : RandomNumberGenerator.GetString("abcdefghijkmnopqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789", 20);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var tmp = file + ".sakshi.tmp";
        try
        {
            File.WriteAllText(tmp, root.ToJsonString(new() { WriteIndented = true }));
            File.Move(tmp, file, overwrite: true);
        }
        finally
        {
            try { File.Delete(tmp); } catch (IOException) { }
        }
        log.Info($"obs-websocket in {file}:{(serverOff ? " server enabled" : "")}{(passwordOff ? " password turned on" : "")}");
    }

    private static async Task<bool> TcpOpen(string host, int port, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMilliseconds(500));
            using var c = new TcpClient();
            await c.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
            return c.Connected;
        }
        catch (Exception) when (!ct.IsCancellationRequested) { return false; }
    }
}
