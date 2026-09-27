using System.Text;
using System.Xml.Linq;
using Mediathekarr;
using Mediathekarr.Data;
using Mediathekarr.Services;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(o => o.ListenAnyIP(8098));

var cfg = new Settings();
builder.Services.AddSingleton(cfg);
builder.Services.AddSingleton(new Database(cfg.DbPath));
builder.Services.AddHttpClient();
builder.Services.AddHttpClient("download", c => c.Timeout = TimeSpan.FromHours(2));
builder.Services.AddHttpClient<MediathekViewClient>(c => c.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddHttpClient<ArrClient>(c => c.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddSingleton<SearchService>();
builder.Services.AddSingleton<Fetcher>();
builder.Services.AddSingleton<FilmIndex>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<FilmIndex>());
builder.Services.AddSingleton<Thumbnails>();
// Manche Sender liefern ihre Seiten nur an etwas, das wie ein Browser aussieht.
builder.Services.AddHttpClient("bilder", c =>
{
    c.Timeout = TimeSpan.FromSeconds(15);
    c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; mediathekarr)");
});
builder.Services.AddSingleton<DirectDownloader>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<DirectDownloader>());
builder.Services.AddHostedService<BlackholeWatcher>();
// Zustaende und Zeiten als lesbare Werte statt Zahlen.
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));

var app = builder.Build();
app.UseDefaultFiles();
// "no-cache" heisst "vor Gebrauch nachfragen", nicht "nie speichern". Ohne
// die Angabe setzt der Browser nach einem Update das alte Stylesheet auf die
// neue Seite. Die Nachfrage kostet dank ETag nur ein 304.
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-cache",
});

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// ---------------------------------------------------------------- Newznab
// Sonarr, Radarr und Prowlarr sprechen diese Schnittstelle. Sie ist alt und
// schlicht: XML ueber GET, zwei Handvoll Parameter.
app.MapGet("/api", async (HttpRequest req, SearchService search, CancellationToken ct) =>
{
    var t = (string?)req.Query["t"] ?? "caps";
    if (t is "caps") return Results.Text(Caps(), "application/xml", Encoding.UTF8);

    var q = ((string?)req.Query["q"] ?? "").Trim();
    var imdb = ((string?)req.Query["imdbid"] ?? "").Trim();
    _ = int.TryParse(req.Query["season"], out var season);
    _ = int.TryParse(req.Query["ep"], out var ep);

    var releases = t switch
    {
        "tvsearch" => await search.TvAsync(q, season, ep, ct),
        "movie" => await search.MovieAsync(q, imdb, ct),
        _ => await search.FreeAsync(q, ct),
    };

    var baseUrl = $"{req.Scheme}://{req.Host}";
    return Results.Text(Feed(releases, baseUrl), "application/rss+xml", Encoding.UTF8);
});

// Die .nzb, die Sonarr in den Blackhole-Ordner legt. Sie enthaelt keine
// Usenet-Daten, sondern nur unsere Id - der Abholer braucht nicht mehr.
app.MapGet("/nzb/{id}", (string id, Database db) =>
{
    var r = db.Find(id);
    if (r is null) return Results.NotFound();
    return Results.File(Encoding.UTF8.GetBytes(Nzb(r)), "application/x-nzb", r.Name + ".nzb");
});

// ------------------------------------------------------------ Oberflaeche

app.MapGet("/api/status", (Database db, DirectDownloader direct, FilmIndex index) => Results.Ok(new
{
    index = index.Current,
    version = typeof(Settings).Assembly.GetName().Version?.ToString(3),
    sender = cfg.Channels,
    mindestdauer = cfg.MinDurationSeconds,
    pfade = new { bibliothek = cfg.LibraryRoot, blackhole = cfg.BlackholeRoot, fertig = cfg.CompleteRoot },
    quelle = cfg.ApiUrl,
    laufend = direct.Running.Count,
}));

