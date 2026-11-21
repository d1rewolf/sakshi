using Microsoft.Data.Sqlite;

namespace Sakshi.Search;

public sealed record RefreshStats(int Added, int Updated, int Removed, int Skipped);

/// <summary>SQLite FTS5 index of meeting transcripts, refreshed incrementally.</summary>
public sealed class SearchIndex : IDisposable
{
    public const int SchemaVersion = 1;

    private static readonly string[] Schema =
    [
        "CREATE TABLE meetings(id INTEGER PRIMARY KEY, dir TEXT UNIQUE NOT NULL, day TEXT NOT NULL, name TEXT NOT NULL, signature INTEGER NOT NULL)",
        "CREATE TABLE lines(id INTEGER PRIMARY KEY, meeting_id INTEGER NOT NULL REFERENCES meetings(id) ON DELETE CASCADE, source TEXT NOT NULL, start_ms INTEGER, text TEXT NOT NULL)",
        "CREATE INDEX lines_meeting ON lines(meeting_id)",
        "CREATE VIRTUAL TABLE lines_fts USING fts5(text, content='lines', content_rowid='id', tokenize='unicode61 remove_diacritics 2')",
        "CREATE VIRTUAL TABLE lines_vocab USING fts5vocab(lines_fts, 'row')",
    ];

    internal SqliteConnection Connection { get; }

    private SearchIndex(SqliteConnection connection) => Connection = connection;

    public void Dispose() => Connection.Dispose();

    private static SqliteConnection Connect(string path)
    {
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false, // pooled connections would keep the file open after Dispose
        }.ToString());
        try
        {
            conn.Open();
            Exec(conn, "PRAGMA foreign_keys=ON");
            return conn;
        }
        catch
        {
            conn.Dispose();
            throw;
        }
    }

    internal static void Exec(SqliteConnection conn, string sql, params object?[] args)
    {
        using var cmd = Command(conn, sql, args);
        cmd.ExecuteNonQuery();
    }

    internal static SqliteCommand Command(SqliteConnection conn, string sql, params object?[] args)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        for (var i = 0; i < args.Length; i++)
            cmd.Parameters.AddWithValue("$p" + i, args[i] ?? DBNull.Value);
        return cmd;
    }

    internal static long Scalar(SqliteConnection conn, string sql, params object?[] args)
    {
        using var cmd = Command(conn, sql, args);
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    public static SearchIndex Open(string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        if (File.Exists(path))
        {
            SqliteConnection? conn = null;
            try
            {
                conn = Connect(path);
                if (Scalar(conn, "PRAGMA user_version") == SchemaVersion)
                {
                    Scalar(conn, "SELECT count(*) FROM meetings");
                    return new SearchIndex(conn);
                }
                conn.Dispose();
            }
            catch (SqliteException)
            {
                conn?.Dispose();
            }
            foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
                File.Delete(path + suffix);
        }
        var fresh = Connect(path);
        try
        {
            Exec(fresh, "BEGIN");
            foreach (var stmt in Schema)
                Exec(fresh, stmt);
            Exec(fresh, $"PRAGMA user_version = {SchemaVersion}");
            Exec(fresh, "COMMIT");
        }
        catch
        {
            fresh.Dispose();
            throw;
        }
        return new SearchIndex(fresh);
    }

    private void DeleteMeeting(long meetingId)
    {
        var old = new List<(long Id, string Text)>();
        using (var cmd = Command(Connection, "SELECT id, text FROM lines WHERE meeting_id=$p0", meetingId))
        using (var r = cmd.ExecuteReader())
            while (r.Read())
                old.Add((r.GetInt64(0), r.GetString(1)));
        foreach (var (id, text) in old)
            Exec(Connection, "INSERT INTO lines_fts(lines_fts, rowid, text) VALUES('delete', $p0, $p1)", id, text);
        Exec(Connection, "DELETE FROM lines WHERE meeting_id=$p0", meetingId);
        Exec(Connection, "DELETE FROM meetings WHERE id=$p0", meetingId);
    }

    private void InsertLine(long meetingId, string source, long? startMs, string text)
    {
        var id = Scalar(Connection,
            "INSERT INTO lines(meeting_id, source, start_ms, text) VALUES($p0,$p1,$p2,$p3) RETURNING id",
            meetingId, source, startMs, text);
        Exec(Connection, "INSERT INTO lines_fts(rowid, text) VALUES($p0, $p1)", id, text);
    }

    private static string NameText(string name) => name.Replace("_", " ").Replace("-", " ").Replace("/", " ");

    public RefreshStats Refresh(IEnumerable<string> roots, Action<string> warn)
    {
        int added = 0, updated = 0, removed = 0, skipped = 0;
        var found = new Dictionary<string, Meeting>(StringComparer.Ordinal);
        foreach (var m in Corpus.Discover(roots))
            found[m.Dir] = m;
        var indexed = new Dictionary<string, (long Id, long Signature)>(StringComparer.Ordinal);
        using (var cmd = Command(Connection, "SELECT id, dir, signature FROM meetings"))
        using (var r = cmd.ExecuteReader())
            while (r.Read())
                indexed[r.GetString(1)] = (r.GetInt64(0), r.GetInt64(2));

        foreach (var (d, (mid, _)) in indexed)
        {
            if (found.ContainsKey(d))
                continue;
            Exec(Connection, "BEGIN");
            DeleteMeeting(mid);
            Exec(Connection, "COMMIT");
            removed++;
        }

        foreach (var (d, m) in found)
        {
            var hasOld = indexed.TryGetValue(d, out var old);
            if (hasOld && old.Signature == m.Signature)
                continue;
            IReadOnlyList<Line> lines;
            try
            {
                lines = Corpus.ReadLines(m.Dir);
            }
            catch (Exception e) when (e is TranscriptException or IOException)
            {
                warn($"skipping {m.Dir}: {e.Message}");
                skipped++;
                continue;
            }
            Exec(Connection, "BEGIN");
            try
            {
                if (hasOld)
                    DeleteMeeting(old.Id);
                var mid = Scalar(Connection,
                    "INSERT INTO meetings(dir, day, name, signature) VALUES($p0,$p1,$p2,$p3) RETURNING id",
                    d, m.Day, m.Name, m.Signature);
                InsertLine(mid, "name", null, NameText(m.Name));
                foreach (var ln in lines)
                    InsertLine(mid, "transcript", ln.StartMs, ln.Text);
                Exec(Connection, "COMMIT");
            }
            catch
            {
                Exec(Connection, "ROLLBACK");
                throw;
            }
            if (hasOld) updated++; else added++;
        }
        return new RefreshStats(added, updated, removed, skipped);
    }

    public IReadOnlyList<string> Vocabulary()
    {
        var terms = new List<string>();
        using var cmd = Command(Connection, "SELECT term FROM lines_vocab");
        using var r = cmd.ExecuteReader();
        while (r.Read())
            terms.Add(r.GetString(0));
        return terms;
    }

    public int MeetingCount() => (int)Scalar(Connection, "SELECT count(*) FROM meetings");
}
