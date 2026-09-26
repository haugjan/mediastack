using Microsoft.Data.Sqlite;
using Mediathekarr.Data;

namespace Mediathekarr.Data;

// Zwei Tabellen, mehr braucht es nicht: die angebotenen Releases, damit
// eine spaeter abgelegte .nzb wieder zugeordnet werden kann, und die
// Auftraege fuer die Anzeige im Browser.
//
// Die Releases sind ein Zwischenspeicher, kein Bestand: was Sonarr nie
// greift, faellt nach einer Woche wieder heraus.
public sealed class Database
{
    private readonly string _cs;

    public Database(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _cs = new SqliteConnectionStringBuilder { DataSource = path }.ToString();
        using var c = Open();
        Exec(c, """
            CREATE TABLE IF NOT EXISTS releases (
                id TEXT PRIMARY KEY, name TEXT NOT NULL, category INTEGER NOT NULL,
                size INTEGER NOT NULL, published INTEGER NOT NULL, video_url TEXT NOT NULL,
                subtitle_url TEXT NOT NULL, channel TEXT NOT NULL, seen INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS jobs (
                rowid INTEGER PRIMARY KEY AUTOINCREMENT, release_id TEXT NOT NULL,
                release_name TEXT NOT NULL, category TEXT NOT NULL, status TEXT NOT NULL,
                bytes INTEGER NOT NULL DEFAULT 0, error TEXT NOT NULL DEFAULT '',
                created INTEGER NOT NULL);
            """);
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_cs);
        c.Open();
        return c;
    }

    private static void Exec(SqliteConnection c, string sql, params (string, object?)[] args)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (k, v) in args) cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public void Remember(Release r)
    {
        using var c = Open();
        Exec(c, """
            INSERT INTO releases (id,name,category,size,published,video_url,subtitle_url,channel,seen)
            VALUES ($i,$n,$c,$s,$p,$v,$u,$h,$t)
            ON CONFLICT(id) DO UPDATE SET seen=$t;
            """,
            ("$i", r.Id), ("$n", r.Name), ("$c", r.Category), ("$s", r.Size),
            ("$p", r.Published.ToUnixTimeSeconds()), ("$v", r.VideoUrl),
            ("$u", r.SubtitleUrl), ("$h", r.Channel),
            ("$t", DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
    }

    public Release? Find(string id)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id,name,category,size,published,video_url,subtitle_url,channel FROM releases WHERE id=$i";
        cmd.Parameters.AddWithValue("$i", id);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new Release(r.GetString(0), r.GetString(1), r.GetInt32(2), r.GetInt64(3),
            DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(4)), r.GetString(5), r.GetString(6), r.GetString(7));
    }

    public long AddJob(string releaseId, string name, string category)
    {
        using var c = Open();
        Exec(c, """
            INSERT INTO jobs (release_id,release_name,category,status,created)
            VALUES ($i,$n,$c,'laeuft',$t);
            """,
            ("$i", releaseId), ("$n", name), ("$c", category),
            ("$t", DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT last_insert_rowid()";
        return (long)cmd.ExecuteScalar()!;
    }

    public void FinishJob(long rowId, string status, long bytes, string error = "")
    {
        using var c = Open();
        Exec(c, "UPDATE jobs SET status=$s, bytes=$b, error=$e WHERE rowid=$r",
            ("$s", status), ("$b", bytes), ("$e", error), ("$r", rowId));
    }

    public IReadOnlyList<Job> Jobs(int limit = 50)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT rowid,release_id,release_name,category,status,bytes,error,created FROM jobs ORDER BY rowid DESC LIMIT $l";
        cmd.Parameters.AddWithValue("$l", limit);
        using var r = cmd.ExecuteReader();
        var list = new List<Job>();
        while (r.Read())
            list.Add(new Job(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3),
                r.GetString(4), r.GetInt64(5), r.GetString(6),
                DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(7))));
        return list;
    }

    // Aufraeumen: angebotene Releases, die nie abgeholt wurden, verfallen.
    public void Prune(TimeSpan age)
    {
        using var c = Open();
        Exec(c, "DELETE FROM releases WHERE seen < $t",
            ("$t", DateTimeOffset.UtcNow.Subtract(age).ToUnixTimeSeconds()));
    }
}
