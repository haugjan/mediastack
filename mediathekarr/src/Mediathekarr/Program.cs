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
builder.Services.AddHostedService<BlackholeWatcher>();

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();

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

// Fuer die Oberflaeche.
app.MapGet("/api/status", (Database db) => Results.Ok(new
{
    jobs = db.Jobs().Select(j => new
    {
        j.ReleaseName, j.Category, j.Status, j.Error,
        mb = j.Bytes / 1_000_000,
        created = j.Created.ToLocalTime().ToString("dd.MM. HH:mm"),
    }),
}));

app.MapGet("/api/search", async (string q, SearchService search, CancellationToken ct) =>
    Results.Ok((await search.FreeAsync(q, ct)).Select(r => new { r.Name, r.Channel, mb = r.Size / 1_000_000 })));

app.Run();
return;

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
