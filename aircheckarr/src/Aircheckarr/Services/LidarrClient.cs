using System.Net.Http.Json;
using System.Text.Json;
using Aircheckarr.Data;

namespace Aircheckarr.Services;

/// <summary>
/// Die Bruecke zu Lidarr.
///
/// Lidarr denkt in Alben, ein Radiomitschnitt ist ein einzelner Titel. Diese
/// Luecke schliesst die Klasse in beide Richtungen: sie holt die fehlenden
/// Alben und loest sie in ihre Titel auf, damit daraus Wuensche werden
/// koennen, und sie stoesst nach einem Mitschnitt den Suchlauf an, damit
/// Lidarr die neue Datei in die Bibliothek uebernimmt.
/// </summary>
public sealed class LidarrClient(HttpClient http, Settings settings, ILogger<LidarrClient> log)
{
    public bool Configured =>
        !string.IsNullOrWhiteSpace(settings.LidarrUrl)
        && !string.IsNullOrWhiteSpace(settings.LidarrApiKey);

    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        var req = new HttpRequestMessage(method, $"{settings.LidarrUrl!.TrimEnd('/')}{path}");
        req.Headers.Add("X-Api-Key", settings.LidarrApiKey);
        return req;
    }

    /// <summary>
    /// Holt fehlende Alben und loest sie in Titel auf. Ohne das muesste man
    /// jeden Wunsch von Hand eintippen, obwohl Lidarr schon weiss, was fehlt.
    /// </summary>
    public async Task<List<Wish>> GetMissingTracksAsync(int maxAlbums, CancellationToken ct)
    {
        var wishes = new List<Wish>();
        if (!Configured) return wishes;

        try
        {
            using var albumsResponse = await http.SendAsync(
                Request(HttpMethod.Get,
                    $"/api/v1/wanted/missing?pageSize={maxAlbums}&sortKey=releaseDate&sortDirection=descending"),
                ct);
            albumsResponse.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(await albumsResponse.Content.ReadAsStringAsync(ct));
            if (!doc.RootElement.TryGetProperty("records", out var records)) return wishes;

            foreach (var album in records.EnumerateArray())
            {
                var albumId = album.GetProperty("id").GetInt32();
                var albumTitle = album.TryGetProperty("title", out var t) ? t.GetString() : null;
                var artistName = album.TryGetProperty("artist", out var a)
                                 && a.TryGetProperty("artistName", out var an)
                    ? an.GetString() : null;
                if (string.IsNullOrWhiteSpace(artistName)) continue;

                using var tracksResponse = await http.SendAsync(
                    Request(HttpMethod.Get, $"/api/v1/track?albumId={albumId}"), ct);
                if (!tracksResponse.IsSuccessStatusCode) continue;

                var tracks = await tracksResponse.Content.ReadFromJsonAsync<JsonElement>(ct);
                foreach (var track in tracks.EnumerateArray())
                {
                    var title = track.TryGetProperty("title", out var tt) ? tt.GetString() : null;
                    if (string.IsNullOrWhiteSpace(title)) continue;
                    wishes.Add(new Wish
                    {
                        Artist = artistName,
                        Title = title,
                        Album = albumTitle,
                        Source = "lidarr",
                        NormalizedArtist = TitleMatcher.Normalize(artistName),
                        NormalizedTitle = TitleMatcher.Normalize(title),
                    });
                }
            }
            log.LogInformation("{Zahl} Titel aus Lidarrs Fehlliste uebernommen", wishes.Count);
        }
        catch (Exception ex)
        {
            log.LogWarning("Lidarr nicht erreichbar: {Fehler}", ex.Message);
        }
        return wishes;
    }

    /// <summary>Laesst Lidarr den Bibliotheksordner neu einlesen.</summary>
    public async Task RescanAsync(CancellationToken ct)
    {
        if (!Configured) return;
        try
        {
            var req = Request(HttpMethod.Post, "/api/v1/command");
            req.Content = JsonContent.Create(new { name = "RescanFolders" });
            using var response = await http.SendAsync(req, ct);
            log.LogInformation("Lidarr-Suchlauf angestossen: {Status}", response.StatusCode);
        }
        catch (Exception ex)
        {
            log.LogWarning("Suchlauf liess sich nicht anstossen: {Fehler}", ex.Message);
        }
    }
}
