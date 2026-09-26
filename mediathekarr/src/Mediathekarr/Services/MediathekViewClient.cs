using System.Text;
using System.Text.Json;
using Mediathekarr.Data;

namespace Mediathekarr.Services;

// Fragt MediathekViewWeb ab. Deren API nimmt einen JSON-Rumpf als
// text/plain entgegen - das ist keine Verwechslung, sondern verlangt sie so.
public sealed class MediathekViewClient(HttpClient http, Settings cfg, ILogger<MediathekViewClient> log)
{
    public async Task<IReadOnlyList<MediathekItem>> QueryAsync(string query, CancellationToken ct)
    {
        // Ohne Suchbegriff die juengsten Sendungen. Das ist kein Sonderfall
        // fuer uns, sondern die Probe, mit der Prowlarr einen neuen Indexer
        // begruesst - und ein RSS-Feed, den Sonarr abonnieren kann.
        var queries = string.IsNullOrWhiteSpace(query)
            ? Array.Empty<object>()
            : [new { fields = new[] { "title", "topic" }, query }];

        // Ueber Titel UND Thema suchen: die Sender fuehren die Reihe mal im
        // einen, mal im anderen Feld. "Tatort" steht als Thema, der
        // Episodentitel im Titel.
        var body = JsonSerializer.Serialize(new
        {
            queries,
            sortBy = "timestamp",
            sortOrder = "desc",
            future = false,
            // Ohne Begriff kommen viele kurze Beitraege, die der Filter
            // unten wegwirft. Deshalb mehr anfordern, damit genug uebrig
            // bleibt.
            size = string.IsNullOrWhiteSpace(query) ? cfg.MaxResults * 5 : cfg.MaxResults,
        });

        using var req = new HttpRequestMessage(HttpMethod.Post, cfg.ApiUrl)
        {
            Content = new StringContent(body, Encoding.UTF8, "text/plain"),
        };

        try
        {
            using var res = await http.SendAsync(req, ct);
            res.EnsureSuccessStatusCode();
            await using var stream = await res.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            if (!doc.RootElement.TryGetProperty("result", out var result) ||
                !result.TryGetProperty("results", out var hits))
                return [];

            var list = new List<MediathekItem>();
            foreach (var h in hits.EnumerateArray())
            {
                var item = new MediathekItem(
                    Str(h, "channel"), Str(h, "topic"), Str(h, "title"), Str(h, "description"),
                    Num(h, "timestamp"), (int)Num(h, "duration"), Num(h, "size"),
                    Str(h, "url_video"), Str(h, "url_video_hd"), Str(h, "url_video_low"),
                    Str(h, "url_subtitle"));

                // Nachrichtenschnipsel, Trailer und Teaser aussortieren.
                if (item.Duration < cfg.MinDurationSeconds) continue;
                // Barrierefreie Zweitfassungen sind eigene Beitraege mit
                // demselben Titel. Fuer Sonarr sind sie nicht von der
                // Hauptfassung zu unterscheiden und landen dann zufaellig
                // in der Bibliothek - mit eingesprochenen Bildbeschreibungen
                // ueber dem Ton.
                if (Barrierefrei.Any(m => item.Title.Contains(m, StringComparison.OrdinalIgnoreCase))) continue;
                if (string.IsNullOrWhiteSpace(item.BestUrl)) continue;
                if (cfg.Channels.Length > 0 &&
                    !cfg.Channels.Any(c => item.Channel.Contains(c, StringComparison.OrdinalIgnoreCase)))
                    continue;

                list.Add(item);
            }
            return list;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            log.LogWarning("Mediathek-Abfrage fehlgeschlagen: {Fehler}", e.Message);
            return [];
        }
    }

    private static readonly string[] Barrierefrei =
        ["Audiodeskription", "Hoerfassung", "Hörfassung", "Gebaerdensprache", "Gebärdensprache",
         "klare Sprache", "Trailer"];

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static long Num(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return 0;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.TryGetInt64(out var l) ? l : 0,
            JsonValueKind.String => long.TryParse(v.GetString(), out var s) ? s : 0,
            _ => 0,
        };
    }
}
