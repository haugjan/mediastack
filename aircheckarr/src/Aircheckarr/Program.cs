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

app.MapGet("/api/status", (Database db, RecordingCoordinator co) =>
{
    var stations = db.GetStations(limit: 100000);
    var wishes = db.GetWishes();
    return Results.Ok(new
    {
        sender = new
        {
            gesamt = stations.Count,
            gemessen = stations.Count(s => s.MeasuredAt is not null),
            tauglich = stations.Count(s => s.PassesQuality(settings)),
            aktiv = co.Running.Count,
        },
        wuensche = new
        {
            offen = wishes.Count(w => w.FulfilledAt is null),
            erfuellt = wishes.Count(w => w.FulfilledAt is not null),
        },
        laeuft = co.Running.Keys
            .Select(id => stations.FirstOrDefault(s => s.Id == id)?.Name ?? id)
            .OrderBy(n => n).ToArray(),
        filter = new
        {
            mindestbitrate = settings.MinBitrateKbps,
            codecs = settings.AllowedCodecs,
            maxSender = settings.MaxConcurrentStations,
        },
    });
});

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

app.MapDelete("/api/wishes/{id:long}", async (long id, Database db) =>
{
    await db.DeleteWishAsync(id);
    return Results.Ok();
});

app.MapPost("/api/wishes/import-lidarr", async (LidarrClient lidarr, Database db,
                                                CancellationToken ct) =>
{
    if (!lidarr.Configured)
        return Results.BadRequest(new { fehler = "LIDARR_URL und LIDARR_API_KEY fehlen" });

    var found = await lidarr.GetMissingTracksAsync(25, ct);
    var added = 0;
    foreach (var w in found)
        if (await db.AddWishAsync(w) != 0) added++;

    return Results.Ok(new { gefunden = found.Count, uebernommen = added });
});

// ------------------------------------------------------------------- Sender

app.MapGet("/api/stations", (Database db, bool? onlyUsable) =>
{
    var stations = db.GetStations(limit: 2000);
    if (onlyUsable == true) stations = stations.Where(s => s.PassesQuality(settings)).ToList();
    return Results.Ok(stations.Select(s => new
    {
        s.Id, s.Name, s.Country, s.Tags, s.Url,
        katalogBitrate = s.CatalogBitrate,
        gemessenBitrate = s.MeasuredBitrate,
        gemessenCodec = s.MeasuredCodec,
        gemessen = s.MeasuredAt,
        fehler = s.MeasureError,
        s.Enabled,
        tauglich = s.PassesQuality(settings),
    }));
});

app.MapPost("/api/stations/refresh", async (CatalogInput input, RecordingCoordinator co,
                                            CancellationToken ct) =>
{
    await co.RefreshCatalogAsync(input.Tag, input.Country, Math.Clamp(input.Limit, 1, 2000), ct);
    return Results.Ok();
});

app.MapPost("/api/stations/{id}/enabled", async (string id, EnabledInput input, Database db) =>
{
    await db.SetStationEnabledAsync(id, input.Enabled);
    return Results.Ok();
});

// -------------------------------------------------------------- Mitschnitte

app.MapGet("/api/captures", (Database db) => Results.Ok(db.GetCaptures(200)));

app.Run("http://0.0.0.0:8099");

internal sealed record WishInput(string Artist, string Title, string? Album);
internal sealed record CatalogInput(string? Tag, string? Country, int Limit = 300);
internal sealed record EnabledInput(bool Enabled);
