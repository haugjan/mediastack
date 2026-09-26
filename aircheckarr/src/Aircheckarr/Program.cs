using Aircheckarr;
using Aircheckarr.Data;
using Aircheckarr.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o =>
{
    o.SingleLine = true;
    o.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
});

// Zustaende als Wort statt als Zahl, sonst muesste die Oberflaeche die
// Reihenfolge der Aufzaehlung kennen.
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));

var settings = Settings.FromEnvironment();
builder.Services.AddSingleton(settings);
builder.Services.AddSingleton<Database>();
builder.Services.AddSingleton<QualityProbe>();
builder.Services.AddSingleton<IcyStreamListener>();
builder.Services.AddSingleton<PostProcessor>();
builder.Services.AddHttpClient<RadioBrowserClient>(c =>
{
    // Die Betreiber des Katalogs bitten ausdruecklich um einen sprechenden
    // Namen, damit sie normale von missbraeuchlicher Nutzung unterscheiden.
    c.DefaultRequestHeaders.UserAgent.ParseAdd("aircheckarr/1.0");
    c.Timeout = TimeSpan.FromSeconds(30);
});
builder.Services.AddHttpClient<LidarrClient>(c => c.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddSingleton<RecordingCoordinator>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RecordingCoordinator>());

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();

// ------------------------------------------------------------------ Zustand

app.MapGet("/api/status", (Database db, RecordingCoordinator co, LidarrClient lidarr) =>
{
    var stations = db.GetStations(limit: 100000);
    var wishes = db.GetWishes();
    return Results.Ok(new
    {
        version = typeof(Settings).Assembly.GetName().Version?.ToString(3),
        sender = new
        {
            gesamt = stations.Count,
            gemessen = stations.Count(s => s.MeasuredAt is not null),
            tauglich = stations.Count(s => s.PassesQuality(settings)),
            ausgewaehlt = stations.Count(s => s.Enabled),
            bereit = stations.Count(s => s.Enabled && s.PassesQuality(settings)),
            aktiv = co.Running.Count,
        },
        wuensche = new
        {
            offen = wishes.Count(w => w.FulfilledAt is null),
            erfuellt = wishes.Count(w => w.FulfilledAt is not null),
        },
        filter = new
        {
            mindestbitrate = settings.MinBitrateKbps,
            codecs = settings.AllowedCodecs,
            maxSender = settings.MaxConcurrentStations,
            schwelle = settings.MatchThreshold,
            minSekunden = settings.MinTrackSeconds,
            maxSekunden = settings.MaxTrackSeconds,
        },
        pfade = new
        {
            bibliothek = settings.LibraryPath,
            arbeit = settings.WorkPath,
            datenbank = settings.DatabasePath,
        },
        lidarr = new
        {
            verbunden = lidarr.Configured,
            abgleichMinuten = lidarr.Configured ? settings.LidarrSyncMinutes : 0,
            maxAlben = settings.LidarrMaxAlbums,
            letzterAbgleich = co.LastLidarrSync,
        },
        katalog = settings.RadioBrowserUrl,
    });
});

app.MapGet("/api/activity", (RecordingCoordinator co) =>
    Results.Ok(co.Running.Values
        .OrderByDescending(l => l.Recording).ThenBy(l => l.Station.Name)
        .Select(l => new
        {
            l.Station.Id, l.Station.Name, l.Station.Country,
            bitrate = l.Station.MeasuredBitrate,
            codec = l.Station.MeasuredCodec,
            seit = l.Since,
            interpret = l.Artist,
            titel = l.Title,
            titelSeit = l.TitleSince,
            nimmtAuf = l.Recording,
        })));

// Fuer Uptime Kuma und die Startseite: muss ohne Anmeldung antworten.
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// ----------------------------------------------------------------- Wuensche

app.MapGet("/api/wishes", (Database db) => Results.Ok(db.GetWishes()));

app.MapPost("/api/wishes", async (WishInput input, Database db) =>
{
    if (string.IsNullOrWhiteSpace(input.Artist) || string.IsNullOrWhiteSpace(input.Title))
        return Results.BadRequest(new { fehler = "Interpret und Titel sind noetig" });

    var id = await db.AddWishAsync(new Wish
    {
        Artist = input.Artist.Trim(),
        Title = input.Title.Trim(),
        Album = string.IsNullOrWhiteSpace(input.Album) ? null : input.Album.Trim(),
        NormalizedArtist = TitleMatcher.Normalize(input.Artist),
        NormalizedTitle = TitleMatcher.Normalize(input.Title),
    });
    return Results.Ok(new { id, bekannt = id == 0 });
});

