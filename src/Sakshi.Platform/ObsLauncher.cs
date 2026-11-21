using System.Net.Sockets;
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
        throw new InvalidOperationException($"OBS started but its WebSocket port {config.ObsWsPort} never opened");
    }

    private void EnsureServerEnabledInConfig(bool obsRunning)
    {
        var file = ConfigFile;
        JsonObject root;
        if (File.Exists(file))
        {
            root = ObsWebSocketConfig.Parse(file, File.ReadAllText(file));
            if (ObsWebSocketConfig.ServerEnabled(root)) return;
        }
        else
        {
            // Nothing to edit; OBS owns the file while it runs.
            if (obsRunning) return;
            root = new JsonObject { ["auth_required"] = false };
        }
        if (obsRunning)
            throw new InvalidOperationException(ObsWebSocketConfig.ServerOffMessage);
        root["server_enabled"] = true;
        root["server_port"] = config.ObsWsPort;
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
        log.Info($"enabled obs-websocket server in {file}");
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
