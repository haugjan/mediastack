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

        // Spalten, die spaeter dazukamen. SQLite kennt kein
        // "ADD COLUMN IF NOT EXISTS", also erst nachsehen.
        AddColumnIfMissing(c, "wish", "lidarr_artist_id", "INTEGER");
        AddColumnIfMissing(c, "wish", "lidarr_album_id", "INTEGER");
        AddColumnIfMissing(c, "wish", "lidarr_release_id", "INTEGER");
        AddColumnIfMissing(c, "wish", "lidarr_track_id", "INTEGER");
        AddColumnIfMissing(c, "capture", "imported_by_lidarr", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing(c, "wish", "expected_seconds", "REAL");
        AddColumnIfMissing(c, "station", "icy_delay", "REAL");
        AddColumnIfMissing(c, "station", "titles_seen", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing(c, "station", "silent_since", "TEXT");
        AddColumnIfMissing(c, "station", "status_kind", "TEXT");
        AddColumnIfMissing(c, "station", "watch_hits", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing(c, "station", "status_checked", "TEXT");

        log.LogInformation("Datenbank bereit: {Pfad}", settings.DatabasePath);
    }

    private static void AddColumnIfMissing(SqliteConnection c, string table, string column,
                                           string type)
    {
        using var check = c.CreateCommand();
        check.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = $n;";
        check.Parameters.AddWithValue("$n", column);
        if (Convert.ToInt64(check.ExecuteScalar()) > 0) return;

        using var alter = c.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {type};";
        alter.ExecuteNonQuery();
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

    /// <summary>
    /// Nimmt Sender aus dem Katalog auf. Mit <paramref name="select"/> werden
    /// sie zugleich zum Mithoeren ausgewaehlt, auch wenn sie schon bekannt
    /// waren; ohne bleibt eine fruehere Auswahl unangetastet.
    /// </summary>
    public async Task UpsertStationsAsync(IEnumerable<Station> stations, bool select = false) =>
        await WriteAsync(c =>
        {
            using var tx = c.BeginTransaction();
            using var cmd = c.CreateCommand();
            // Gemessene Werte NICHT ueberschreiben: der Katalog weiss es
            // schlechter als unsere eigene Messung.
            cmd.CommandText = """
                INSERT INTO station (id, name, url, country, tags, catalog_codec, catalog_bitrate, enabled)
                VALUES ($id, $name, $url, $country, $tags, $codec, $bitrate, $sel)
                ON CONFLICT(id) DO UPDATE SET
                    name = excluded.name, url = excluded.url,
                    country = excluded.country, tags = excluded.tags,
                    catalog_codec = excluded.catalog_codec,
                    catalog_bitrate = excluded.catalog_bitrate,
                    enabled = MAX(station.enabled, excluded.enabled);
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
                cmd.Parameters.AddWithValue("$sel", select ? 1 : 0);
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

    public async Task SetStationsEnabledAsync(IReadOnlyCollection<string> ids, bool enabled) =>
        await WriteAsync(c =>
        {
            using var tx = c.BeginTransaction();
            using var cmd = c.CreateCommand();
            // Wer einen stummen Sender von Hand wieder einschaltet, will ihm
            // eine neue Chance geben.
            cmd.CommandText = enabled
                ? "UPDATE station SET enabled = 1, silent_since = NULL WHERE id = $id;"
                : "UPDATE station SET enabled = 0 WHERE id = $id;";
            foreach (var id in ids)
            {
                cmd.Parameters.Clear();
                cmd.Parameters.AddWithValue("$id", id);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
            return 0;
        });

    public async Task AddStationTitlesAsync(string id, int count) =>
        await WriteAsync(c =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "UPDATE station SET titles_seen = titles_seen + $n WHERE id = $id;";
            cmd.Parameters.AddWithValue("$n", count);
            cmd.Parameters.AddWithValue("$id", id);
            return cmd.ExecuteNonQuery();
        });

    public async Task SetStatusKindAsync(string id, string kind) =>
        await WriteAsync(c =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "UPDATE station SET status_kind = $k, status_checked = $at WHERE id = $id;";
            cmd.Parameters.AddWithValue("$k", kind);
            cmd.Parameters.AddWithValue("$at", DateTime.UtcNow.ToString("o"));
            cmd.Parameters.AddWithValue("$id", id);
            return cmd.ExecuteNonQuery();
        });

    public async Task AddWatchHitAsync(string id) =>
        await WriteAsync(c =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "UPDATE station SET watch_hits = watch_hits + 1 WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            return cmd.ExecuteNonQuery();
        });

    public async Task MarkStationSilentAsync(string id) =>
        await WriteAsync(c =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "UPDATE station SET silent_since = $at WHERE id = $id;";
            cmd.Parameters.AddWithValue("$at", DateTime.UtcNow.ToString("o"));
            cmd.Parameters.AddWithValue("$id", id);
            return cmd.ExecuteNonQuery();
        });

    /// <summary>
    /// Verzoegerung nachfuehren. Gleitender Mittelwert statt letztem Wert:
    /// ein einzelner Schnitt an der falschen Stelle soll den Sender nicht
    /// dauerhaft verstellen.
    /// </summary>
    public async Task LearnIcyDelayAsync(string id, double delay) =>
        await WriteAsync(c =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                UPDATE station SET icy_delay =
                    CASE WHEN icy_delay IS NULL THEN $d ELSE icy_delay * 0.7 + $d * 0.3 END
                 WHERE id = $id;
                """;
            cmd.Parameters.AddWithValue("$d", delay);
            cmd.Parameters.AddWithValue("$id", id);
            return cmd.ExecuteNonQuery();
        });

    /// <summary>
    /// Entfernt Sender aus der Liste. Mitschnitte bleiben stehen, sie tragen
    /// den Sendernamen selbst und brauchen den Eintrag nicht mehr.
    /// </summary>
    public async Task DeleteStationsAsync(IReadOnlyCollection<string> ids) =>
        await WriteAsync(c =>
        {
            using var tx = c.BeginTransaction();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "DELETE FROM station WHERE id = $id;";
            foreach (var id in ids)
            {
                cmd.Parameters.Clear();
                cmd.Parameters.AddWithValue("$id", id);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
            return 0;
        });

    public List<Station> GetStations(bool onlyEnabled = false, bool onlyUnmeasured = false,
                                     int limit = 500)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT station.*,
                   (SELECT COUNT(*) FROM capture
                     WHERE capture.station_id = station.id AND capture.state = 'done') AS matches
              FROM station WHERE 1=1
            """
            + (onlyEnabled ? " AND enabled = 1" : "")
            + (onlyUnmeasured ? " AND measured_at IS NULL" : "")
            // Ausgewaehlte zuerst: sonst wartet ein frisch angekreuzter Sender
            // bei der Messung hinter Hunderten, die niemand hoeren will.
            + " ORDER BY enabled DESC, measured_bitrate DESC, catalog_bitrate DESC LIMIT $limit;";
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
        IcyDelay = r["icy_delay"] is DBNull or null ? null : Convert.ToDouble(r["icy_delay"]),
        TitlesSeen = Convert.ToInt32(r["titles_seen"]),
        SilentSince = r["silent_since"] is string ss ? DateTime.Parse(ss).ToUniversalTime() : null,
        Matches = Convert.ToInt32(r["matches"]),
        StatusKind = r["status_kind"] as string,
        WatchHits = Convert.ToInt32(r["watch_hits"]),
        StatusCheckedAt = r["status_checked"] is string sc ? DateTime.Parse(sc).ToUniversalTime() : null,
    };

    // -------------------------------------------------------------- Wuensche

    public async Task<long> AddWishAsync(Wish w) =>
        await WriteAsync(c =>
        {
            using var cmd = c.CreateCommand();
            // Doppelte Wuensche still schlucken, sonst scheitert der Import
            // aus Lidarr am ersten bereits bekannten Titel.
            cmd.CommandText = """
                INSERT INTO wish (artist, title, album, source, created_at, norm_artist, norm_title,
                                  lidarr_artist_id, lidarr_album_id, lidarr_release_id, lidarr_track_id,
                                  expected_seconds)
                VALUES ($a, $t, $al, $s, $c, $na, $nt, $lar, $lal, $lre, $ltr, $exp)
                ON CONFLICT(norm_artist, norm_title) DO NOTHING
                RETURNING id;
                """;
            AddLidarrIds(cmd, w);
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

    private static void AddLidarrIds(SqliteCommand cmd, Wish w)
    {
        cmd.Parameters.AddWithValue("$lar", (object?)w.LidarrArtistId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$lal", (object?)w.LidarrAlbumId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$lre", (object?)w.LidarrReleaseId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ltr", (object?)w.LidarrTrackId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$exp", (object?)w.ExpectedSeconds ?? DBNull.Value);
    }

    /// <summary>
    /// Gleicht die Wunschliste mit Lidarrs Fehlliste ab. Neue Titel kommen
    /// dazu, ein von Hand eingetragener Wunsch bekommt die Lidarr-Zuordnung,
    /// und offene Lidarr-Wuensche, die dort nicht mehr fehlen, fliegen raus:
    /// dann hat eine andere Quelle schneller geliefert, oder das Album wird
    /// nicht mehr beobachtet.
    /// </summary>
    public async Task<(int Added, int Removed)> SyncLidarrWishesAsync(IReadOnlyList<Wish> wanted) =>
        await WriteAsync(c =>
        {
            using var tx = c.BeginTransaction();
            var added = 0;

            using (var cmd = c.CreateCommand())
            {
                // Eine vorhandene Zuordnung bleibt stehen. Steht derselbe Titel
                // auf Single und Album, wuerde er sonst bei jedem Abgleich
                // zwischen beiden hin und her springen. Die Solllaenge wird
                // dagegen immer nachgetragen, auch bei schon verknuepften
                // Wuenschen, die aus der Zeit vor dieser Spalte stammen.
                cmd.CommandText = """
                    INSERT INTO wish (artist, title, album, source, created_at, norm_artist, norm_title,
                                      lidarr_artist_id, lidarr_album_id, lidarr_release_id, lidarr_track_id,
                                      expected_seconds)
                    VALUES ($a, $t, $al, 'lidarr', $c, $na, $nt, $lar, $lal, $lre, $ltr, $exp)
                    ON CONFLICT(norm_artist, norm_title) DO UPDATE SET
                        album = COALESCE(wish.album, excluded.album),
                        lidarr_artist_id = COALESCE(wish.lidarr_artist_id, excluded.lidarr_artist_id),
                        lidarr_album_id = COALESCE(wish.lidarr_album_id, excluded.lidarr_album_id),
                        lidarr_release_id = COALESCE(wish.lidarr_release_id, excluded.lidarr_release_id),
                        lidarr_track_id = COALESCE(wish.lidarr_track_id, excluded.lidarr_track_id),
                        expected_seconds = COALESCE(wish.expected_seconds, excluded.expected_seconds)
                    RETURNING (created_at = $c);
                    """;
                var now = DateTime.UtcNow.ToString("o");
                foreach (var w in wanted)
                {
                    cmd.Parameters.Clear();
                    cmd.Parameters.AddWithValue("$a", w.Artist);
                    cmd.Parameters.AddWithValue("$t", w.Title);
                    cmd.Parameters.AddWithValue("$al", (object?)w.Album ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("$c", now);
                    cmd.Parameters.AddWithValue("$na", w.NormalizedArtist);
                    cmd.Parameters.AddWithValue("$nt", w.NormalizedTitle);
                    AddLidarrIds(cmd, w);
                    if (cmd.ExecuteScalar() is long isNew && isNew == 1) added++;
                }
            }

            // Was Lidarr nicht mehr vermisst, ist erledigt. Nur offene Wuensche
            // aus Lidarr selbst; von Hand Eingetragenes bleibt immer stehen.
            using var ids = c.CreateCommand();
            ids.CommandText = "CREATE TEMP TABLE IF NOT EXISTS still_wanted (id INTEGER PRIMARY KEY);"
                            + "DELETE FROM still_wanted;";
            ids.ExecuteNonQuery();
            using (var ins = c.CreateCommand())
            {
                ins.CommandText = "INSERT OR IGNORE INTO still_wanted (id) VALUES ($id);";
                foreach (var w in wanted.Where(w => w.LidarrTrackId is not null))
                {
                    ins.Parameters.Clear();
                    ins.Parameters.AddWithValue("$id", w.LidarrTrackId!.Value);
                    ins.ExecuteNonQuery();
                }
            }
            using var del = c.CreateCommand();
            del.CommandText = """
                DELETE FROM wish
                 WHERE source = 'lidarr' AND fulfilled_at IS NULL
                   AND (lidarr_track_id IS NULL
                        OR lidarr_track_id NOT IN (SELECT id FROM still_wanted));
                """;
            var removed = del.ExecuteNonQuery();

            tx.Commit();
            return (added, removed);
        });

    public async Task DeleteWishesAsync(IReadOnlyCollection<long> ids) =>
        await WriteAsync(c =>
        {
            using var tx = c.BeginTransaction();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "DELETE FROM wish WHERE id = $id;";
            foreach (var id in ids)
            {
                cmd.Parameters.Clear();
                cmd.Parameters.AddWithValue("$id", id);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
            return 0;
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

    public Wish? GetWish(long id)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT * FROM wish WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadWish(r) : null;
    }

    public List<Wish> GetWishes(bool onlyOpen = false)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT * FROM wish"
            + (onlyOpen ? " WHERE fulfilled_at IS NULL" : "")
            + " ORDER BY created_at DESC;";
        using var r = cmd.ExecuteReader();
        var list = new List<Wish>();
        while (r.Read()) list.Add(ReadWish(r));
        return list;
    }

    private static int? NullableInt(SqliteDataReader r, string column) =>
        r[column] is DBNull or null ? null : Convert.ToInt32(r[column]);

    private static Wish ReadWish(SqliteDataReader r) => new()
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
        LidarrArtistId = NullableInt(r, "lidarr_artist_id"),
        LidarrAlbumId = NullableInt(r, "lidarr_album_id"),
        LidarrReleaseId = NullableInt(r, "lidarr_release_id"),
        LidarrTrackId = NullableInt(r, "lidarr_track_id"),
        ExpectedSeconds = r["expected_seconds"] is DBNull or null
            ? null : Convert.ToDouble(r["expected_seconds"]),
    };

    // ------------------------------------------------------------ Mitschnitte

    public async Task<long> AddCaptureAsync(Capture cap) =>
        await WriteAsync(c =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                INSERT INTO capture (wish_id, station_id, station_name, artist, title,
                                     started_at, seconds, bitrate, codec, path, state, reason,
                                     imported_by_lidarr)
                VALUES ($w, $sid, $sn, $a, $t, $st, $sec, $br, $co, $p, $state, $r, $lid)
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
            cmd.Parameters.AddWithValue("$lid", cap.ImportedByLidarr ? 1 : 0);
            return Convert.ToInt64(cmd.ExecuteScalar());
        });

    public Capture? GetCapture(long id) =>
        GetCaptures(1, id).FirstOrDefault();

    public List<Capture> GetCaptures(int limit = 100) => GetCaptures(limit, null);

    private List<Capture> GetCaptures(int limit, long? onlyId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT * FROM capture"
            + (onlyId is null ? "" : " WHERE id = $id")
            + " ORDER BY started_at DESC LIMIT $l;";
        cmd.Parameters.AddWithValue("$l", limit);
        if (onlyId is not null) cmd.Parameters.AddWithValue("$id", onlyId);
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
                ImportedByLidarr = Convert.ToInt32(r["imported_by_lidarr"]) == 1,
            });
        return list;
    }
}
