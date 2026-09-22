using Microsoft.Data.Sqlite;

namespace Aircheckarr.Data;

/// <summary>
/// Zugriff auf die SQLite-Datenbank mit handgeschriebenem SQL.
///
/// Bewusst ohne ORM: das Schema hat drei Tabellen und aendert sich selten,
/// dafuer bleibt sichtbar, was die Anwendung tut. Schreibzugriffe laufen
/// ueber ein Semaphore, weil SQLite nur einen Schreiber zulaesst und die
/// Aufnahmeleitung aus vielen Aufgaben gleichzeitig schreibt.
/// </summary>
public sealed class Database(Settings settings, ILogger<Database> log)
{
    private readonly string _connectionString =
        new SqliteConnectionStringBuilder
        {
            DataSource = settings.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        }.ToString();

    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        using var pragma = c.CreateCommand();
        // WAL, damit Lesen waehrend einer laufenden Aufnahme nicht blockiert.
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;";
        pragma.ExecuteNonQuery();
        return c;
    }

    public void Initialize()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(settings.DatabasePath)!);
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS station (
                id             TEXT PRIMARY KEY,
                name           TEXT NOT NULL,
                url            TEXT NOT NULL,
                country        TEXT,
                tags           TEXT,
                catalog_codec  TEXT,
                catalog_bitrate INTEGER NOT NULL DEFAULT 0,
                measured_codec TEXT,
                measured_bitrate INTEGER NOT NULL DEFAULT 0,
                measured_at    TEXT,
                measure_error  TEXT,
                enabled        INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS wish (
                id           INTEGER PRIMARY KEY AUTOINCREMENT,
                artist       TEXT NOT NULL,
                title        TEXT NOT NULL,
                album        TEXT,
                source       TEXT NOT NULL DEFAULT 'manual',
                created_at   TEXT NOT NULL,
                fulfilled_at TEXT,
                norm_artist  TEXT NOT NULL,
                norm_title   TEXT NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS wish_unique
                ON wish (norm_artist, norm_title);
            CREATE TABLE IF NOT EXISTS capture (
                id           INTEGER PRIMARY KEY AUTOINCREMENT,
                wish_id      INTEGER,
                station_id   TEXT NOT NULL,
                station_name TEXT NOT NULL,
                artist       TEXT NOT NULL,
                title        TEXT NOT NULL,
                started_at   TEXT NOT NULL,
                seconds      REAL NOT NULL DEFAULT 0,
                bitrate      INTEGER NOT NULL DEFAULT 0,
                codec        TEXT,
                path         TEXT,
                state        TEXT NOT NULL,
                reason       TEXT
            );
            CREATE INDEX IF NOT EXISTS capture_started ON capture (started_at DESC);
            """;
        cmd.ExecuteNonQuery();
        log.LogInformation("Datenbank bereit: {Pfad}", settings.DatabasePath);
    }

    private async Task<T> WriteAsync<T>(Func<SqliteConnection, T> work)
    {
        await _writeLock.WaitAsync();
        try
        {
            using var c = Open();
            return work(c);
        }
        finally { _writeLock.Release(); }
    }

    // ------------------------------------------------------------- Stationen

    public async Task UpsertStationsAsync(IEnumerable<Station> stations) =>
        await WriteAsync(c =>
        {
            using var tx = c.BeginTransaction();
            using var cmd = c.CreateCommand();
            // Gemessene Werte NICHT ueberschreiben: der Katalog weiss es
            // schlechter als unsere eigene Messung.
            cmd.CommandText = """
                INSERT INTO station (id, name, url, country, tags, catalog_codec, catalog_bitrate)
                VALUES ($id, $name, $url, $country, $tags, $codec, $bitrate)
                ON CONFLICT(id) DO UPDATE SET
                    name = excluded.name, url = excluded.url,
                    country = excluded.country, tags = excluded.tags,
                    catalog_codec = excluded.catalog_codec,
                    catalog_bitrate = excluded.catalog_bitrate;
                """;
            foreach (var s in stations)
            {
                cmd.Parameters.Clear();
                cmd.Parameters.AddWithValue("$id", s.Id);
                cmd.Parameters.AddWithValue("$name", s.Name);
                cmd.Parameters.AddWithValue("$url", s.Url);
                cmd.Parameters.AddWithValue("$country", (object?)s.Country ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$tags", (object?)s.Tags ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$codec", (object?)s.CatalogCodec ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$bitrate", s.CatalogBitrate);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
            return 0;
        });

    public async Task SaveMeasurementAsync(string id, string? codec, int bitrate, string? error) =>
        await WriteAsync(c =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                UPDATE station SET measured_codec = $codec, measured_bitrate = $bitrate,
                       measured_at = $at, measure_error = $err
                 WHERE id = $id;
                """;
            cmd.Parameters.AddWithValue("$codec", (object?)codec ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$bitrate", bitrate);
            cmd.Parameters.AddWithValue("$at", DateTime.UtcNow.ToString("o"));
            cmd.Parameters.AddWithValue("$err", (object?)error ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$id", id);
            return cmd.ExecuteNonQuery();
        });

    public async Task SetStationEnabledAsync(string id, bool enabled) =>
        await WriteAsync(c =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "UPDATE station SET enabled = $e WHERE id = $id;";
            cmd.Parameters.AddWithValue("$e", enabled ? 1 : 0);
            cmd.Parameters.AddWithValue("$id", id);
            return cmd.ExecuteNonQuery();
        });

    public List<Station> GetStations(bool onlyEnabled = false, bool onlyUnmeasured = false,
                                     int limit = 500)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT * FROM station WHERE 1=1"
            + (onlyEnabled ? " AND enabled = 1" : "")
            + (onlyUnmeasured ? " AND measured_at IS NULL" : "")
            + " ORDER BY measured_bitrate DESC, catalog_bitrate DESC LIMIT $limit;";
        cmd.Parameters.AddWithValue("$limit", limit);
        using var r = cmd.ExecuteReader();
        var list = new List<Station>();
        while (r.Read()) list.Add(ReadStation(r));
        return list;
    }

    private static Station ReadStation(SqliteDataReader r) => new()
    {
        Id = r.GetString(r.GetOrdinal("id")),
        Name = r.GetString(r.GetOrdinal("name")),
        Url = r.GetString(r.GetOrdinal("url")),
        Country = r["country"] as string,
        Tags = r["tags"] as string,
        CatalogCodec = r["catalog_codec"] as string,
        CatalogBitrate = Convert.ToInt32(r["catalog_bitrate"]),
        MeasuredCodec = r["measured_codec"] as string,
        MeasuredBitrate = Convert.ToInt32(r["measured_bitrate"]),
        MeasuredAt = r["measured_at"] is string s ? DateTime.Parse(s).ToUniversalTime() : null,
        MeasureError = r["measure_error"] as string,
        Enabled = Convert.ToInt32(r["enabled"]) == 1,
    };

    // -------------------------------------------------------------- Wuensche

    public async Task<long> AddWishAsync(Wish w) =>
        await WriteAsync(c =>
        {
            using var cmd = c.CreateCommand();
            // Doppelte Wuensche still schlucken, sonst scheitert der Import
            // aus Lidarr am ersten bereits bekannten Titel.
            cmd.CommandText = """
                INSERT INTO wish (artist, title, album, source, created_at, norm_artist, norm_title)
                VALUES ($a, $t, $al, $s, $c, $na, $nt)
                ON CONFLICT(norm_artist, norm_title) DO NOTHING
                RETURNING id;
                """;
            cmd.Parameters.AddWithValue("$a", w.Artist);
            cmd.Parameters.AddWithValue("$t", w.Title);
            cmd.Parameters.AddWithValue("$al", (object?)w.Album ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$s", w.Source);
            cmd.Parameters.AddWithValue("$c", w.CreatedAt.ToString("o"));
            cmd.Parameters.AddWithValue("$na", w.NormalizedArtist);
            cmd.Parameters.AddWithValue("$nt", w.NormalizedTitle);
            var id = cmd.ExecuteScalar();
            return id is null or DBNull ? 0L : Convert.ToInt64(id);
        });

    public async Task DeleteWishAsync(long id) =>
        await WriteAsync(c =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "DELETE FROM wish WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            return cmd.ExecuteNonQuery();
        });

    public async Task FulfillWishAsync(long id) =>
        await WriteAsync(c =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "UPDATE wish SET fulfilled_at = $at WHERE id = $id;";
            cmd.Parameters.AddWithValue("$at", DateTime.UtcNow.ToString("o"));
            cmd.Parameters.AddWithValue("$id", id);
            return cmd.ExecuteNonQuery();
        });

    public List<Wish> GetWishes(bool onlyOpen = false)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT * FROM wish"
            + (onlyOpen ? " WHERE fulfilled_at IS NULL" : "")
            + " ORDER BY created_at DESC;";
        using var r = cmd.ExecuteReader();
        var list = new List<Wish>();
        while (r.Read())
            list.Add(new Wish
            {
                Id = r.GetInt64(r.GetOrdinal("id")),
                Artist = r.GetString(r.GetOrdinal("artist")),
                Title = r.GetString(r.GetOrdinal("title")),
                Album = r["album"] as string,
                Source = r.GetString(r.GetOrdinal("source")),
                CreatedAt = DateTime.Parse(r.GetString(r.GetOrdinal("created_at"))).ToUniversalTime(),
                FulfilledAt = r["fulfilled_at"] is string f
                    ? DateTime.Parse(f).ToUniversalTime() : null,
                NormalizedArtist = r.GetString(r.GetOrdinal("norm_artist")),
                NormalizedTitle = r.GetString(r.GetOrdinal("norm_title")),
            });
        return list;
    }

    // ------------------------------------------------------------ Mitschnitte

    public async Task<long> AddCaptureAsync(Capture cap) =>
        await WriteAsync(c =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                INSERT INTO capture (wish_id, station_id, station_name, artist, title,
                                     started_at, seconds, bitrate, codec, path, state, reason)
                VALUES ($w, $sid, $sn, $a, $t, $st, $sec, $br, $co, $p, $state, $r)
                RETURNING id;
                """;
            cmd.Parameters.AddWithValue("$w", (object?)cap.WishId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$sid", cap.StationId);
            cmd.Parameters.AddWithValue("$sn", cap.StationName);
            cmd.Parameters.AddWithValue("$a", cap.Artist);
            cmd.Parameters.AddWithValue("$t", cap.Title);
            cmd.Parameters.AddWithValue("$st", cap.StartedAt.ToString("o"));
            cmd.Parameters.AddWithValue("$sec", cap.Seconds);
            cmd.Parameters.AddWithValue("$br", cap.Bitrate);
            cmd.Parameters.AddWithValue("$co", (object?)cap.Codec ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$p", (object?)cap.Path ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$state", cap.State.ToString().ToLowerInvariant());
            cmd.Parameters.AddWithValue("$r", (object?)cap.Reason ?? DBNull.Value);
            return Convert.ToInt64(cmd.ExecuteScalar());
        });

    public List<Capture> GetCaptures(int limit = 100)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT * FROM capture ORDER BY started_at DESC LIMIT $l;";
        cmd.Parameters.AddWithValue("$l", limit);
        using var r = cmd.ExecuteReader();
        var list = new List<Capture>();
        while (r.Read())
            list.Add(new Capture
            {
                Id = r.GetInt64(r.GetOrdinal("id")),
                WishId = r["wish_id"] is DBNull ? null : Convert.ToInt64(r["wish_id"]),
                StationId = r.GetString(r.GetOrdinal("station_id")),
                StationName = r.GetString(r.GetOrdinal("station_name")),
                Artist = r.GetString(r.GetOrdinal("artist")),
                Title = r.GetString(r.GetOrdinal("title")),
                StartedAt = DateTime.Parse(r.GetString(r.GetOrdinal("started_at"))).ToUniversalTime(),
                Seconds = Convert.ToDouble(r["seconds"]),
                Bitrate = Convert.ToInt32(r["bitrate"]),
                Codec = r["codec"] as string,
                Path = r["path"] as string,
                State = Enum.Parse<CaptureState>(r.GetString(r.GetOrdinal("state")), true),
                Reason = r["reason"] as string,
            });
        return list;
    }
}
