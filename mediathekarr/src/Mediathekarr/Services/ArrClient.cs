using System.Text.Json;

namespace Mediathekarr.Services;

// Der Grund, warum dieser Dienst ueberhaupt brauchbare Namen erzeugt.
//
// Die Mediatheken kennen keine Staffeln und Folgen, nur Sendedaten. Sonarr
// dagegen sucht nach "Tatort S2026E12" und erkennt nur Releases, die auch so
// heissen. Wir fragen deshalb bei Sonarr nach, WANN diese Folge lief, suchen
// in der Mediathek nach diesem Datum und benennen den Treffer dann so, wie
// Sonarr ihn erwartet. Ohne Schluessel faellt alles auf Datumsnamen zurueck,
// die nur bei taeglichen Sendungen passen.
public sealed class ArrClient(HttpClient http, ILogger<ArrClient> log)
{
    private readonly string _sonarr = Environment.GetEnvironmentVariable("SONARR_URL") ?? "http://sonarr:8989";
    private readonly string _sonarrKey = Environment.GetEnvironmentVariable("SONARR_API_KEY") ?? "";
    private readonly string _radarr = Environment.GetEnvironmentVariable("RADARR_URL") ?? "http://radarr:7878";
    private readonly string _radarrKey = Environment.GetEnvironmentVariable("RADARR_API_KEY") ?? "";

    public bool SonarrReady => !string.IsNullOrWhiteSpace(_sonarrKey);
    public bool RadarrReady => !string.IsNullOrWhiteSpace(_radarrKey);

    // Sendedatum und Episodentitel zu einer Staffel-Folge-Kombination.
    public async Task<(DateOnly? Air, string Title)> EpisodeAsync(string series, int season, int episode, CancellationToken ct)
    {
        if (!SonarrReady || season <= 0 || episode <= 0) return (null, "");
        try
        {
            var all = await GetAsync($"{_sonarr}/api/v3/series", _sonarrKey, ct);
            if (all is null) return (null, "");
            var id = all.Value.EnumerateArray()
                .Where(s => Title(s).Equals(series, StringComparison.OrdinalIgnoreCase)
                         || Title(s).Contains(series, StringComparison.OrdinalIgnoreCase))
                .Select(s => s.GetProperty("id").GetInt32())
                .FirstOrDefault();
            if (id == 0) return (null, "");

            var eps = await GetAsync($"{_sonarr}/api/v3/episode?seriesId={id}", _sonarrKey, ct);
            if (eps is null) return (null, "");
            foreach (var e in eps.Value.EnumerateArray())
            {
                if (e.GetProperty("seasonNumber").GetInt32() != season) continue;
                if (e.GetProperty("episodeNumber").GetInt32() != episode) continue;
                var air = e.TryGetProperty("airDate", out var a) && a.ValueKind == JsonValueKind.String
                    && DateOnly.TryParse(a.GetString(), out var d) ? d : (DateOnly?)null;
                var title = e.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                return (air, title);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException)
        {
            log.LogDebug("Sonarr-Abfrage fehlgeschlagen: {Fehler}", ex.Message);
        }
        return (null, "");
    }

    // Erscheinungsjahr zu einer IMDb-Id. Radarr lehnt ein Release ohne
    // passendes Jahr im Namen ab.
    public async Task<(string Title, int Year)> MovieAsync(string imdbId, CancellationToken ct)
    {
        if (!RadarrReady || string.IsNullOrWhiteSpace(imdbId)) return ("", 0);
        try
        {
            var all = await GetAsync($"{_radarr}/api/v3/movie", _radarrKey, ct);
            if (all is null) return ("", 0);
            foreach (var m in all.Value.EnumerateArray())
            {
                var imdb = m.TryGetProperty("imdbId", out var i) ? i.GetString() ?? "" : "";
                if (!imdb.Equals(imdbId, StringComparison.OrdinalIgnoreCase)) continue;
                return (Title(m), m.TryGetProperty("year", out var y) ? y.GetInt32() : 0);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            log.LogDebug("Radarr-Abfrage fehlgeschlagen: {Fehler}", ex.Message);
        }
        return ("", 0);
    }

    private static string Title(JsonElement e) =>
        e.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";

    private async Task<JsonElement?> GetAsync(string url, string key, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("X-Api-Key", key);
        using var res = await http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode) return null;
        var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        return doc.RootElement.Clone();
    }
}