// ------------------------------------------------ Stoebern im eigenen Index

// Die Sender, die im Index stehen, mit ihren tatsaechlichen Namen
// (ZDFinfo, ARTE.DE ...). Solange der Index noch nicht fertig ist, die
// eingestellte Liste.
app.MapGet("/api/kanaele", (FilmIndex index) =>
{
    if (!index.Current.Ready)
        return Results.Ok(cfg.Channels.Select(c => new { name = c, sendungen = 0 }));
    using var c = index.Open();
    using var cmd = c.CreateCommand();
    cmd.CommandText = "SELECT channel, COUNT(*) FROM topic GROUP BY channel ORDER BY channel COLLATE NOCASE";
    using var r = cmd.ExecuteReader();
    var list = new List<object>();
    while (r.Read()) list.Add(new { name = r.GetString(0), sendungen = r.GetInt32(1) });
    return Results.Ok(list);
});

// Sendereihen eines Senders (oder aller), mit den Kategorien und ihrer
// Anzahl fuer die Auswahl oben. Eingeschraenkt nach Kategorie und Suchwort.
app.MapGet("/api/sendungen", (string? kanal, string? kategorie, string? q, FilmIndex index) =>
{
    if (!index.Current.Ready) return Results.Ok(new { bereit = false });
    using var c = index.Open();

    var where = new List<string>();
    using var counts = c.CreateCommand();
    using var list = c.CreateCommand();
    foreach (var cmd in new[] { counts, list })
    {
        if (!string.IsNullOrWhiteSpace(kanal)) cmd.Parameters.AddWithValue("$k", kanal);
        if (!string.IsNullOrWhiteSpace(q)) cmd.Parameters.AddWithValue("$q", $"%{q.Trim()}%");
        if (!string.IsNullOrWhiteSpace(kategorie)) cmd.Parameters.AddWithValue("$cat", kategorie);
    }
    if (!string.IsNullOrWhiteSpace(kanal)) where.Add("channel = $k");
    if (!string.IsNullOrWhiteSpace(q)) where.Add("topic LIKE $q");
    var baseWhere = where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "";

    counts.CommandText = $"SELECT category, COUNT(*) FROM topic{baseWhere} GROUP BY category";
    var cats = new Dictionary<string, int>();
    using (var r = counts.ExecuteReader())
        while (r.Read()) cats[r.GetString(0)] = r.GetInt32(1);

    if (!string.IsNullOrWhiteSpace(kategorie)) where.Add("category = $cat");
    var fullWhere = where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "";
    // Grosse Reihen zuerst waere eine Hitparade; A-Z findet man wieder.
    list.CommandText = $"""
        SELECT channel, topic, category, films, newest FROM topic{fullWhere}
         ORDER BY topic COLLATE NOCASE LIMIT 3000
        """;
    var shows = new List<object>();
    using (var r = list.ExecuteReader())
        while (r.Read())
            shows.Add(new
            {
                kanal = r.GetString(0), name = r.GetString(1), kategorie = r.GetString(2),
                folgen = r.GetInt32(3), neueste = DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(4)),
            });

    return Results.Ok(new
    {
        bereit = true,
        kategorien = Categorizer.All.Where(cats.ContainsKey).Select(k => new { name = k, anzahl = cats[k] }),
        sendungen = shows,
    });
});

