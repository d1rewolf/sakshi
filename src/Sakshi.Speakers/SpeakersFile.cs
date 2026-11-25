using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text.Json.Serialization;
using Sakshi.Transcribe;

namespace Sakshi.Speakers;

[JsonConverter(typeof(JsonStringEnumConverter<SpeakerStatus>))]
public enum SpeakerStatus { Named, Suggested, Unknown }

public sealed record SpeakerEntry(
    string Id, SpeakerStatus Status, string? Name, string? PersonId, string? Suggestion, double? Score,
    double Seconds, string Sample, string Clip, float[] Voiceprint)
{
    [JsonIgnore]
    public string Label => Status switch
    {
        SpeakerStatus.Named => Name ?? "",
        SpeakerStatus.Suggested => Suggestion + "?",
        _ => "Speaker " + Id.TrimStart('S'),
    };
}

public sealed partial record SpeakersFile(
    int Version, string EmbeddingModel, string Source, IReadOnlyList<FrameRecord>? Frames, IReadOnlyList<SpeakerEntry> Speakers)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static SpeakersFile Read(string path)
    {
        try
        {
            var file = JsonSerializer.Deserialize<SpeakersFile>(File.ReadAllText(path), Options)
                ?? throw new InvalidDataException("empty");
            if (file.Version != 1) throw new InvalidDataException($"unsupported version {file.Version}");
            if (file.Speakers is null) throw new InvalidDataException("no speakers");
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in file.Speakers)
            {
                if (s is null) throw new InvalidDataException("empty speaker entry");
                if (s.Id is null || !SpeakerId().IsMatch(s.Id)) throw new InvalidDataException($"bad speaker id {s.Id ?? "null"}");
                if (!ids.Add(s.Id)) throw new InvalidDataException($"speaker {s.Id} is listed twice");
                if (!Enum.IsDefined(s.Status)) throw new InvalidDataException($"speaker {s.Id} has an unknown status");
                if (s.Voiceprint is not { Length: > 0 }) throw new InvalidDataException($"speaker {s.Id} has no voiceprint");
            }
            return file;
        }
        catch (Exception e) when (e is JsonException or InvalidDataException or NotSupportedException)
        {
            throw new InvalidDataException($"Cannot read {path}: {e.Message}", e);
        }
    }

    [GeneratedRegex(@"^S\d+$")]
    private static partial Regex SpeakerId();

    public void Write(string path) => AtomicFile.WriteAllText(path, JsonSerializer.Serialize(this, Options) + "\n");
}
