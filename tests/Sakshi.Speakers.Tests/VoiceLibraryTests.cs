using Sakshi.Speakers;

namespace Sakshi.Speakers.Tests;

public sealed class VoiceLibraryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sakshi-vl-" + Guid.NewGuid().ToString("N"));
    private string File_ => Path.Combine(_dir, "voices", "voices.json");
    private static readonly DateTimeOffset T0 = new(2026, 11, 25, 10, 0, 0, TimeSpan.Zero);

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void MissingFileIsEmpty()
    {
        var lib = VoiceLibrary.Load(File_);
        Assert.Empty(lib.People);
        Assert.Null(lib.BackgroundSum.Sum);
        Assert.Equal(0, lib.BackgroundSum.Count);
    }

    [Fact]
    public void TeachSaveLoadRoundTrips()
    {
        var lib = VoiceLibrary.Load(File_);
        var p = lib.Teach("Ada", [1f, 2f], "2026-11-20-Fri/steering", "S2", T0);
        lib.AddToBackground([[1f, 1f], [2f, 3f]]);
        lib.Save();
        var back = VoiceLibrary.Load(File_);
        var ada = Assert.Single(back.People);
        Assert.Equal(p.Id, ada.Id);
        Assert.Equal(32, ada.Id.Length);
        Assert.Equal("Ada", ada.Name);
        var pr = Assert.Single(ada.Prints);
        Assert.Equal([1f, 2f], pr.Vector);
        Assert.Equal("S2", pr.Speaker);
        Assert.Equal(T0, pr.Added);
        Assert.Equal(2, back.BackgroundSum.Count);
        Assert.Equal([3.0, 4.0], back.BackgroundSum.Sum!);
    }

    [Fact]
    public void TeachFindsCaseInsensitivelyAndKeepsName()
    {
        var lib = VoiceLibrary.Load(File_);
        lib.Teach("Ada", [1f], "m", "S1", T0);
        lib.Teach("ada", [2f], "m", "S2", T0.AddMinutes(1));
        var ada = Assert.Single(lib.People);
        Assert.Equal("Ada", ada.Name);
        Assert.Equal(2, ada.Prints.Count);
        Assert.Same(lib.Find("ADA"), lib.People[0]);
    }

    [Fact]
    public void EleventhPrintDropsOldest()
    {
        var lib = VoiceLibrary.Load(File_);
        for (var i = 0; i < 11; i++) lib.Teach("Ada", [i], "m", "S" + i, T0.AddMinutes(i));
        var ada = lib.Find("Ada")!;
        Assert.Equal(10, ada.Prints.Count);
        Assert.DoesNotContain(ada.Prints, p => p.Speaker == "S0");
        Assert.Contains(ada.Prints, p => p.Speaker == "S10");
    }

    [Fact]
    public void RenameToOtherPersonThrows()
    {
        var lib = VoiceLibrary.Load(File_);
        lib.Teach("Ada", [1f], "m", "S1", T0);
        lib.Teach("Ben", [1f], "m", "S2", T0);
        Assert.Throws<VoiceLibraryException>(() => lib.Rename("Ada", "ben"));
        Assert.Throws<VoiceLibraryException>(() => lib.Rename("Cy", "Dee"));
        lib.Rename("Ada", "Ada L");
        Assert.NotNull(lib.Find("ada l"));
        Assert.Null(lib.Find("Ada"));
    }

    [Fact]
    public void ForgetRemoves()
    {
        var lib = VoiceLibrary.Load(File_);
        lib.Teach("Ada", [1f], "m", "S1", T0);
        Assert.True(lib.Forget("ada"));
        Assert.False(lib.Forget("ada"));
        Assert.Empty(lib.People);
    }

    [Fact]
    public void BackgroundLengthMismatchThrows()
    {
        var lib = VoiceLibrary.Load(File_);
        lib.AddToBackground([[1f, 2f]]);
        Assert.Throws<ArgumentException>(() => lib.AddToBackground([[1f]]));
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("{\"version\":2,\"people\":[]}")]
    public void UnreadableFileThrowsAndIsUntouched(string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(File_)!);
        File.WriteAllText(File_, text);
        var before = File.ReadAllBytes(File_);
        var ex = Assert.Throws<VoiceLibraryException>(() => VoiceLibrary.Load(File_));
        Assert.Contains(File_, ex.Message);
        Assert.Equal(before, File.ReadAllBytes(File_));
    }

    [Fact]
    public void SavedFileIsPrivateOnUnix()
    {
        if (OperatingSystem.IsWindows()) return;
        var lib = VoiceLibrary.Load(File_);
        lib.Teach("Ada", [1f], "m", "S1", T0);
        lib.Save();
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, System.IO.File.GetUnixFileMode(File_));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            System.IO.File.GetUnixFileMode(Path.GetDirectoryName(File_)!));
    }

    [Theory]
    [InlineData("{\"version\":1,\"background\":{\"count\":3},\"people\":[]}")]
    [InlineData("{\"version\":1,\"background\":{\"sum\":[1],\"count\":0},\"people\":[]}")]
    [InlineData("{\"version\":1,\"people\":[{\"id\":\"\",\"name\":\"Ada\",\"voiceprints\":[]}]}")]
    [InlineData("{\"version\":1,\"people\":[{\"id\":\"a\",\"name\":\"Ada\",\"voiceprints\":[{\"vector\":[],\"meeting\":\"m\",\"speaker\":\"S1\",\"added\":\"2026-11-25T10:00:00Z\"}]}]}")]
    [InlineData("{\"version\":1,\"people\":[{\"id\":\"a\",\"name\":\"Ada\",\"voiceprints\":[{\"vector\":[1],\"meeting\":\"m\",\"speaker\":\"S1\",\"added\":\"2026-11-25T10:00:00Z\"},{\"vector\":[1,2],\"meeting\":\"m\",\"speaker\":\"S2\",\"added\":\"2026-11-25T10:00:00Z\"}]}]}")]
    public void InconsistentContentIsUnreadable(string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(File_)!);
        File.WriteAllText(File_, text);
        var ex = Assert.Throws<VoiceLibraryException>(() => VoiceLibrary.Load(File_));
        Assert.Contains(File_, ex.Message);
    }

    [Fact]
    public void InputArraysAreCopied()
    {
        var lib = VoiceLibrary.Load(File_);
        var v = new[] { 1f, 2f };
        lib.Teach("Ada", v, "m", "S1", T0);
        v[0] = 99;
        Assert.Equal(1f, lib.Find("Ada")!.Prints[0].Vector[0]);
        var bg = new[] { 1f };
        lib.AddToBackground([bg]);
        bg[0] = 50;
        lib.AddToBackground([[1f]]);
        Assert.Equal([2.0], lib.BackgroundSum.Sum!);
    }

    [Fact]
    public void TeachReplacesAnEarlierPrintOfTheSameMeetingSpeaker()
    {
        var lib = VoiceLibrary.Load(File_);
        lib.Teach("Ada", [1f, 0f], "d/one", "S1", T0);
        lib.Teach("Ada", [1f, 0f], "d/two", "S1", T0);
        lib.Teach("Ben", [1f, 0f], "d/one", "S1", T0.AddMinutes(1));
        Assert.Equal(["d/two"], lib.Find("Ada")!.Prints.Select(p => p.Meeting));
        Assert.Equal(["d/one"], lib.Find("Ben")!.Prints.Select(p => p.Meeting));

        lib.Teach("Ben", [1f, 0f], "d/one", "S1", T0.AddMinutes(2));
        Assert.Single(lib.Find("Ben")!.Prints);
    }

    [Fact]
    public void TeachKeepsAPrintFromAnEarlierLabellingThatReusedTheId()
    {
        // Labelling a meeting again numbers its speakers afresh: the new S1 is not the voice the old S1 taught.
        var lib = VoiceLibrary.Load(File_);
        lib.Teach("Ada", [1f, 0f], "d/one", "S1", T0);
        lib.Teach("Ben", [0f, 1f], "d/one", "S1", T0.AddMinutes(1));
        Assert.Single(lib.Find("Ada")!.Prints);
        Assert.Single(lib.Find("Ben")!.Prints);
    }

    [Fact]
    public void UnteachRemovesThatPrintAndKeepsThePerson()
    {
        var lib = VoiceLibrary.Load(File_);
        lib.Teach("Ada", [1f, 0f], "d/one", "S1", T0);
        Assert.True(lib.Unteach("d/one", [1f, 0f]));
        Assert.Empty(lib.Find("Ada")!.Prints);
        Assert.False(lib.Unteach("d/one", [1f, 0f]));
        lib.Save();
        Assert.Empty(VoiceLibrary.Load(File_).Find("Ada")!.Prints);
    }

    [Fact]
    public void UnteachMatchesTheMeetingAndTheVoiceprint()
    {
        var lib = VoiceLibrary.Load(File_);
        lib.Teach("Ada", [1f, 0f], "d/one", "S1", T0);
        Assert.False(lib.Unteach("d/one", [0f, 1f]));
        Assert.False(lib.Unteach("d/two", [1f, 0f]));
        Assert.Single(lib.Find("Ada")!.Prints);
    }

    [Fact]
    public void TheSameVoiceUnderANewIdReplacesItsEarlierPrint()
    {
        // Labelling the same audio again can give the same voice another id; it is still one voiceprint.
        var lib = VoiceLibrary.Load(File_);
        lib.Teach("Ada", [1f, 0f], "d/one", "S1", T0);
        lib.Teach("Ben", [1f, 0f], "d/one", "S4", T0.AddMinutes(1));
        Assert.Empty(lib.Find("Ada")!.Prints);
        Assert.Equal("S4", Assert.Single(lib.Find("Ben")!.Prints).Speaker);
    }

    [Fact]
    public void UnteachMatchesAVoiceprintAfterASaveAndLoad()
    {
        var v = new[] { 0.1f, -0.3333333f, 1e-7f, 0.70710677f };
        var lib = VoiceLibrary.Load(File_);
        lib.Teach("Ada", v, "d/one", "S1", T0);
        lib.Save();
        var again = VoiceLibrary.Load(File_);
        Assert.True(again.Unteach("d/one", (float[])v.Clone()));
    }

    [Fact]
    public void TeachByIdFollowsARename()
    {
        var lib = VoiceLibrary.Load(File_);
        var ada = lib.Teach("Ada", [1f, 0f], "d/one", "S1", T0);
        lib.Rename("Ada", "Adah");
        var p = lib.Teach("Ada", [1f, 0f], "d/two", "S2", T0, ada.Id);
        Assert.Equal(ada.Id, p.Id);
        Assert.Equal("Adah", p.Name);
        Assert.Single(lib.People);
        Assert.Equal(2, p.Prints.Count);
    }
}