// Die Folgen einer Sendereihe aus dem Index, neueste zuerst.
app.MapGet("/api/folgen", (string kanal, string sendung, int? offset, FilmIndex index, DirectDownloader direct) =>
{
    if (!index.Current.Ready) return Results.Ok(new { total = 0, weiter = 0, items = Array.Empty<object>() });
    using var c = index.Open();
    using var cmd = c.CreateCommand();
    cmd.CommandText = """
        SELECT channel, topic, title, description, ts, duration, size, url, url_hd, url_low, subtitle,
               (SELECT COUNT(*) FROM film WHERE channel = $k AND topic = $t)
          FROM film WHERE channel = $k AND topic = $t ORDER BY ts DESC LIMIT 100 OFFSET $o
        """;
    cmd.Parameters.AddWithValue("$k", kanal);
    cmd.Parameters.AddWithValue("$t", sendung);
    cmd.Parameters.AddWithValue("$o", Math.Max(0, offset ?? 0));
    var items = new List<MediathekItem>();
    long total = 0;
    using (var r = cmd.ExecuteReader())
        while (r.Read())
        {
            items.Add(new MediathekItem(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3),
                r.GetInt64(4), r.GetInt32(5), r.GetInt64(6), r.GetString(7), r.GetString(8),
                r.GetString(9), r.GetString(10)));
            total = r.GetInt64(11);
        }
    direct.Remember(items);
    return Results.Ok(new
    {
        total,
        weiter = Math.Max(0, offset ?? 0) + 100,
        items = items.Select(i => ToView(i, direct)),
    });
});

// Vorschaubild einer Sendereihe, von der Webseite ihrer neuesten Folge.
app.MapGet("/api/bild", async (string kanal, string sendung, FilmIndex index, Thumbnails thumbs,
                               HttpResponse res) =>
{
    if (!index.Current.Ready) return Results.NotFound();
    string? website;
    using (var c = index.Open())
    using (var cmd = c.CreateCommand())
    {
        cmd.CommandText = "SELECT website FROM topic WHERE channel = $k AND topic = $t";
        cmd.Parameters.AddWithValue("$k", kanal);
        cmd.Parameters.AddWithValue("$t", sendung);
        website = cmd.ExecuteScalar() as string;
    }
    var file = website is null ? null : await thumbs.GetAsync(website);
    if (file is null) return Results.NotFound();
    // Ein Tag im Browser: das Bild einer Reihe aendert sich selten.
    res.Headers.CacheControl = "public, max-age=86400";
    return Results.File(file, "image/jpeg");
});

// Stoebern: die neuesten Sendungen einer Mediathek, seitenweise.
app.MapGet("/api/browse", async (string? channel, string? topic, string? q, int? offset, int? size,
                                 bool? kurz, MediathekViewClient mediathek, DirectDownloader direct,
                                 CancellationToken ct) =>
{
    var (items, total, next) = await mediathek.BrowseAsync(channel, topic, q, Math.Max(0, offset ?? 0),
        Math.Clamp(size ?? 50, 1, 200), kurz == true, ct);
    direct.Remember(items);
    return Results.Ok(new
    {
        total,
        weiter = next,
        items = items.Select(i => ToView(i, direct)),
    });
});

app.MapPost("/api/downloads", (DownloadInput input, DirectDownloader direct) =>
{
    var (queued, present, unknown) = direct.Enqueue(input.Ids ?? []);
    return Results.Ok(new { eingereiht = queued, vorhanden = present, unbekannt = unknown });
});

app.MapGet("/api/downloads", (Database db, DirectDownloader direct) =>
    Results.Ok(db.Jobs(200).Select(j =>
    {
        direct.Running.TryGetValue(j.RowId, out var p);
        return new
        {
            id = j.RowId, name = j.ReleaseName, art = j.Category, status = j.Status,
            fehler = j.Error, bytes = p?.Done ?? j.Bytes, gesamt = p?.Total ?? 0,
            erstellt = j.Created,
        };
    })));

// Was Sonarr und Radarr bei einer Suche angeboten bekaemen. Zum Ausprobieren,
// ob eine Sendung ueberhaupt gefunden wird.
app.MapGet("/api/search", async (string q, SearchService search, CancellationToken ct) =>
    Results.Ok((await search.FreeAsync(q, ct)).Select(r => new { r.Name, r.Channel, mb = r.Size / 1_000_000 })));

app.Run();
return;

