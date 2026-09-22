using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Aircheckarr.Data;

namespace Aircheckarr.Services;

/// <summary>
/// Holt den Senderkatalog von radio-browser.info, einer offenen Datenbank
/// mit knapp 60000 Sendern.
///
/// Die Betreiber bitten um einen sprechenden User-Agent, damit sie
/// missbraeuchliche Nutzung von normaler unterscheiden koennen. Die
/// Katalogangaben zu Codec und Bitrate sind Selbstauskunft und teilweise
/// grober Unfug (64000 kbit/s, Videostreams als Radio); sie dienen hier nur
/// als Vorauswahl, gemessen wird mit QualityProbe.
/// </summary>
public sealed class RadioBrowserClient(HttpClient http, Settings settings,
                                       ILogger<RadioBrowserClient> log)
{
    private sealed record Entry(
        [property: JsonPropertyName("stationuuid")] string Uuid,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("url_resolved")] string? UrlResolved,
        [property: JsonPropertyName("url")] string? Url,
        [property: JsonPropertyName("country")] string? Country,
        [property: JsonPropertyName("tags")] string? Tags,
        [property: JsonPropertyName("codec")] string? Codec,
        [property: JsonPropertyName("bitrate")] int Bitrate);

    /// <summary>
    /// Laedt Sender, die als Vorauswahl taugen. Gefiltert wird auf
    /// erreichbare Sender mit passendem Codec; alles Weitere entscheidet
    /// die eigene Messung.
    /// </summary>
    public async Task<List<Station>> FetchAsync(string? tag, string? country,
                                                int limit, CancellationToken ct)
    {
        var query = new List<string>
        {
            "hidebroken=true",
            "order=clickcount",
            "reverse=true",
            $"limit={limit}",
        };
        if (!string.IsNullOrWhiteSpace(tag)) query.Add($"tagList={Uri.EscapeDataString(tag)}");
        if (!string.IsNullOrWhiteSpace(country))
            query.Add($"countrycode={Uri.EscapeDataString(country)}");

        var url = $"{settings.RadioBrowserUrl}/json/stations/search?{string.Join('&', query)}";
        log.LogInformation("Katalog abrufen: {Url}", url);

        var entries = await http.GetFromJsonAsync<List<Entry>>(url, ct) ?? [];

        var stations = entries
            .Where(e => !string.IsNullOrWhiteSpace(e.UrlResolved ?? e.Url))
            // Playlisten und Videostreams fliegen sofort raus, die kann der
            // Mitschneider nicht lesen.
            .Where(e => !(e.UrlResolved ?? e.Url)!.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))
            .Where(e => e.Codec is null
                        || settings.AllowedCodecs.Contains(e.Codec.ToLowerInvariant()))
            .Select(e => new Station
            {
                Id = e.Uuid,
                Name = e.Name.Trim(),
                Url = (e.UrlResolved ?? e.Url)!,
                Country = e.Country,
                Tags = e.Tags,
                CatalogCodec = e.Codec?.ToLowerInvariant(),
                // Unsinnige Werte gar nicht erst uebernehmen.
                CatalogBitrate = e.Bitrate is > 0 and <= 640 ? e.Bitrate : 0,
            })
            .DistinctBy(s => s.Id)
            .ToList();

        log.LogInformation("{Zahl} Sender aus dem Katalog uebernommen", stations.Count);
        return stations;
    }
}
