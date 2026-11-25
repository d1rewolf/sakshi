using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sakshi.Speakers;

public sealed class VoiceLibraryException(string message, Exception? inner = null) : Exception(message, inner);

public sealed record StoredPrint(float[] Vector, string Meeting, string Speaker, DateTimeOffset Added);

public sealed record Person(string Id, string Name, IReadOnlyList<StoredPrint> Prints);

public sealed class VoiceLibrary
{
    private sealed record BackgroundDto(double[] Sum, int Count);
    private sealed record PrintDto(float[] Vector, string Meeting, string Speaker, DateTimeOffset Added);
    private sealed record PersonDto(string Id, string Name, List<PrintDto> Voiceprints);
    private sealed record FileDto(int Version, BackgroundDto? Background, List<PersonDto> People);

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _path;
    private readonly List<Person> _people = [];
    private double[]? _sum;
    private int _count;

    private VoiceLibrary(string path) => _path = path;

    public IReadOnlyList<Person> People => _people;
    public (double[]? Sum, int Count) BackgroundSum => (_sum, _count);

    public static VoiceLibrary Load(string path)
    {
        var lib = new VoiceLibrary(path);
        if (!File.Exists(path)) return lib;
        try
        {
            var dto = JsonSerializer.Deserialize<FileDto>(File.ReadAllText(path), Options)
                ?? throw new InvalidDataException("empty file");
            if (dto.Version != 1) throw new InvalidDataException($"unsupported version {dto.Version}");
            if (dto.Background is { } bg) { lib._sum = bg.Sum; lib._count = bg.Count; }
            foreach (var p in dto.People ?? [])
                lib._people.Add(new Person(p.Id, p.Name,
                    (p.Voiceprints ?? []).Select(v => new StoredPrint(v.Vector, v.Meeting, v.Speaker, v.Added)).ToList()));
        }
        catch (Exception e) when (e is JsonException or InvalidDataException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            throw new VoiceLibraryException($"Cannot read the voice library {path}: {e.Message}", e);
        }
        return lib;
    }

    public Person? Find(string name) =>
        _people.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    public Person Teach(string name, float[] raw, string meeting, string speaker, DateTimeOffset now)
    {
        var print = new StoredPrint(raw, meeting, speaker, now);
        var existing = Find(name);
        var prints = existing is null ? [] : existing.Prints.ToList();
        prints.Add(print);
        prints = prints.OrderBy(p => p.Added).ToList(); // stable: ties keep insertion order
        var max = SpeakerSettings.MaxVoiceprintsPerPerson;
        if (prints.Count > max) prints = prints.GetRange(prints.Count - max, max);
        var person = new Person(existing?.Id ?? Guid.NewGuid().ToString("N"), existing?.Name ?? name, prints);
        if (existing is null) _people.Add(person);
        else _people[_people.IndexOf(existing)] = person;
        return person;
    }

    public void Rename(string oldName, string newName)
    {
        var p = Find(oldName) ?? throw new VoiceLibraryException($"No one called {oldName} in the voice library.");
        if (Find(newName) is { } other && !ReferenceEquals(other, p))
            throw new VoiceLibraryException($"{newName} is already in the voice library.");
        _people[_people.IndexOf(p)] = p with { Name = newName };
    }

    public bool Forget(string name)
    {
        var p = Find(name);
        return p is not null && _people.Remove(p);
    }

    public void AddToBackground(IEnumerable<float[]> raw)
    {
        foreach (var v in raw)
        {
            _sum ??= new double[v.Length];
            if (v.Length != _sum.Length)
                throw new ArgumentException($"Voiceprint has {v.Length} values, expected {_sum.Length}.");
            for (var i = 0; i < v.Length; i++) _sum[i] += v[i];
            _count++;
        }
    }

    public void Save()
    {
        var dto = new FileDto(1, _sum is null ? null : new BackgroundDto(_sum, _count),
            _people.Select(p => new PersonDto(p.Id, p.Name,
                p.Prints.Select(v => new PrintDto(v.Vector, v.Meeting, v.Speaker, v.Added)).ToList())).ToList());
        AtomicFile.Write(_path, JsonSerializer.Serialize(dto, Options) + "\n", privateFile: true);
    }
}