// Ein Beitrag so, wie die Oberflaeche ihn braucht. Gemeinsam fuer die
// Live-Abfrage und den Index, damit beide Listen gleich aussehen.
static object ToView(MediathekItem i, DirectDownloader direct) => new
{
    i.Id, i.Channel, i.Topic, i.Title, i.Description,
    published = i.Published, i.Duration, i.Size,
    hd = !string.IsNullOrWhiteSpace(i.UrlVideoHd),
    untertitel = !string.IsNullOrWhiteSpace(i.UrlSubtitle),
    vorhanden = direct.Exists(i),
    laedt = direct.IsPending(i.Id),
};

static string Caps() =>
    """
    <?xml version="1.0" encoding="UTF-8"?>
    <caps>
      <server title="Mediathekarr" />
      <limits max="100" default="60" />
      <searching>
        <search available="yes" supportedParams="q" />
        <tv-search available="yes" supportedParams="q,season,ep" />
        <movie-search available="yes" supportedParams="q,imdbid" />
        <music-search available="no" supportedParams="" />
        <book-search available="no" supportedParams="" />
      </searching>
      <categories>
        <category id="2000" name="Movies"><subcat id="2040" name="Movies/HD" /></category>
        <category id="5000" name="TV"><subcat id="5040" name="TV/HD" /></category>
      </categories>
    </caps>
    """;

static string Feed(IReadOnlyList<Release> releases, string baseUrl)
{
    XNamespace nn = "http://www.newznab.com/DTD/2010/feeds/attributes/";
    var channel = new XElement("channel",
        new XElement("title", "Mediathekarr"),
        new XElement("description", "Oeffentlich-rechtliche Mediatheken als Indexer"),
        releases.Select(r =>
        {
            var link = $"{baseUrl}/nzb/{r.Id}";
            return new XElement("item",
                new XElement("title", r.Name),
                new XElement("guid", new XAttribute("isPermaLink", "false"), r.Id),
                new XElement("link", link),
                new XElement("comments", link),
                new XElement("pubDate", r.Published.ToString("r")),
                new XElement("category", r.Category),
                new XElement("enclosure",
                    new XAttribute("url", link),
                    new XAttribute("length", r.Size),
                    new XAttribute("type", "application/x-nzb")),
                new XElement(nn + "attr", new XAttribute("name", "category"), new XAttribute("value", r.Category)),
                new XElement(nn + "attr", new XAttribute("name", "size"), new XAttribute("value", r.Size)),
                new XElement(nn + "attr", new XAttribute("name", "grabs"), new XAttribute("value", 0)));
        }));

    var rss = new XElement("rss",
        new XAttribute("version", "2.0"),
        new XAttribute(XNamespace.Xmlns + "newznab", nn.NamespaceName),
        channel);
    return new XDocument(new XDeclaration("1.0", "UTF-8", null), rss).ToString();
}

static string Nzb(Release r)
{
    XNamespace ns = "http://www.newzbin.com/DTD/2003/nzb";
    var doc = new XDocument(
        new XDeclaration("1.0", "UTF-8", null),
        new XElement(ns + "nzb",
            new XElement(ns + "head",
                new XElement(ns + "meta", new XAttribute("type", "mediathekarr-id"), r.Id),
                new XElement(ns + "meta", new XAttribute("type", "title"), r.Name)),
            new XElement(ns + "file",
                new XAttribute("poster", "mediathekarr"),
                new XAttribute("date", r.Published.ToUnixTimeSeconds()),
                new XAttribute("subject", $"\"{r.Name}\""),
                new XElement(ns + "groups", new XElement(ns + "group", "alt.binaries.mediathek")),
                new XElement(ns + "segments",
                    new XElement(ns + "segment",
                        new XAttribute("bytes", r.Size), new XAttribute("number", 1), r.Id)))));
    return doc.ToString();
}

internal sealed record DownloadInput(string[]? Ids);
