using System.Text.Json;
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

public sealed record SpeakersFile(
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
            return file;
        }
        catch (Exception e) when (e is JsonException or InvalidDataException or NotSupportedException)
        {
            throw new InvalidDataException($"Cannot read {path}: {e.Message}", e);
        }
    }

    public void Write(string path) => AtomicFile.WriteAllText(path, JsonSerializer.Serialize(this, Options) + "\n");
}
