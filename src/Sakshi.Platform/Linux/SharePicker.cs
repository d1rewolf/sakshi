using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Sakshi.Core.Config;
using Sakshi.Core.Text;

namespace Sakshi.Platform.Linux;

/// <summary>
/// Wrapper around the Hyprland share picker (port of bin/sakshi-share-picker). xdg-desktop-portal-hyprland
/// hands the window list in XDPH_WINDOW_SHARING_LIST, one entry per toplevel:
/// <c>&lt;id&gt;[HC&gt;]&lt;class&gt;[HT&gt;]&lt;title&gt;[HE&gt;]&lt;address&gt;[HA&gt;]</c>. The wrapper restores titles the portal
/// sanitised, moves meeting windows to the front, reverses the list (the picker prepends each card), and runs
/// the real picker with its stdout passed through.
/// </summary>
public static class SharePicker
{
    private const string End = "[HA>]";
    private const string DefaultRealBin = "hyprland-preview-share-picker";
    private const string ActiveMarker = "SAKSHI_SHARE_PICKER_ACTIVE";

    private static readonly Regex EntryShape = new(
        @"\A([0-9]+)\[HC>\](.*)\[HT>\](.*)\[HE>\]([0-9]+)\[HA>\]\z", RegexOptions.Singleline | RegexOptions.CultureInvariant);
    private static readonly Regex Sanitised = new(@"['’]|(?<=>)\]", RegexOptions.CultureInvariant);
    private static readonly Regex TrailLine = new(@".*\[HC>\](.*)\[HT>\](.*)\[HE>\].*", RegexOptions.CultureInvariant);

    public static async Task<int> RunAsync(IReadOnlyList<string> args, SakshiConfig config, IProcessRunner runner,
        Func<string, string?> env, string home, CancellationToken ct = default, TextWriter? stderr = null)
    {
        // Set in the picker's environment: seeing it here means pickerRealBin (under whatever name)
        // ran us again. Run the default picker on the list as it came; it is already reordered.
        if (env(ActiveMarker) == "1")
        {
            var err = stderr ?? Console.Error;
            err.WriteLine($"sakshi share-picker: pickerRealBin points back at sakshi; using {DefaultRealBin}");
            return await RunPickerAsync(DefaultRealBin, args, new Dictionary<string, string?>(), runner, err, ct);
        }

        var realBin = config.PickerRealBin;
        if (IsSelf(realBin)) realBin = DefaultRealBin;

        var childEnv = new Dictionary<string, string?> { [ActiveMarker] = "1" };
        var list = env("XDPH_WINDOW_SHARING_LIST");
        if (!string.IsNullOrEmpty(list))
        {
            var reordered = await ReorderAsync(list, config.PickerPriorityRegex, runner, ct);
            if (!string.IsNullOrEmpty(reordered)) list = reordered;
            childEnv["XDPH_WINDOW_SHARING_LIST"] = list;
            if (env("XDG_RUNTIME_DIR") is { Length: > 0 } runtime) WriteTrail(Path.Combine(runtime, "sakshi-share-picker.last"), list);
        }

        var pickerArgs = new List<string>();
        if (DefaultPageConfig(config.PickerDefaultPage, args, home) is { } conf) pickerArgs.AddRange(["--config", conf]);
        pickerArgs.AddRange(args);
        return await RunPickerAsync(realBin, pickerArgs, childEnv, runner, stderr ?? Console.Error, ct);
    }

    private static async Task<int> RunPickerAsync(string realBin, IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string?> env, IProcessRunner runner, TextWriter stderr, CancellationToken ct)
    {
        try
        {
            return await runner.RunAttachedAsync(realBin, args, env, ct);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Like bash's exec of a missing command.
            stderr.WriteLine($"sakshi share-picker: {realBin}: not found");
            return 127;
        }
    }

    /// <summary>Never let ourselves be the "real" picker (an infinite exec loop).</summary>
    private static bool IsSelf(string realBin) => Path.GetFileName(realBin) == "sakshi-share-picker";

    private static async Task<string?> ReorderAsync(string raw, string priorityRegex, IProcessRunner runner, CancellationToken ct)
    {
        var entries = raw.Split(End).Where(e => e.Length > 0).Select(e => e + End).ToList();
        var clients = await HyprlandClientsAsync(runner, ct);
        entries = entries.Select(e => Restore(e, clients)).ToList();

        // bash: when the ordering fails the list stays exactly as it came (not restored, not reversed).
        if (WindowPriority.TryOrder(entries, e => MeetingText(e), priorityRegex) is not { } ordered) return null;
        return string.Concat(ordered.Reverse());
    }

