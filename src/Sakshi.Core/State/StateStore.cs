using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sakshi.Core.Ports;

namespace Sakshi.Core.State;

/// <summary>
/// The state file. Every writer goes through <see cref="Update"/> (Load, change, Save
/// under one store-owned lock), so changes made by different components never overwrite
/// each other. <see cref="Save"/> replaces the whole state and is internal, for tests.
/// </summary>
public sealed class StateStore(string path, ILog log, INotifier notifier)
{
    private readonly object _gate = new();

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        WriteIndented = true,
    };

    private const int ReadAttempts = 5;
    private const int WriteAttempts = 5;

    public SakshiState Load()
    {
        lock (_gate) return LoadLocked();
    }

    /// <summary>Atomically applies <paramref name="change"/> to the current state and saves the result.</summary>
    public SakshiState Update(Func<SakshiState, SakshiState> change)
    {
        lock (_gate)
        {
            var next = change(LoadLocked());
            SaveLocked(next);
            return next;
        }
    }

    private SakshiState LoadLocked()
    {
        var text = ReadText();
        if (text is null) return SakshiState.Empty;

        try
        {
            var state = JsonSerializer.Deserialize<SakshiState>(text, Options);
            if (state is null || state.Capture is null || state.Jobs is null)
                throw new JsonException("state is incomplete");
            return state;
        }
        catch (JsonException ex)
        {
            return Quarantine(ex.Message);
        }
    }

    // Returns null when there is no file. A sharing violation (a concurrent Save's
    // move on Windows) is retried; if it persists it is thrown, never treated as corruption.
    private string? ReadText()
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return File.ReadAllText(path);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return null;
            }
            catch (IOException) when (attempt < ReadAttempts)
            {
                Thread.Sleep(100);
            }
        }
    }

    private SakshiState Quarantine(string reason)
    {
        var stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        try
        {
            for (var n = 1; ; n++)
            {
                var bad = path + ".bad-" + stamp + (n == 1 ? "" : "-" + n);
                try
                {
                    File.Move(path, bad, overwrite: false);
                }
                catch (IOException) when (File.Exists(bad))
                {
                    continue; // name taken: never overwrite an earlier quarantined file
                }
                log.Info($"state: {path} is unreadable ({reason}); kept as {bad}, starting idle");
                notifier.Notify("Sakshi",
                    $"The state file was unreadable, so Sakshi started fresh. The old file is kept as {bad}.",
                    Urgency.Critical);
                return SakshiState.Empty;
            }
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return SakshiState.Empty; // someone else already moved or removed it
        }
        catch (IOException ex)
        {
            log.Info($"state: {path} is unreadable ({reason}) and could not be moved aside: {ex.Message}");
            notifier.Notify("Sakshi",
                $"The state file {path} was unreadable and could not be moved aside, so Sakshi started fresh. The file has been left in place.",
                Urgency.Critical);
            return SakshiState.Empty;
        }
    }

    internal void Save(SakshiState state)
    {
        lock (_gate) SaveLocked(state);
    }

    private void SaveLocked(SakshiState state)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var tmp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(fs, state, Options);
                fs.Flush(flushToDisk: true);
            }
            MoveIntoPlace(tmp);
        }
        catch
        {
            try { File.Delete(tmp); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    // A reader holding state.json (on Windows) or anything else briefly occupying the name makes
    // the rename fail: retry a few times before giving up.
    private void MoveIntoPlace(string tmp)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(tmp, path, overwrite: true);
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException && attempt < WriteAttempts)
            {
                Thread.Sleep(100);
            }
        }
    }
}
