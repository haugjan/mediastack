using System.Diagnostics;
using System.Text.Json;
using Mediathekarr.Data;
using Microsoft.Data.Sqlite;

namespace Mediathekarr.Services;

// Ein eigener Index ueber alle Mediatheken, fuer das Stoebern nach Sender,
// Kategorie und Sendung. Die Live-Abfrage bei MediathekViewWeb liefert
// hoechstens 1000 Eintraege und braucht dafuer bis zu 15 Sekunden - daraus
// laesst sich keine Uebersicht A-Z bauen. Das Projekt MediathekView
// veroeffentlicht aber mehrmals taeglich die komplette "Filmliste": gut
// 700 000 Beitraege, 77 MB gepackt. Die wird einmal am Tag geholt.
//
// Drei Fallen:
//  - Das Format ist JSON mit hunderttausendmal demselben Schluessel "X".
//    Ein Objekt-Parser behielte nur den letzten; gelesen wird deshalb
//    Token fuer Token, und entpackt 475 MB passen nicht in einen String.
//  - Leere Felder fuer Sender und Thema heissen "wie im Eintrag davor".
//  - "Url Klein" und "Url HD" sind keine Adressen, sondern "Laenge|Rest":
//    die ersten Laenge Zeichen der normalen Url plus Rest.
//
// Neu aufgebaut wird in eine zweite Datei, die erst am Schluss die alte
// ersetzt. Stoebern geht also auch waehrend des Aufbaus weiter.
public sealed class FilmIndex(Settings cfg, IHttpClientFactory httpFactory, ILogger<FilmIndex> log)
    : BackgroundService
{
    public sealed record State(bool Ready, bool Building, DateTime? BuiltAt, int Films, int Topics, string? Error);

    private volatile State _state = new(false, false, null, 0, 0, null);
    public State Current => _state;

    private string DbPath => Path.Combine(Path.GetDirectoryName(cfg.DbPath)!, "filmliste.db");

    public SqliteConnection Open()
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DbPath, Mode = SqliteOpenMode.ReadOnly,
        }.ToString());
        c.Open();
        return c;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (File.Exists(DbPath)) _state = ReadState(null);
        while (!ct.IsCancellationRequested)
        {
            var age = File.Exists(DbPath) ? DateTime.UtcNow - File.GetLastWriteTimeUtc(DbPath) : TimeSpan.MaxValue;
            if (age > TimeSpan.FromHours(cfg.FilmlisteHours))
            {
                try { await BuildAsync(ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (Exception e)
                {
                    log.LogWarning("Filmliste liess sich nicht einlesen: {Fehler}", e.Message);
                    _state = _state with { Building = false, Error = e.Message };
                }
            }
            try { await Task.Delay(TimeSpan.FromMinutes(30), ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private State ReadState(string? error)
    {
        try
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT (SELECT COUNT(*) FROM film), (SELECT COUNT(*) FROM topic)";
            using var r = cmd.ExecuteReader();
            r.Read();
            return new State(true, false, File.GetLastWriteTimeUtc(DbPath), r.GetInt32(0), r.GetInt32(1), error);
        }
        catch (Exception e) when (e is SqliteException or InvalidOperationException)
        {
            return new State(false, false, null, 0, 0, error ?? e.Message);
        }
    }

    private async Task BuildAsync(CancellationToken ct)
    {
        _state = _state with { Building = true, Error = null };
        var dir = Path.GetDirectoryName(DbPath)!;
        var xz = Path.Combine(dir, "filmliste.xz.teil");
        var fresh = DbPath + ".neu";

        log.LogInformation("Lade Filmliste von {Url}", cfg.FilmlisteUrl);
        var http = httpFactory.CreateClient("download");
        using (var res = await http.GetAsync(cfg.FilmlisteUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            res.EnsureSuccessStatusCode();
            await using var src = await res.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(xz);
            await src.CopyToAsync(dst, ct);
        }

        var sw = Stopwatch.StartNew();
        if (File.Exists(fresh)) File.Delete(fresh);
        int films;
        using (var db = new SqliteConnection($"Data Source={fresh}"))
        {
            db.Open();
            Exec(db, """
                PRAGMA journal_mode=OFF; PRAGMA synchronous=OFF;
                CREATE TABLE film (
                    id TEXT NOT NULL, channel TEXT NOT NULL, topic TEXT NOT NULL, title TEXT NOT NULL,
                    description TEXT NOT NULL, ts INTEGER NOT NULL, duration INTEGER NOT NULL,
                    size INTEGER NOT NULL, url TEXT NOT NULL, url_hd TEXT NOT NULL, url_low TEXT NOT NULL,
                    subtitle TEXT NOT NULL, website TEXT NOT NULL,
                    UNIQUE (channel, topic, title, ts));
                """);
            films = await ImportAsync(db, xz, ct);
            // Vor der Auswertung, nicht danach: sie sucht je Sendereihe die
            // neueste Folge, ohne Index waeren das 11 000 volle Durchlaeufe.
            Exec(db, "CREATE INDEX film_topic ON film (channel, topic, ts DESC);");
            // Die Sendereihen samt Kategorie. Klassifiziert wird die Reihe,
            // nicht jede Folge: sonst stuende eine Krimireihe mit einer
            // Folge "Mord im Kloster" und einer "Die Hochzeit" in zwei Regalen.
            Exec(db, """
                CREATE TABLE topic (
                    channel TEXT NOT NULL, topic TEXT NOT NULL, category TEXT NOT NULL,
                    films INTEGER NOT NULL, newest INTEGER NOT NULL, website TEXT NOT NULL,
                    PRIMARY KEY (channel, topic));
                CREATE INDEX film_id ON film (id);
                """);
            var topics = new List<(string Channel, string Topic, int Films, long Newest, string Website, string Desc, string Titles)>();
            using (var q = db.CreateCommand())
            {
                // Webseite der neuesten Folge fuer das Vorschaubild (aeltere
                // Seiten sind oft schon fort), und die Beschreibungen der
                // fuenf neuesten fuer die Kategorie: eine allein gab im
                // Praxistest zu wenig her, jede zweite Reihe blieb "Weitere".
                q.CommandText = """
                    SELECT channel, topic, COUNT(*), MAX(ts),
                           (SELECT website FROM film f2 WHERE f2.channel = f.channel AND f2.topic = f.topic ORDER BY ts DESC LIMIT 1),
                           (SELECT group_concat(description, ' ') FROM
                               (SELECT description FROM film f2 WHERE f2.channel = f.channel AND f2.topic = f.topic
                                 ORDER BY ts DESC LIMIT 5)),
                           (SELECT group_concat(title, char(10)) FROM
                               (SELECT title FROM film f2 WHERE f2.channel = f.channel AND f2.topic = f.topic
                                 ORDER BY ts DESC LIMIT 10))
                      FROM film f GROUP BY channel, topic;
                    """;
                using var r = q.ExecuteReader();
                while (r.Read())
                    topics.Add((r.GetString(0), r.GetString(1), r.GetInt32(2), r.GetInt64(3), r.GetString(4),
                                r.IsDBNull(5) ? "" : r.GetString(5), r.IsDBNull(6) ? "" : r.GetString(6)));
            }
            using (var tx = db.BeginTransaction())
            using (var ins = db.CreateCommand())
            {
                ins.CommandText = "INSERT INTO topic VALUES ($c,$t,$k,$n,$ts,$w)";
                foreach (var t in topics)
                {
                    ins.Parameters.Clear();
                    ins.Parameters.AddWithValue("$c", t.Channel);
                    ins.Parameters.AddWithValue("$t", t.Topic);
                    ins.Parameters.AddWithValue("$k", Categorizer.Classify(t.Channel, t.Topic, t.Desc, t.Titles));
                    ins.Parameters.AddWithValue("$n", t.Films);
                    ins.Parameters.AddWithValue("$ts", t.Newest);
                    ins.Parameters.AddWithValue("$w", t.Website);
                    ins.ExecuteNonQuery();
                }
                tx.Commit();
            }
            Exec(db, "CREATE INDEX topic_category ON topic (category, channel);");
        }
        SqliteConnection.ClearAllPools();
        File.Move(fresh, DbPath, true);
        File.Delete(xz);
        _state = ReadState(null);
        log.LogInformation("Filmliste eingelesen: {Filme} Beitraege in {Sendungen} Sendungen, {Sekunden:F0} s",
            _state.Films, _state.Topics, sw.Elapsed.TotalSeconds);
    }

    private async Task<int> ImportAsync(SqliteConnection db, string xzPath, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("xz", ["-dc", xzPath])
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("xz liess sich nicht starten");
        var stderr = proc.StandardError.ReadToEndAsync(ct);
        await using var stream = proc.StandardOutput.BaseStream;

        using var tx = db.BeginTransaction();
        using var ins = db.CreateCommand();
        ins.CommandText = """
            INSERT OR IGNORE INTO film VALUES ($id,$c,$t,$ti,$d,$ts,$du,$s,$u,$uh,$ul,$sub,$w)
            """;
        foreach (var p in (string[])["$id", "$c", "$t", "$ti", "$d", "$ts", "$du", "$s", "$u", "$uh", "$ul", "$sub", "$w"])
            ins.Parameters.Add(p, SqliteType.Text);

        string channel = "", topic = "";
        var count = 0;
        var fields = new List<string>(20);
        var inX = false;          // naechstes Array gehoert zu einem "X"
        var collecting = false;   // gerade in einem solchen Array

        var buffer = new byte[1 << 20];
        var length = 0;
        var state = new JsonReaderState();
        while (true)
        {
            if (length == buffer.Length) Array.Resize(ref buffer, buffer.Length * 2);
            var read = await stream.ReadAsync(buffer.AsMemory(length), ct);
            var final = read == 0;
            length += read;

            var consumed = Scan(buffer.AsSpan(0, length), final, ref state, ref inX, ref collecting, fields, row =>
            {
                channel = row[0].Length > 0 ? row[0] : channel;
                topic = row[1].Length > 0 ? row[1] : topic;
                if (Keep(row, channel, topic, out var film))
                {
                    Bind(ins, film);
                    count += ins.ExecuteNonQuery();
                }
            });
            Array.Copy(buffer, consumed, buffer, 0, length - consumed);
            length -= consumed;
            if (final) break;
        }
        tx.Commit();

        await proc.WaitForExitAsync(ct);
        if (proc.ExitCode != 0) throw new InvalidOperationException($"xz: {(await stderr).Trim()}");
        return count;
    }

    // Eigene Methode, weil Utf8JsonReader ein ref struct ist und nicht ueber
    // ein await hinweg leben darf. Liefert, wie viele Bytes verarbeitet sind.
    private static int Scan(Span<byte> data, bool final, ref JsonReaderState state, ref bool inX,
                            ref bool collecting, List<string> fields, Action<List<string>> onRow)
    {
        var reader = new Utf8JsonReader(data, final, state);
        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.PropertyName:
                    inX = reader.ValueTextEquals("X"u8);
                    break;
                case JsonTokenType.StartArray when inX:
                    collecting = true;
                    fields.Clear();
                    break;
                case JsonTokenType.String when collecting:
                    fields.Add(reader.GetString() ?? "");
                    break;
                case JsonTokenType.EndArray when collecting:
                    collecting = false;
                    inX = false;
                    if (fields.Count >= 17) onRow(fields);
                    break;
            }
        }
        state = reader.CurrentState;
        return (int)reader.BytesConsumed;
    }

    private sealed record Film(string Id, string Channel, string Topic, string Title, string Description,
                               long Ts, int Duration, long Size, string Url, string UrlHd, string UrlLow,
                               string Subtitle, string Website);

    // Filmliste-Felder: 0 Sender, 1 Thema, 2 Titel, 5 Dauer, 6 Groesse MB,
    // 7 Beschreibung, 8 Url, 9 Website, 10 Untertitel, 12 Klein, 14 HD, 16 DatumL.
    private bool Keep(List<string> x, string channel, string topic, out Film film)
    {
        film = null!;
        if (cfg.Channels.Length > 0 &&
            !cfg.Channels.Any(c => channel.Contains(c, StringComparison.OrdinalIgnoreCase))) return false;

        var duration = TimeSpan.TryParse(x[5], out var d) ? (int)d.TotalSeconds : 0;
        if (duration < cfg.MinDurationSeconds) return false;
        var title = x[2];
        if (MediathekViewClient.IsAccessibilityVersion(title)) return false;

        var url = x[8];
        if (string.IsNullOrWhiteSpace(url)) return false;
        var hd = Expand(url, x[14]);
        var low = Expand(url, x[12]);
        var best = !string.IsNullOrWhiteSpace(hd) ? hd : url;
        _ = long.TryParse(x[16], out var ts);
        _ = long.TryParse(x[6], out var mb);

        film = new Film(MediathekItem.IdFor(best), channel, topic, title, x[7], ts, duration,
                        mb * 1_000_000, url, hd, low, x[10], x[9]);
        return true;
    }

    private static string Expand(string url, string packed)
    {
        if (string.IsNullOrEmpty(packed)) return "";
        var bar = packed.IndexOf('|');
        if (bar < 0 || !int.TryParse(packed[..bar], out var keep) || keep > url.Length) return packed;
        return url[..keep] + packed[(bar + 1)..];
    }

    private static void Bind(SqliteCommand ins, Film f)
    {
        var p = ins.Parameters;
        p["$id"].Value = f.Id; p["$c"].Value = f.Channel; p["$t"].Value = f.Topic;
        p["$ti"].Value = f.Title; p["$d"].Value = f.Description; p["$ts"].Value = f.Ts;
        p["$du"].Value = f.Duration; p["$s"].Value = f.Size; p["$u"].Value = f.Url;
        p["$uh"].Value = f.UrlHd; p["$ul"].Value = f.UrlLow; p["$sub"].Value = f.Subtitle;
        p["$w"].Value = f.Website;
    }

    private static void Exec(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