    /// <summary>Real class/title per decimal window address; any failure leaves the entries as they came.</summary>
    private static async Task<Dictionary<ulong, (string Class, string Title)>> HyprlandClientsAsync(IProcessRunner runner, CancellationToken ct)
    {
        var clients = new Dictionary<ulong, (string, string)>();
        try
        {
            var r = await runner.RunAsync("hyprctl", ["-j", "clients"], TimeSpan.FromSeconds(3), ct);
            using var doc = JsonDocument.Parse(r.StdOut);
            foreach (var c in doc.RootElement.EnumerateArray())
            {
                var address = c.GetProperty("address").GetString()!;
                var key = Convert.ToUInt64(address.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? address[2..] : address, 16);
                clients[key] = (Str(c, "class"), Str(c, "title"));
            }
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return [];
        }
        return clients;
    }

    private static string Str(JsonElement c, string name) =>
        c.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

    private static string Restore(string entry, Dictionary<ulong, (string Class, string Title)> clients)
    {
        var m = EntryShape.Match(entry);
        if (!m.Success) return entry;
        if (!ulong.TryParse(m.Groups[4].Value, out var addr) || !clients.TryGetValue(addr, out var real)) return entry;
        var (cls, title) = (m.Groups[2].Value, m.Groups[3].Value);
        // Only trust hyprctl when it differs by what the portal could have stripped, not when the title has since changed.
        if (Sanitised.Replace(real.Class, " ") != cls || Sanitised.Replace(real.Title, " ") != title) return entry;
        return $"{m.Groups[1].Value}[HC>]{real.Class}[HT>]{real.Title}[HE>]{m.Groups[4].Value}{End}";
    }

    /// <summary>What the priority regex is matched against: class and title, or the whole entry when it has neither.</summary>
    private static string MeetingText(string entry)
    {
        var body = entry[..^End.Length];
        string cls = "", title = "";
        var c = body.IndexOf("[HC>]", StringComparison.Ordinal);
        if (c >= 0)
        {
            var rest = body[(c + 5)..];
            var t = rest.IndexOf("[HT>]", StringComparison.Ordinal);
            if (t >= 0)
            {
                cls = rest[..t];
                rest = rest[(t + 5)..];
                var e = rest.IndexOf("[HE>]", StringComparison.Ordinal);
                title = e >= 0 ? rest[..e] : rest;
            }
        }
        return cls.Length > 0 || title.Length > 0 ? cls + "\n" + title : body;
    }

    /// <summary>A small debug trail: "class | title" per line in the order the picker shows them (the handed-over list reversed).</summary>
    private static void WriteTrail(string file, string list)
    {
        try
        {
            var lines = new List<string>();
            foreach (var piece in list.Split(End).SelectMany(p => p.Split('\n')))
                if (TrailLine.Match(piece) is { Success: true } m) lines.Add($"{m.Groups[1].Value} | {m.Groups[2].Value}");
            lines.Reverse();
            File.WriteAllText(file, string.Concat(lines.Select(l => l + "\n")), new UTF8Encoding(false));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Opens the picker on <paramref name="page"/> by writing a sibling of its config.yaml with only default_page changed,
    /// so relative stylesheet paths still resolve. Returns the file, or null when nothing applies or the write failed.
    /// </summary>
    private static string? DefaultPageConfig(string page, IReadOnlyList<string> args, string home)
    {
        var dir = Path.Combine(home, ".config", "hyprland-preview-share-picker");
        var conf = Path.Combine(dir, "config.yaml");
        if (string.IsNullOrEmpty(page) || !File.Exists(conf)) return null;
        var joined = " " + string.Join(' ', args) + " ";
        if (joined.Contains(" -c ", StringComparison.Ordinal) || joined.Contains(" --config", StringComparison.Ordinal)) return null;
        try
        {
            var text = File.ReadAllText(conf);
            var line = new Regex(@"^default_page:[^\n]*", RegexOptions.Multiline | RegexOptions.CultureInvariant);
            text = line.IsMatch(text)
                ? line.Replace(text, _ => "default_page: " + page)
                : (text.Length == 0 || text.EndsWith('\n') ? text : text + "\n") + $"default_page: {page}\n";
            var sibling = Path.Combine(dir, "sakshi.yaml");
            File.WriteAllText(sibling, text, new UTF8Encoding(false));
            return sibling;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }
}