app.MapPost("/api/wishes/delete", async (WishIdsInput input, Database db) =>
{
    await db.DeleteWishesAsync(input.Ids ?? []);
    return Results.Ok();
});

// Sofortiger Abgleich. Laeuft sonst ohnehin alle paar Minuten von selbst.
app.MapPost("/api/wishes/sync-lidarr", async (LidarrClient lidarr, RecordingCoordinator co,
                                              CancellationToken ct) =>
{
    if (!lidarr.Configured)
        return Results.BadRequest(new { fehler = "LIDARR_URL und LIDARR_API_KEY fehlen" });

    var result = await co.SyncLidarrAsync(ct);
    return result is null
        ? Results.Json(new { fehler = "Lidarr antwortet nicht" }, statusCode: 502)
        : Results.Ok(new { fehlend = result.Wanted, neu = result.Added, erledigt = result.Removed });
});

// ------------------------------------------------------------------- Sender

app.MapGet("/api/stations", (Database db, RecordingCoordinator co) =>
{
    var running = co.Running;
    return Results.Ok(db.GetStations(limit: 5000).Select(s => new
    {
        s.Id, s.Name, s.Country, s.Tags, s.Url,
        katalogBitrate = s.CatalogBitrate,
        gemessenBitrate = s.MeasuredBitrate,
        gemessenCodec = s.MeasuredCodec,
        gemessen = s.MeasuredAt,
        fehler = s.MeasureError,
        s.Enabled,
        tauglich = s.PassesQuality(settings),
        laeuft = running.ContainsKey(s.Id),
    }));
});

// Durchsucht den Katalog, ohne etwas zu speichern. Uebernommen wird erst,
// was im Dialog angekreuzt und bestaetigt ist.
app.MapGet("/api/catalog", async (string? name, string? tag, string? country, int? limit,
                                  RadioBrowserClient catalog, Database db, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(tag)
        && string.IsNullOrWhiteSpace(country))
        return Results.BadRequest(new { fehler = "Name, Stilrichtung oder Land angeben" });

    var known = db.GetStations(limit: 100000).ToDictionary(s => s.Id);
    var found = await catalog.FetchAsync(tag, country, name, Math.Clamp(limit ?? 100, 1, 500), ct);
    return Results.Ok(found.Select(s => new
    {
        s.Id, s.Name, s.Country, s.Tags,
        katalogBitrate = s.CatalogBitrate,
        katalogCodec = s.CatalogCodec,
        bekannt = known.ContainsKey(s.Id),
        ausgewaehlt = known.TryGetValue(s.Id, out var k) && k.Enabled,
    }));
});

app.MapPost("/api/stations/add", async (StationIdsInput input, RecordingCoordinator co,
                                        CancellationToken ct) =>
{
    var ids = (input.Ids ?? []).Distinct().ToList();
    if (ids.Count == 0) return Results.BadRequest(new { fehler = "Kein Sender angegeben" });
    return Results.Ok(new { uebernommen = await co.AddStationsAsync(ids, ct) });
});

app.MapPost("/api/stations/refresh", async (CatalogInput input, RecordingCoordinator co,
                                            CancellationToken ct) =>
{
    var count = await co.RefreshCatalogAsync(input.Tag, input.Country,
                                             Math.Clamp(input.Limit, 1, 2000), input.Select, ct);
    return Results.Ok(new { geholt = count });
});

app.MapPost("/api/stations/enabled", async (StationIdsInput input, Database db,
                                            RecordingCoordinator co) =>
{
    var ids = input.Ids ?? [];
    await db.SetStationsEnabledAsync(ids, input.Enabled);
    // Abwaehlen soll sofort wirken, nicht erst in der naechsten Runde.
    if (!input.Enabled) co.StopListening(ids);
    return Results.Ok();
});

app.MapPost("/api/stations/delete", async (StationIdsInput input, Database db,
                                           RecordingCoordinator co) =>
{
    var ids = input.Ids ?? [];
    await db.DeleteStationsAsync(ids);
    co.StopListening(ids);
    return Results.Ok();
});

// -------------------------------------------------------------- Mitschnitte

app.MapGet("/api/captures", (Database db) => Results.Ok(db.GetCaptures(500)));

app.Run("http://0.0.0.0:8099");

internal sealed record WishInput(string Artist, string Title, string? Album);
internal sealed record WishIdsInput(long[]? Ids);
internal sealed record StationIdsInput(string[]? Ids, bool Enabled = true);
internal sealed record CatalogInput(string? Tag, string? Country, int Limit = 300, bool Select = false);
