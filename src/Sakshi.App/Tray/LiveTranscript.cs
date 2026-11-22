using System.Diagnostics;
using System.Text.Json;

namespace Sakshi.App.Tray;

/// <summary>One line of the live transcript: when it was said (from the start of the recording) and what.</summary>
internal sealed record LiveLine(TimeSpan At, string Text);

internal enum LiveState { Starting, DownloadingModel, Running, Ended, Failed }

/// <summary>
/// The live transcript of one recording: runs <c>sakshi live-transcribe VIDEO</c> as a child process and collects
/// what it prints. No Avalonia here; the window reads <see cref="Snapshot"/> when <see cref="Changed"/> fires.
/// Thread-safe: lines arrive on a reader thread.
/// </summary>
internal sealed class LiveTranscript : IAsyncDisposable
{
    internal const string StartingText = "Starting… the first lines appear about 10 seconds after someone speaks.";
    internal const string DownloadingText = "Downloading the speech model (about 150 MB, once)…";
    internal const string EndedText =
        "The recording has stopped. If you save it, the full transcript, with screen pictures, will be in meeting.md when it is ready.";
    internal const string UnreadableText =
        "Sakshi cannot read OBS's recording until it ends, because OBS records to plain MP4. In OBS, choose " +
        "Settings → Output → Recording Format → Hybrid MP4 (or MKV); live transcripts work from the next recording. " +
        "The full transcript will still be made after this meeting.";
    internal const string NoModelText =
        "The speech model could not be downloaded; check the internet connection. " +
        "The full transcript will still be made after the meeting.";
    internal const string ModelMissingText =
        "The speech model named in Sakshi's config (\"model\") does not exist. Fix the path, or remove the setting to use " +
        "the standard model. The full transcript after the meeting needs it too.";
    internal const string NoFfmpegText =
        "ffmpeg is not installed, so Sakshi cannot read the recording. Choose Setup… in the Sakshi menu for the command " +
        "that installs it; the full transcript after the meeting needs it too.";
    internal const string LostText =
        "Sakshi can no longer read the recording file, so the live transcript has stopped. " +
        "The full transcript will still be made after the meeting.";
    internal const string CrashedText =
        "The live transcript stopped unexpectedly (details are in sakshi.log). " +
        "The full transcript will still be made after the meeting.";

    private readonly object _sync = new();
    private readonly List<LiveLine> _lines = [];
    private TimeSpan _readTo;
    private LiveState _state = LiveState.Starting;
    private string? _problem;
    private Process? _process;
    private Task _pump = Task.CompletedTask;
    private readonly Action<string> _log;

    internal LiveTranscript(Action<string> log) => _log = log;

    /// <summary>Raised (on any thread) whenever a line, the progress or the state changes.</summary>
    public event Action? Changed;

    public sealed record View(IReadOnlyList<LiveLine> Lines, TimeSpan ReadTo, LiveState State, string Status);

    /// <summary>A consistent copy of everything so far, with the status text to show above it.</summary>
    public View Snapshot()
    {
        lock (_sync)
        {
            var status = _state switch
            {
                LiveState.Starting => StartingText,
                LiveState.DownloadingModel => DownloadingText,
                LiveState.Running => $"Transcribed up to {Clock(_readTo)}.",
                LiveState.Ended => EndedText,
                _ => _problem ?? CrashedText,
            };
            return new View([.. _lines], _readTo, _state, status);
        }
    }

    /// <summary>Starts <paramref name="exe"/> live-transcribe <paramref name="video"/>. Throws when it cannot start.</summary>
    public static LiveTranscript Start(string exe, string video, Action<string> log)
    {
        var live = new LiveTranscript(log);
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("live-transcribe");
        psi.ArgumentList.Add(video);
        var p = Process.Start(psi) ?? throw new InvalidOperationException("the live transcript could not start");
        live._process = p;
        live._pump = Task.Run(() => live.PumpAsync(p));
        log($"live transcript: started for {video}");
        return live;
    }

    private async Task PumpAsync(Process p)
    {
        var errors = Task.Run(async () =>
        {
            while (await p.StandardError.ReadLineAsync() is { } line) _log($"live transcript: {line}");
        });
        while (await p.StandardOutput.ReadLineAsync() is { } line) Accept(line);
        await errors;
        await p.WaitForExitAsync();
        Exited(p.ExitCode);
    }

    /// <summary>One line the child printed. Anything that is not one of its JSON lines is logged and ignored.</summary>
    internal void Accept(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var o = doc.RootElement;
            lock (_sync)
            {
                if (o.TryGetProperty("text", out var text) && o.TryGetProperty("start", out var start))
                {
                    _lines.Add(new LiveLine(TimeSpan.FromSeconds(start.GetDouble()), text.GetString() ?? ""));
                    _state = LiveState.Running;
                }
                else if (o.TryGetProperty("readTo", out var readTo))
                {
                    _readTo = TimeSpan.FromSeconds(readTo.GetDouble());
                    _state = LiveState.Running;
                }
                else if (o.TryGetProperty("status", out var status) && status.GetString() == "downloading-model")
                    _state = LiveState.DownloadingModel;
                else if (o.TryGetProperty("error", out var error))
                {
                    _state = LiveState.Failed;
                    _problem = error.GetString() switch
                    {
                        "unreadable" => UnreadableText,
                        "no-model" => NoModelText,
                        "model-missing" => ModelMissingText,
                        "no-ffmpeg" => NoFfmpegText,
                        "lost" => LostText,
                        _ => CrashedText,
                    };
                }
                else return;
            }
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
        {
            _log($"live transcript: ignored a line ({e.Message})");
            return;
        }
        Changed?.Invoke();
    }

    /// <summary>The child ended: a clean exit after the recording stopped, or a failure it may already have reported.</summary>
    internal void Exited(int code)
    {
        lock (_sync)
        {
            if (_state != LiveState.Failed)
            {
                if (code == 0) _state = LiveState.Ended;
                else
                {
                    _state = LiveState.Failed;
                    _problem = CrashedText;
                }
            }
        }
        _log($"live transcript: ended (exit {code})");
        Changed?.Invoke();
    }

    /// <summary>The recording stopped: the child transcribes what is left and exits. Safe to call repeatedly.</summary>
    public void RecordingEnded()
    {
        try { _process?.StandardInput.Close(); }
        catch (Exception e) when (e is InvalidOperationException or IOException) { }
    }

    /// <summary>Quit: stop the child at once.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_process is not { } p) return;
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        try { await _pump.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (Exception e) { _log($"live transcript: {e.Message}"); }
        p.Dispose();
    }

    internal static string Clock(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes:00}:{t.Seconds:00}";
}
