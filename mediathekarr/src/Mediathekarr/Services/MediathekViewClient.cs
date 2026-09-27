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
        //
        // Ueber Titel UND Thema suchen: die Sender fuehren die Reihe mal im
        // einen, mal im anderen Feld. "Tatort" steht als Thema, der
        // Episodentitel im Titel.
        var queries = string.IsNullOrWhiteSpace(query)
            ? Array.Empty<object>()
            : [new { fields = new[] { "title", "topic" }, query }];

        // Ohne Begriff kommen viele kurze Beitraege, die der Filter unten
        // wegwirft. Deshalb mehr anfordern, damit genug uebrig bleibt.
        var size = string.IsNullOrWhiteSpace(query) ? cfg.MaxResults * 5 : cfg.MaxResults;
        var (items, _) = await RunAsync(queries, size, 0, null, cfg.MinDurationSeconds, ct);
        return items;
    }

    /// <summary>
    /// Stoebern in einer Mediathek: die neuesten Sendungen eines Senders,
    /// wahlweise auf eine Sendereihe oder einen Suchbegriff eingeschraenkt.
    /// Mehrere Bedingungen verknuepft MediathekViewWeb mit UND.
    /// </summary>
    /// <returns>
    /// Next ist die Position fuer die naechste Seite. Sie laesst sich nicht
    /// aus der Zahl der Beitraege ableiten, weil Doppelte wegfallen: die
    /// Liste fuehrt viele Sendungen zweimal mit derselben Adresse.
    /// </returns>
    public async Task<(IReadOnlyList<MediathekItem> Items, long Total, int Next)> BrowseAsync(
        string? channel, string? topic, string? text, int offset, int size,
        bool includeShort, CancellationToken ct)
    {
        var queries = new List<object>();
        if (!string.IsNullOrWhiteSpace(channel))
            queries.Add(new { fields = new[] { "channel" }, query = channel.Trim() });
        if (!string.IsNullOrWhiteSpace(topic))
            queries.Add(new { fields = new[] { "topic" }, query = topic.Trim() });
        if (!string.IsNullOrWhiteSpace(text))
            queries.Add(new { fields = new[] { "title", "topic" }, query = text.Trim() });

        // Die Mindestdauer filtert hier schon der Server, sonst kaemen bei
        // den Nachrichtensendern Seiten voller Schnipsel zurueck.
        var minimum = includeShort ? 0 : cfg.MinDurationSeconds;
        var (items, total) = await RunAsync(queries.ToArray(), size, offset, minimum, minimum, ct);
        return (Unique(items), total, offset + size);
    }

    // Die Liste fuehrt viele Sendungen doppelt: dieselbe Ausstrahlung unter
    // zwei Adressen, etwa einmal mit und einmal ohne Untertitel. Beim
    // Stoebern stuende dann jede Zeile zweimal da. Behalten wird die bessere
    // Fassung, mit HD und Untertiteln vor ohne.
    private static List<MediathekItem> Unique(IEnumerable<MediathekItem> items) =>
        items.GroupBy(i => (i.Channel, i.Topic, i.Title, i.Timestamp))
             .Select(g => g.OrderByDescending(i => !string.IsNullOrWhiteSpace(i.UrlVideoHd))
                           .ThenByDescending(i => !string.IsNullOrWhiteSpace(i.UrlSubtitle))
                           .First())
             .ToList();

    private async Task<(IReadOnlyList<MediathekItem> Items, long Total)> RunAsync(
        object[] queries, int size, int offset, int? serverMinDuration, int minDuration,
        CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["queries"] = queries,
            ["sortBy"] = "timestamp",
            ["sortOrder"] = "desc",
            ["future"] = false,
            ["size"] = size,
            ["offset"] = offset,
            ["duration_min"] = serverMinDuration is > 0 ? serverMinDuration : null,
        }.Where(kv => kv.Value is not null).ToDictionary());

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
                return ([], 0);

            long total = 0;
            if (result.TryGetProperty("queryInfo", out var info)) total = Num(info, "totalResults");

            var list = new List<MediathekItem>();
            foreach (var h in hits.EnumerateArray())
            {
                var item = new MediathekItem(
                    Str(h, "channel"), Str(h, "topic"), Str(h, "title"), Str(h, "description"),
                    Num(h, "timestamp"), (int)Num(h, "duration"), Num(h, "size"),
                    Str(h, "url_video"), Str(h, "url_video_hd"), Str(h, "url_video_low"),
                    Str(h, "url_subtitle"));

                // Nachrichtenschnipsel, Trailer und Teaser aussortieren.
                if (item.Duration < minDuration) continue;
                // Barrierefreie Zweitfassungen sind eigene Beitraege mit
                // demselben Titel. Fuer Sonarr sind sie nicht von der
                // Hauptfassung zu unterscheiden und landen dann zufaellig
                // in der Bibliothek - mit eingesprochenen Bildbeschreibungen
                // ueber dem Ton.
                if (IsAccessibilityVersion(item.Title)) continue;
                if (string.IsNullOrWhiteSpace(item.BestUrl)) continue;
                if (cfg.Channels.Length > 0 &&
                    !cfg.Channels.Any(c => item.Channel.Contains(c, StringComparison.OrdinalIgnoreCase)))
                    continue;

                list.Add(item);
            }
            return (list, total);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            log.LogWarning("Mediathek-Abfrage fehlgeschlagen: {Fehler}", e.Message);
            return ([], 0);
        }
    }

    public static bool IsAccessibilityVersion(string title) =>
        Barrierefrei.Any(m => title.Contains(m, StringComparison.OrdinalIgnoreCase));

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
