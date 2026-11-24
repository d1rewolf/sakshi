using System.Reflection;
using System.Text.Json;
using Sakshi.Core.Paths;

namespace Sakshi.Core.Config;

public static class ConfigStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
    };

    private static readonly HashSet<string> KnownKeys = typeof(SakshiConfig)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.Name != nameof(SakshiConfig.EffectiveArchiveDir))
        .Select(p => p.Name)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static SakshiConfig Load(string path, HostOs os, string home, Action<string>? warn = null)
    {
        var defaults = SakshiConfig.Defaults(os, home);
        if (!File.Exists(path)) return defaults;

        var text = File.ReadAllText(path);
        System.Text.Json.Nodes.JsonObject root;
        try
        {
            root = System.Text.Json.Nodes.JsonNode.Parse(text,
                    documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })
                as System.Text.Json.Nodes.JsonObject
                ?? throw new InvalidDataException($"Config file {path} must contain a JSON object.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Config file {path} is not valid JSON: {ex.Message}", ex);
        }

        // An explicit null means "use the default".
        foreach (var name in root.Where(kv => kv.Value is null).Select(kv => kv.Key).ToList())
            root.Remove(name);

        bool Has(string key) => root.Any(kv => kv.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        foreach (var kv in root.Where(kv => !KnownKeys.Contains(kv.Key)))
            warn?.Invoke($"config: unknown key '{kv.Key}' ignored");

        // OS- and home-dependent defaults apply only to keys the file leaves out.
        SakshiConfig merged;
        try { merged = root.Deserialize<SakshiConfig>(Options) ?? defaults; }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Config file {path} has an invalid value: {ex.Message}", ex);
        }
        // Nulls inside a list are dropped; a list that held only nulls means the default. A blank
        // meetings or archive folder means the default too, never a path relative to the cwd.
        var handOff = Has("handOffPath") ? WithoutNulls(merged.HandOffPath) : null;
        var modes = WithoutNulls(merged.FramesModes);
        merged = merged with
        {
            MeetingsDir = string.IsNullOrWhiteSpace(merged.MeetingsDir) ? defaults.MeetingsDir : merged.MeetingsDir,
            ObsSource = Has("obsSource") ? merged.ObsSource : defaults.ObsSource,
            ArchiveDir = string.IsNullOrWhiteSpace(merged.ArchiveDir) ? "" : merged.ArchiveDir,
            HandOffPath = handOff ?? defaults.HandOffPath,
            FramesModes = modes is null
                ? defaults.FramesModes
                : modes.Select(m => m with
                {
                    Glyph = m.Glyph ?? "", Label = m.Label ?? "", Args = m.Args ?? "", Description = m.Description ?? "",
                }).ToList(),
        };
        return merged with
        {
            MeetingsDir = Expand(merged.MeetingsDir, home),
            ArchiveDir = Expand(merged.ArchiveDir, home),
            Model = Expand(merged.Model, home),
            RecordFolder = Expand(merged.RecordFolder, home),
            HandOffPath = merged.HandOffPath.Select(p => Expand(p, home)).ToList(),
        };
    }

    /// <summary>The list without null entries; null when it had entries and all were null.</summary>
    private static List<T>? WithoutNulls<T>(IReadOnlyList<T?>? list) where T : class
    {
        if (list is null) return null;
        var kept = list.OfType<T>().ToList();
        return kept.Count == 0 && list.Count > 0 ? null : kept;
    }

    // Every key is commented out, so the file means "all defaults" until the user changes a line.
    // Trailing commas are accepted, so any mix of lines can be uncommented.
    private const string StarterText = """
        // Sakshi settings. Anything not set here keeps its default.
        // To change a setting, remove the // in front of its line and edit the value.
        // Keys are camelCase; unknown keys are ignored.
        {
          // "meetingsDir": "~/meetings",
          // "obsScene": "Scene",
          // "obsSource": "{obsSource}",
          // "pickerPriorityRegex": "zoom|teams|meet\\.google|webex|gotomeeting",

          // true: the shortcut, a click on the bar icon and "sakshi toggle" record sound only (no window):
          // "recordAudioOnly": false,

          // OBS settings for Sakshi's own OBS profile (yours is never changed):
          // "recordFps": 15,
          // "recordMaxWidth": 1920,
          // "recordMaxHeight": 1080,
          // "recordFormat": "hybrid_mp4",
          // "recordEncoder": "",
          // "recordFolder": "",
        }

        """;

    /// <summary>
    /// Writes a commented starter file when there is no config yet (so "Open config" has something to
    /// open). Never replaces an existing file. True when it wrote one.
    /// </summary>
    public static bool CreateStarter(string path, HostOs os)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        // Private from the start, like Save: the user may add the OBS websocket password here.
        var opts = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) opts.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        try
        {
            using var w = new StreamWriter(new FileStream(path, opts));
            // Shows the name this OS really uses, so uncommenting the line changes nothing by itself.
            w.Write(StarterText.Replace("{obsSource}", SakshiConfig.Defaults(os, "").ObsSource).ReplaceLineEndings());
            return true;
        }
        catch (IOException) when (File.Exists(path))
        {
            return false;
        }
    }

    /// <summary>Writes a temporary file next to <paramref name="path"/> and renames it over the old one.</summary>
    public static void Save(string path, SakshiConfig config)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            // The config holds the OBS websocket password: keep the user's mode on replace and make
            // a new file private, set at creation so the secret is never briefly readable.
            // (Unix modes do not exist on Windows.)
            var opts = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            if (!OperatingSystem.IsWindows())
            {
                if (File.Exists(path)) mode = File.GetUnixFileMode(path);
                opts.UnixCreateMode = mode;
            }
            using (var fs = new FileStream(tmp, opts))
            {
                JsonSerializer.Serialize(fs, config, Options);
                fs.Flush(flushToDisk: true);
            }
            // UnixCreateMode is masked by the umask; restore the exact mode.
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(tmp, mode);
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    internal static string Expand(string value, string home)
    {
        if (value == "~") return home;
        if (value.StartsWith("~/", StringComparison.Ordinal) || value.StartsWith("~\\", StringComparison.Ordinal))
            return Path.Combine(home, value[2..]);
        return value;
    }
}
