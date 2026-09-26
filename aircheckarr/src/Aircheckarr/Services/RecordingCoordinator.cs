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
///
/// Welche Sender mitgehoert werden, bestimmt allein die Auswahl in der
/// Oberflaeche (Spalte enabled). Die Messung schaltet nichts mehr von selbst
/// an, sie entscheidet nur, ob ein ausgewaehlter Sender auch taugt.
/// </summary>
public sealed class RecordingCoordinator(
    Database db, Settings settings, RadioBrowserClient catalog, QualityProbe probe,
    IcyStreamListener listener, PostProcessor post, LidarrClient lidarr,
    ILogger<RecordingCoordinator> log) : BackgroundService
{
    /// <summary>
    /// Zustand eines mitgehoerten Senders. Wird aus dessen eigener Aufgabe
    /// beschrieben und von der Oberflaeche nur gelesen; ein halb
    /// aktualisierter Stand ist dort harmlos.
    /// </summary>
    public sealed class Listening(Station station)
    {
        public Station Station { get; } = station;
        public DateTime Since { get; } = DateTime.UtcNow;
        public CancellationTokenSource Stop { get; } = new();
        public string? Artist { get; set; }
        public string? Title { get; set; }
        public DateTime? TitleSince { get; set; }
        public bool Recording { get; set; }
    }

    private volatile List<Wish> _openWishes = [];
    private readonly ConcurrentDictionary<string, Listening> _running = new();

    // Weckt die Messung, sobald neue Sender da sind. Sonst wartete ein
    // frisch ausgewaehlter Sender bis zu zehn Minuten auf seine Messung.
    private readonly SemaphoreSlim _measureWake = new(0);

    /// <summary>Was gerade mitgehoert wird, fuer die Oberflaeche.</summary>
    public IReadOnlyDictionary<string, Listening> Running => _running;

    /// <summary>Ergebnis des letzten Abgleichs mit Lidarr, fuer die Oberflaeche.</summary>
    public sealed record SyncState(DateTime At, bool Ok, int Wanted, int Added, int Removed);
    public SyncState? LastLidarrSync { get; private set; }

    // Nur ein Abgleich zur Zeit, sonst loeschte der eine, was der andere
    // gerade eingetragen hat.
    private readonly SemaphoreSlim _syncLock = new(1, 1);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        db.Initialize();

        if (db.GetStations(limit: 1).Count == 0)
        {
            log.LogInformation("Kein Sender bekannt, Katalog wird erstmalig geholt");
            // Beim allerersten Start gleich ausgewaehlt, damit ohne einen
            // einzigen Klick etwas passiert. Was nicht taugt, bleibt stumm.
            await RefreshCatalogAsync(null, null, 400, select: true, ct);
        }

        var measuring = Task.Run(() => MeasureLoopAsync(ct), ct);
        var listening = Task.Run(() => ListenLoopAsync(ct), ct);
        var wishes = Task.Run(() => WishLoopAsync(ct), ct);
        var lidarrSync = Task.Run(() => LidarrLoopAsync(ct), ct);
        await Task.WhenAll(measuring, listening, wishes, lidarrSync);
    }

    public async Task<int> RefreshCatalogAsync(string? tag, string? country, int limit,
                                               bool select, CancellationToken ct)
    {
        var stations = await catalog.FetchAsync(tag, country, null, limit, ct);
        await db.UpsertStationsAsync(stations, select);
        _measureWake.Release();
        return stations.Count;
    }

    /// <summary>Uebernimmt einzeln ausgesuchte Sender und waehlt sie aus.</summary>
    public async Task<int> AddStationsAsync(IReadOnlyCollection<string> ids, CancellationToken ct)
    {
        var stations = await catalog.FetchByIdsAsync(ids, ct);
        await db.UpsertStationsAsync(stations, select: true);
        _measureWake.Release();
        return stations.Count;
    }

    /// <summary>
    /// Trennt die Verbindung zu einem Sender sofort. Ein laufender Mitschnitt
    /// wird dabei verworfen, siehe IcyStreamListener.
    /// </summary>
    public void StopListening(IEnumerable<string> ids)
    {
        foreach (var id in ids)
            if (_running.TryGetValue(id, out var l)) l.Stop.Cancel();
    }

    // --------------------------------------------------------------- Messen

    private async Task MeasureLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var todo = db.GetStations(onlyUnmeasured: true, limit: 40);
            if (todo.Count == 0)
            {
                await _measureWake.WaitAsync(TimeSpan.FromMinutes(10), ct);
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

    private async Task LidarrLoopAsync(CancellationToken ct)
    {
        if (!lidarr.Configured || settings.LidarrSyncMinutes == 0) return;

        // Kurz warten: beim gemeinsamen Start mit dem Stack ist Lidarr oft
        // noch nicht so weit, und der erste Versuch ginge ins Leere.
        await Task.Delay(TimeSpan.FromMinutes(1), ct);
        while (!ct.IsCancellationRequested)
        {
            await SyncLidarrAsync(ct);
            await Task.Delay(TimeSpan.FromMinutes(settings.LidarrSyncMinutes), ct);
        }
    }

    /// <summary>
    /// Holt Lidarrs Fehlliste und gleicht die Wuensche damit ab. Null, wenn
    /// Lidarr nicht antwortet; dann bleibt die Liste, wie sie ist.
    /// </summary>
    public async Task<SyncState?> SyncLidarrAsync(CancellationToken ct)
    {
        await _syncLock.WaitAsync(ct);
        try
        {
            var wanted = await lidarr.GetWantedTracksAsync(settings.LidarrMaxAlbums, ct);
            if (wanted is null)
            {
                LastLidarrSync = new SyncState(DateTime.UtcNow, false, 0, 0, 0);
                return null;
            }
            var (added, removed) = await db.SyncLidarrWishesAsync(wanted);
            _openWishes = db.GetWishes(onlyOpen: true);
            if (added > 0 || removed > 0)
                log.LogInformation("Abgleich mit Lidarr: {Neu} neu, {Weg} erledigt", added, removed);
            return LastLidarrSync = new SyncState(DateTime.UtcNow, true, wanted.Count, added, removed);
        }
        finally { _syncLock.Release(); }
    }

    /// <summary>
    /// Wird bei jedem Titelwechsel aufgerufen. Muss schnell sein, deshalb
    /// laeuft der Vergleich gegen eine Kopie der Wunschliste im Speicher.
    /// </summary>
    private long? MatchWish(Listening state, string artist, string title)
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
        state.Recording = best is not null;
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

            var selected = db.GetStations(onlyEnabled: true, limit: 2000)
                .Where(s => s.PassesQuality(settings))
                .ToList();

            // Abgewaehlte Sender trennen. Die Oberflaeche tut das auch schon
            // selbst, hier faengt es ab, was an ihr vorbei geaendert wurde.
            var selectedIds = selected.Select(s => s.Id).ToHashSet();
            StopListening(_running.Keys.Where(id => !selectedIds.Contains(id)).ToList());

            var candidates = selected
                .Where(s => !_running.ContainsKey(s.Id))
                .Take(Math.Max(0, settings.MaxConcurrentStations - _running.Count))
                .ToList();

            foreach (var station in candidates)
            {
                var state = new Listening(station);
                _running[station.Id] = state;
                _ = Task.Run(() => ListenToStationAsync(state, ct), ct);
            }

            await Task.Delay(TimeSpan.FromSeconds(20), ct);
        }
    }

    private async Task ListenToStationAsync(Listening state, CancellationToken ct)
    {
        var station = state.Station;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, state.Stop.Token);
        try
        {
            await listener.ListenAsync(
                station, settings.WorkPath, station.MeasuredBitrate,
                (artist, title) => MatchWish(state, artist, title),
                // Nachbearbeitung und Uebergabe an Lidarr laufen abseits:
                // sie dauern bis zu Minuten, und so lange stuende sonst der
                // Datenstrom still, bis der Sender auflegt. Ein fertiger
                // Titel wird auch nach dem Abwaehlen noch abgelegt, deshalb
                // hier nur das Token des Dienstes.
                (segment, wishId) =>
                {
                    _ = Task.Run(async () =>
                    {
                        try { await HandleSegmentAsync(state, segment, wishId, ct); }
                        catch (OperationCanceledException) { /* Abschalten */ }
                        catch (Exception ex)
                        {
                            log.LogWarning("Nachbearbeitung von {Interpret} - {Titel} gescheitert: {Fehler}",
                                segment.Artist, segment.Title, ex.Message);
                        }
                    }, ct);
                    return Task.CompletedTask;
                },
                (artist, title) =>
                {
                    state.Artist = artist;
                    state.Title = title;
                    state.TitleSince = DateTime.UtcNow;
                    state.Recording = false;
                },
                linked.Token);
        }
        catch (OperationCanceledException) { /* Abschalten oder abgewaehlt, kein Fehler */ }
        catch (Exception ex)
        {
            log.LogWarning("{Sender} abgebrochen: {Fehler}", station.Name, ex.Message);
        }
        finally
        {
            // Stop wird bewusst nicht freigegeben: StopListening kann den
            // Zustand gerade noch in der Hand haben, und ohne Zeitgeber haelt
            // eine CancellationTokenSource nichts fest.
            _running.TryRemove(station.Id, out _);
        }
    }

    private async Task HandleSegmentAsync(Listening state, IcyStreamListener.Segment segment,
                                          long wishId, CancellationToken ct)
    {
        var station = state.Station;
        state.Recording = false;

        // Frisch aus der Datenbank, nicht aus der Kopie im Speicher: der
        // Wunsch kann waehrend der Aufnahme erledigt worden sein, weil Lidarr
        // den Titel inzwischen auf anderem Weg bekommen hat.
        var wish = db.GetWish(wishId);
        if (wish is null || wish.FulfilledAt is not null)
        {
            File.Delete(segment.TempPath);
            await SaveCaptureAsync(station, segment, wishId, 0, null, false,
                                   "Wunsch war inzwischen erledigt");
            return;
        }

        // Mit Lidarr-Zuordnung geht die Datei in einen eigenen Ordner zur
        // Uebergabe. Allein darin, weil Lidarr den ganzen Ordner einliest.
        var viaLidarr = wish.InLidarr && lidarr.Configured;
        var handover = viaLidarr
            ? Path.Combine(settings.WorkPath, "lidarr", Guid.NewGuid().ToString("N"))
            : null;

        var result = await post.FinalizeAsync(segment.TempPath, segment.Artist,
                                              segment.Title, wish.Album, handover, ct);
        if (File.Exists(segment.TempPath)) File.Delete(segment.TempPath);

        if (!result.Ok)
        {
            if (handover is not null && Directory.Exists(handover)) Directory.Delete(handover, true);
            await SaveCaptureAsync(station, segment, wishId, result.Seconds, null, false, result.Reason);
            log.LogInformation("Verworfen: {Interpret} - {Titel} ({Grund})",
                segment.Artist, segment.Title, result.Reason);
            return;
        }

        var path = result.Path!;
        var imported = false;
        string? note = null;
        if (viaLidarr)
        {
            var import = await lidarr.ImportAsync(path, wish, ct);
            if (import.Ok)
            {
                imported = true;
                path = import.FinalPath ?? path;
            }
            else
            {
                // Rueckfall: direkt in die Bibliothek wie ein Wunsch von Hand.
                // Der Mitschnitt ist gelungen, er soll nicht verloren gehen.
                note = import.Reason;
                var target = post.LibraryPathFor(wish.Artist, wish.Title, wish.Album,
                                                 Path.GetExtension(path));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Move(path, target, overwrite: true);
                path = target;
                log.LogWarning("Lidarr hat nicht uebernommen ({Grund}), abgelegt unter {Pfad}",
                    note, path);
            }
            if (Directory.Exists(handover!)) Directory.Delete(handover!, true);
        }

        await SaveCaptureAsync(station, segment, wishId, result.Seconds, path, imported, note);

        // Erst wenn die Datei wirklich liegt, gilt der Wunsch als erfuellt.
        await db.FulfillWishAsync(wishId);
        _openWishes = db.GetWishes(onlyOpen: true);
        if (!imported) await lidarr.RescanAsync(ct);
    }

    private Task SaveCaptureAsync(Station station, IcyStreamListener.Segment segment, long wishId,
                                  double seconds, string? path, bool imported, string? reason) =>
        db.AddCaptureAsync(new Capture
        {
            WishId = wishId,
            StationId = station.Id,
            StationName = station.Name,
            Artist = segment.Artist,
            Title = segment.Title,
            StartedAt = segment.StartedAt,
            Seconds = seconds,
            Bitrate = station.MeasuredBitrate,
            Codec = station.MeasuredCodec,
            Path = path,
            State = path is null ? CaptureState.Rejected : CaptureState.Done,
            Reason = reason,
            ImportedByLidarr = imported,
        });
}
