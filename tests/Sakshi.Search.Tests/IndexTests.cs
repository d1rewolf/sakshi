using System.Text.Json;
using Microsoft.Data.Sqlite;
using Sakshi.Search;
using Sakshi.TestSupport;

namespace Sakshi.Search.Tests;

public sealed class IndexTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "sakshi-index-" + Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly string _path;
    private readonly List<string> _warn = [];
    private readonly string _a;
    private readonly string _b;
    private SearchIndex _index;

    public IndexTests()
    {
        _root = Path.Combine(_tmp, "meetings");
        Directory.CreateDirectory(_root);
        _path = Path.Combine(_tmp, "data", "index.db");
        _a = MeetingFixture.Make(_root, "2026-11-04-Wed", "catholic_keys",
            [(0, "The diocese wants keys."), (3000, "Second line")]);
        _b = MeetingFixture.Make(_root, "2026-11-05-Thu", "other", [(0, "Nothing here")]);
        _index = SearchIndex.Open(_path);
    }

    public void Dispose()
    {
        _index.Dispose();
        try { Directory.Delete(_tmp, recursive: true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private RefreshStats Refresh(params string[] roots) =>
        _index.Refresh(roots.Length == 0 ? [_root] : roots, _warn.Add);

    private static void Bump(string file, long seconds) =>
        File.SetLastWriteTimeUtc(file, File.GetLastWriteTimeUtc(file).AddSeconds(seconds));

    private long Scalar(string sql)
    {
        using var cmd = _index.Connection.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    [Fact]
    public void Fts5_IsAvailable()
    {
        using var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "CREATE VIRTUAL TABLE t USING fts5(text, tokenize='unicode61 remove_diacritics 2')";
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public void FirstRefreshAddsEveryMeeting()
    {
        var s = Refresh();
        Assert.Equal((2, 0, 0), (s.Added, s.Updated, s.Removed));
    }

    [Fact]
    public void SecondRefreshWithNoChangesDoesNothing()
    {
        Refresh();
        Assert.Equal(new RefreshStats(0, 0, 0, 0), Refresh());
    }

    [Fact]
    public void ChangedTranscriptIsReindexed()
    {
        Refresh();
        var t = Path.Combine(_a, "transcript.json");
        File.WriteAllText(t, JsonSerializer.Serialize(new
        {
            transcription = new[] { new { offsets = new { from = 0, to = 1 }, text = " brand new words" } },
        }));
        Bump(t, 5);
        Assert.Equal(1, Refresh().Updated);
        Assert.Contains("brand", _index.Vocabulary());
        Assert.DoesNotContain("diocese", _index.Vocabulary());
    }

    [Fact]
    public void ReplacedTranscriptWithOriginalMtimeIsReindexed()
    {
        Refresh();
        var t = Path.Combine(_a, "transcript.json");
        var mtime = File.GetLastWriteTimeUtc(t);
        File.WriteAllText(t, JsonSerializer.Serialize(new
        {
            transcription = new[] { new { offsets = new { from = 0, to = 1 }, text = " zebra" } },
        }));
        File.SetLastWriteTimeUtc(t, mtime);
        Assert.Equal(1, Refresh().Updated);
        Assert.Contains("zebra", _index.Vocabulary());
    }

    [Fact]
    public void RemovedMeetingIsDropped()
    {
        Refresh();
        Directory.Delete(_b, recursive: true);
        Assert.Equal(1, Refresh().Removed);
    }

    [Fact]
    public void MovedFolderIsReplacedNotDuplicated()
    {
        Refresh();
        var arch = Path.Combine(_root, "archives");
        Directory.CreateDirectory(arch);
        Directory.Move(Path.Combine(_root, "2026-11-04-Wed"), Path.Combine(arch, "2026-11-04-Wed"));
        Refresh(_root, arch);
        var dirs = new List<string>();
        using (var cmd = _index.Connection.CreateCommand())
        {
            cmd.CommandText = "select dir from meetings";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                dirs.Add(r.GetString(0));
        }
        Assert.Equal(2, dirs.Count);
        var marker = Path.DirectorySeparatorChar + "archives" + Path.DirectorySeparatorChar;
        Assert.Contains(dirs, d => d.Contains(marker));
    }

    [Fact]
    public void BrokenTranscriptIsSkippedWithAWarningThenPickedUp()
    {
        var t = Path.Combine(_b, "transcript.json");
        var good = File.ReadAllText(t);
        File.WriteAllText(t, good[..10]);
        var s = Refresh();
        Assert.Equal((1, 1), (s.Added, s.Skipped));
        Assert.Single(_warn);
        Assert.Contains(_b, _warn[0]);
        File.WriteAllText(t, good);
        Bump(t, 5);
        Assert.Equal(1, Refresh().Added);
    }

    [Fact]
    public void OldSchemaOrGarbageFileIsRebuilt()
    {
        _index.Dispose();
        File.WriteAllBytes(_path, "not a database"u8.ToArray());
        _index = SearchIndex.Open(_path);
        Assert.Equal(SearchIndex.SchemaVersion, Scalar("pragma user_version"));
    }

    [Fact]
    public void OtherUserVersionIsRebuilt()
    {
        Refresh();
        Scalar("pragma user_version = 99");
        _index.Dispose();
        _index = SearchIndex.Open(_path);
        Assert.Equal(0, Scalar("select count(*) from meetings"));
        Assert.Equal(0, _index.MeetingCount());
    }

    [Fact]
    public void VocabularyContainsTranscriptAndNameWords()
    {
        Refresh();
        Assert.Contains("diocese", _index.Vocabulary());
        Assert.Contains("keys", _index.Vocabulary());
        Assert.Contains("catholic", _index.Vocabulary());
    }
}
