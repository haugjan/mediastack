using System.Collections.Concurrent;
using Aircheckarr.Data;

namespace Aircheckarr.Services;

/// <summary>
/// Haelt den Betrieb am Laufen: Katalog fuellen, Sender nachmessen, die
/// besten mithoeren, Treffer nachbearbeiten und einsortieren.
///
/// Der Ablauf ist bewusst gedrosselt. Ein Messlauf ueber 60000 Sender waere
/// weder noetig noch hoeflich, und mehr als eine Handvoll gleichzeitig
/// mitgehoerter Sender bringt nichts: gespielt wird ohnehin ueberall
/// dasselbe, und jede Verbindung kostet Bandbreite rund um die Uhr.
/// </summary>
public sealed class RecordingCoordinator(
    Database db, Settings settings, RadioBrowserClient catalog, QualityProbe probe,
    IcyStreamListener listener, PostProcessor post, LidarrClient lidarr,
    ILogger<RecordingCoordinator> log) : BackgroundService
{
    private volatile List<Wish> _openWishes = [];
    private readonly ConcurrentDictionary<string, DateTime> _running = new();

    /// <summary>Was gerade mitgehoert wird, fuer die Oberflaeche.</summary>
    public IReadOnlyDictionary<string, DateTime> Running => _running;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        db.Initialize();

        if (db.GetStations(limit: 1).Count == 0)
        {
            log.LogInformation("Kein Sender bekannt, Katalog wird erstmalig geholt");
            await RefreshCatalogAsync(null, null, 400, ct);
        }

        var measuring = Task.Run(() => MeasureLoopAsync(ct), ct);
        var listening = Task.Run(() => ListenLoopAsync(ct), ct);
        var wishes = Task.Run(() => WishLoopAsync(ct), ct);
        await Task.WhenAll(measuring, listening, wishes);
    }

    public async Task RefreshCatalogAsync(string? tag, string? country, int limit,
                                          CancellationToken ct)
    {
        var stations = await catalog.FetchAsync(tag, country, limit, ct);
        await db.UpsertStationsAsync(stations);
    }

    // --------------------------------------------------------------- Messen

    private async Task MeasureLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var todo = db.GetStations(onlyUnmeasured: true, limit: 40);
            if (todo.Count == 0)
            {
                await Task.Delay(TimeSpan.FromMinutes(10), ct);
                continue;
            }

            log.LogInformation("{Zahl} Sender werden nachgemessen", todo.Count);
            // Vier gleichzeitig: mehr bringt nichts, jede Messung wartet
            // ohnehin ueberwiegend auf das Netz.
            using var slots = new SemaphoreSlim(4);
            await Task.WhenAll(todo.Select(async station =>
            {
                await slots.WaitAsync(ct);
                try
                {
                    var result = await probe.ProbeAsync(station.Url, ct);
                    await db.SaveMeasurementAsync(station.Id, result.Codec,
                                                  result.BitrateKbps, result.Error);

                    var measured = station with
                    {
                        MeasuredCodec = result.Codec,
                        MeasuredBitrate = result.BitrateKbps,
                        MeasuredAt = DateTime.UtcNow,
                        MeasureError = result.Error,
                    };
                    // Was die Messung besteht, wird von selbst scharfgeschaltet.
                    if (measured.PassesQuality(settings))
                        await db.SetStationEnabledAsync(station.Id, true);
                }
                finally { slots.Release(); }
            }));
        }
    }

    // -------------------------------------------------------------- Wuensche

    private async Task WishLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            _openWishes = db.GetWishes(onlyOpen: true);
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
        }
    }

    /// <summary>
    /// Wird bei jedem Titelwechsel aufgerufen. Muss schnell sein, deshalb
    /// laeuft der Vergleich gegen eine Kopie der Wunschliste im Speicher.
    /// </summary>
    private long? MatchWish(string artist, string title)
    {
        var wishes = _openWishes;
        long? best = null;
        var bestScore = settings.MatchThreshold;

        foreach (var w in wishes)
        {
            var score = TitleMatcher.Score(artist, title, w.NormalizedArtist, w.NormalizedTitle);
            if (score < bestScore) continue;
            bestScore = score;
            best = w.Id;
        }
        return best;
    }

    // --------------------------------------------------------------- Hoeren

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (_openWishes.Count == 0)
            {
                // Ohne Wuensche waere Mithoeren reine Bandbreitenverschwendung.
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
                continue;
            }

            var candidates = db.GetStations(onlyEnabled: true, limit: 200)
                .Where(s => s.PassesQuality(settings))
                .Where(s => !_running.ContainsKey(s.Id))
                .Take(Math.Max(0, settings.MaxConcurrentStations - _running.Count))
                .ToList();

            foreach (var station in candidates)
            {
                _running[station.Id] = DateTime.UtcNow;
                _ = Task.Run(() => ListenToStationAsync(station, ct), ct);
            }

            await Task.Delay(TimeSpan.FromSeconds(20), ct);
        }
    }

    private async Task ListenToStationAsync(Station station, CancellationToken ct)
    {
        try
        {
            await listener.ListenAsync(
                station, settings.WorkPath, station.MeasuredBitrate,
                MatchWish,
                (segment, wishId) => HandleSegmentAsync(station, segment, wishId, ct),
                ct);
        }
        catch (OperationCanceledException) { /* Abschalten, kein Fehler */ }
        catch (Exception ex)
        {
            log.LogWarning("{Sender} abgebrochen: {Fehler}", station.Name, ex.Message);
        }
        finally
        {
            _running.TryRemove(station.Id, out _);
        }
    }

    private async Task HandleSegmentAsync(Station station, IcyStreamListener.Segment segment,
                                          long wishId, CancellationToken ct)
    {
        var wish = _openWishes.FirstOrDefault(w => w.Id == wishId);
        var result = await post.FinalizeAsync(segment.TempPath, segment.Artist,
                                              segment.Title, wish?.Album, ct);

        await db.AddCaptureAsync(new Capture
        {
            WishId = wishId,
            StationId = station.Id,
            StationName = station.Name,
            Artist = segment.Artist,
            Title = segment.Title,
            StartedAt = segment.StartedAt,
            Seconds = result.Seconds,
            Bitrate = station.MeasuredBitrate,
            Codec = station.MeasuredCodec,
            Path = result.Path,
            State = result.Ok ? CaptureState.Done : CaptureState.Rejected,
            Reason = result.Reason,
        });

        if (File.Exists(segment.TempPath)) File.Delete(segment.TempPath);

        if (!result.Ok)
        {
            log.LogInformation("Verworfen: {Interpret} - {Titel} ({Grund})",
                segment.Artist, segment.Title, result.Reason);
            return;
        }

        // Erst wenn die Datei wirklich liegt, gilt der Wunsch als erfuellt.
        await db.FulfillWishAsync(wishId);
        _openWishes = db.GetWishes(onlyOpen: true);
        await lidarr.RescanAsync(ct);
    }
}
