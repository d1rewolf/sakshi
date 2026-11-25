using Sakshi.Speakers;

namespace Sakshi.Speakers.Tests;

public sealed class VoicesCommandTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sakshi-voices-" + Guid.NewGuid().ToString("N"));
    private readonly string _file;
    private static readonly DateTimeOffset Now = new(2026, 1, 5, 10, 0, 0, TimeSpan.Zero);

    public VoicesCommandTests() { _file = Path.Combine(_root, "voices", "voices.json"); }
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private void Seed()
    {
        var lib = VoiceLibrary.Load(_file);
        lib.Teach("Ada", [1f, 0f], "d/one", "S1", Now);
        lib.Teach("Ada", [1f, 0f], "d/two", "S1", Now.AddMinutes(1));
        lib.Teach("Ada", [0.6f, 0.8f], "d/two", "S2", Now.AddMinutes(2));
        lib.Teach("Ben", [0f, 1f], "d/one", "S2", Now);
        lib.Save();
    }

    private (int Code, string Out, string Err) Run(params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var code = VoicesCommand.Run(args, _file, o, e);
        return (code, o.ToString().Replace("\r", ""), e.ToString().Replace("\r", ""));
    }

    [Fact]
    public void ListsPeopleWithPrintsAndMeetings()
    {
        Seed();
        var r = Run();
        Assert.Equal(0, r.Code);
        Assert.Equal("Ada  3 voiceprints  2 meetings\nBen  1 voiceprint  1 meeting\n", r.Out);
    }

    [Fact]
    public void NoPeopleSaysHowToStart()
    {
        var r = Run();
        Assert.Equal(0, r.Code);
        Assert.Equal("No voices yet. Name a speaker with: sakshi speakers MEETING S1=NAME\n", r.Out);
    }

    [Fact]
    public void RenameThenForget()
    {
        Seed();
        var r = Run("rename", "Ada", "Ann");
        Assert.Equal(0, r.Code);
        Assert.NotNull(VoiceLibrary.Load(_file).Find("Ann"));
        Assert.Null(VoiceLibrary.Load(_file).Find("Ada"));
        r = Run("forget", "Ann");
        Assert.Equal(0, r.Code);
        Assert.Equal("Forgot Ann and 3 voiceprints\n", r.Out);
        Assert.Null(VoiceLibrary.Load(_file).Find("Ann"));
    }

    [Fact]
    public void UnknownNameExitsOne()
    {
        Seed();
        var r = Run("forget", "Zed");
        Assert.Equal(1, r.Code);
        Assert.Equal("No voice named Zed.\n", r.Err);
        Assert.Equal(1, Run("rename", "Zed", "Q").Code);
    }

    [Fact]
    public void RenameToAnExistingNameIsAnError()
    {
        Seed();
        var r = Run("rename", "Ada", "Ben");
        Assert.Equal(1, r.Code);
        Assert.StartsWith("Error: Ben is already in the voice library", r.Err);
        Assert.NotNull(VoiceLibrary.Load(_file).Find("Ada"));
    }

    [Fact]
    public void ABadFileExitsOneNamingIt()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        File.WriteAllText(_file, "{nope");
        foreach (var args in new[] { Array.Empty<string>(), ["forget", "Ada"], ["rename", "Ada", "B"] })
        {
            var r = Run(args);
            Assert.Equal(1, r.Code);
            Assert.Contains(_file, r.Err);
        }
        Assert.Equal("{nope", File.ReadAllText(_file));
    }

    [Fact]
    public void UsageOnUnknownSubcommandOrWrongArgs()
    {
        Assert.Equal(2, Run("bogus").Code);
        Assert.Equal(2, Run("rename", "Ada").Code);
        Assert.Equal(2, Run("forget").Code);
        Assert.Contains("sakshi voices", Run("bogus").Err);
    }

    [Fact]
    public void RenameTrimsTheNewNameAndRefusesAnEmptyOne()
    {
        Seed();
        var before = File.ReadAllBytes(_file);
        var r = Run("rename", "Ada", "  ");
        Assert.Equal(2, r.Code);
        Assert.Contains("sakshi voices rename OLD NEW", r.Err);
        Assert.Equal(before, File.ReadAllBytes(_file));
        Assert.Equal(0, Run("rename", "Ada", " Ann ").Code);
        Assert.Equal("Ann", VoiceLibrary.Load(_file).People[0].Name);
    }
}
