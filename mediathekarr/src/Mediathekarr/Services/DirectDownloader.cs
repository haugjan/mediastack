using System.Collections.Concurrent;
using System.Threading.Channels;
using Mediathekarr.Data;

namespace Mediathekarr.Services;

// Der Direkt-Download aus der Oberflaeche: stoebern, ankreuzen, laden. Am
// Ziel steht keine App, sondern eine eigene Plex-Bibliothek, sortiert nach
// Sender und Sendung. Dokus und Einzelsendungen passen weder zu Sonarr noch
// zu Radarr, und genau die findet man beim Stoebern.
//
// Die eine Falle: Plex liest den Ordner laufend ein. Eine halb geladene
// Datei unter ihrem richtigen Namen stuende sofort als kaputtes Video in
// der Bibliothek. Deshalb heisst sie bis zum Schluss ".teil".
public sealed class DirectDownloader(
    Settings cfg, Database db, Fetcher fetcher, ILogger<DirectDownloader> log) : BackgroundService
{
    public const string Category = "direkt";

    // Zwei gleichzeitig: die Sender liefern je Verbindung meist langsamer,
    // als die Leitung koennte, aber mehr belastet die Platte ohne Gewinn.
    private const int Parallel = 2;

    public sealed record Progress(long Done, long Total);

    private readonly Channel<(long Job, MediathekItem Item)> _queue =
        Channel.CreateUnbounded<(long, MediathekItem)>();
    private readonly ConcurrentDictionary<long, Progress> _progress = new();
    private readonly ConcurrentDictionary<string, byte> _pending = new();

    // Was die Oberflaeche zuletzt angezeigt hat. Der Browser schickt nur
    // die Ids zurueck; Adresse und Namen kommen von hier, nicht aus dem,
    // was der Browser behauptet.
    private readonly ConcurrentDictionary<string, MediathekItem> _seen = new();

    public IReadOnlyDictionary<long, Progress> Running => _progress;

    public void Remember(IEnumerable<MediathekItem> items)
    {
        if (_seen.Count > 20000) _seen.Clear();
        foreach (var i in items) _seen[i.Id] = i;
    }

    public bool IsPending(string id) => _pending.ContainsKey(id);

    public string TargetFor(MediathekItem i)
    {
        var topic = string.IsNullOrWhiteSpace(i.Topic) ? "Einzelsendungen" : i.Topic;
        var date = i.Published.ToLocalTime().ToString("yyyy-MM-dd");
        return Path.Combine(cfg.LibraryRoot, Clean(i.Channel), Clean(topic),
                            Clean($"{topic} - {i.Title} ({date})") + ".mp4");
    }

    public bool Exists(MediathekItem i) => File.Exists(TargetFor(i));

    public (int Queued, int Present, int Unknown) Enqueue(IEnumerable<string> ids)
    {
        int queued = 0, present = 0, unknown = 0;
        foreach (var id in ids.Distinct())
        {
            if (!_seen.TryGetValue(id, out var item)) { unknown++; continue; }
            if (Exists(item) || !_pending.TryAdd(id, 0)) { present++; continue; }

            var name = Path.GetFileNameWithoutExtension(TargetFor(item));
            var job = db.AddJob(id, name, Category, "wartet");
            _queue.Writer.TryWrite((job, item));
            queued++;
        }
        return (queued, present, unknown);
    }

    protected override Task ExecuteAsync(CancellationToken ct)
    {
        // Was beim letzten Beenden noch wartete oder lief, ist verloren: die
        // Warteschlange lebt nur im Speicher. Ehrlich als abgebrochen zeigen
        // statt ewig als "laeuft".
        db.AbortOpenJobs();
        Directory.CreateDirectory(cfg.LibraryRoot);
        return Task.WhenAll(Enumerable.Range(0, Parallel).Select(_ => WorkAsync(ct)));
    }

    private async Task WorkAsync(CancellationToken ct)
    {
        await foreach (var (job, item) in _queue.Reader.ReadAllAsync(ct))
        {
            var target = TargetFor(item);
            var temp = target + ".teil";
            try
            {
                db.SetJobStatus(job, "laeuft");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                _progress[job] = new Progress(0, item.Size);
                var bytes = await fetcher.DownloadAsync(item.BestUrl, temp, ct,
                    (done, total) => _progress[job] = new Progress(done, total > 0 ? total : item.Size),
                    item.Duration);
                if (!string.IsNullOrWhiteSpace(item.UrlSubtitle))
                    await fetcher.SubtitleAsync(item.UrlSubtitle, Path.ChangeExtension(target, ".ger.srt"), ct);

                File.Move(temp, target, true);
                db.FinishJob(job, "fertig", bytes);
                log.LogInformation("Heruntergeladen: {Pfad} ({MB} MB)", target, bytes / 1_000_000);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                if (File.Exists(temp)) File.Delete(temp);
                return;
            }
            catch (Exception e)
            {
                if (File.Exists(temp)) File.Delete(temp);
                db.FinishJob(job, "fehlgeschlagen", 0, e.Message);
                log.LogWarning("Download {Titel} fehlgeschlagen: {Fehler}", item.Title, e.Message);
            }
            finally
            {
                _progress.TryRemove(job, out _);
                _pending.TryRemove(item.Id, out _);
            }
        }
    }

    // Was Dateisysteme und Plex nicht moegen, fliegt raus. Laenge begrenzt,
    // weil manche Sender die halbe Beschreibung in den Titel schreiben.
    private static string Clean(string s)
    {
        var bad = Path.GetInvalidFileNameChars().Concat([':', '*', '?', '"', '<', '>', '|', '\\']).ToHashSet();
        var c = new string(s.Where(ch => !bad.Contains(ch) && !char.IsControl(ch)).ToArray()).Trim().TrimEnd('.');
        if (c.Length == 0) c = "Unbenannt";
        return c.Length > 120 ? c[..120].Trim() : c;
    }
}
