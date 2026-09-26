using System.Net.Http.Json;
using System.Text.Json;
using Aircheckarr.Data;

namespace Aircheckarr.Services;

/// <summary>
/// Die Bruecke zu Lidarr, in beide Richtungen.
///
/// Hinein: Lidarrs Fehlliste ist die Wunschliste. Wer ein Album in Lidarr
/// beobachtet, wuenscht sich damit auch dessen fehlende Titel hier, ohne
/// etwas doppelt einzutragen. Lidarr denkt in Alben, ein Mitschnitt ist ein
/// einzelner Titel, deshalb wird jedes Album in seine Titel aufgeloest.
///
/// Hinaus: ein Mitschnitt geht ueber den manuellen Import an Lidarr, mit
/// den genauen Nummern von Interpret, Album, Ausgabe und Titel. Lidarr
/// benennt, verschiebt und hakt ab wie bei jedem Download. Die eigene
/// Erkennung von Lidarr waere hier die falsche Wahl: ein einzelner Titel
/// eines Albums "passt" fuer sie nie gut genug und bliebe liegen.
///
/// Die eine Falle: beide Container muessen die Datei unter demselben Pfad
/// sehen. Das tun sie, weil beide /data als einzigen Mount haben.
/// </summary>
public sealed class LidarrClient(HttpClient http, Settings settings, ILogger<LidarrClient> log)
{
    public sealed record ImportResult(bool Ok, string? FinalPath, string? Reason);

    public bool Configured =>
        !string.IsNullOrWhiteSpace(settings.LidarrUrl)
        && !string.IsNullOrWhiteSpace(settings.LidarrApiKey);

    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        var req = new HttpRequestMessage(method, $"{settings.LidarrUrl!.TrimEnd('/')}{path}");
        req.Headers.Add("X-Api-Key", settings.LidarrApiKey);
        return req;
    }

    private async Task<JsonElement> GetJsonAsync(string path, CancellationToken ct)
    {
        using var response = await http.SendAsync(Request(HttpMethod.Get, path), ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>(ct);
    }

    private static int? Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>
    /// Alle Titel, die Lidarr bei beobachteten Alben noch fehlen. Gibt null
    /// zurueck, wenn Lidarr nicht antwortet: eine leere Liste hiesse "nichts
    /// fehlt mehr", und der Abgleich wuerde alle Wuensche wegraeumen.
    /// </summary>
    public async Task<List<Wish>?> GetWantedTracksAsync(int maxAlbums, CancellationToken ct)
    {
        if (!Configured) return null;
        var wishes = new List<Wish>();

        try
        {
            const int pageSize = 50;
            var albums = 0;
            for (var page = 1; albums < maxAlbums; page++)
            {
                var doc = await GetJsonAsync(
                    $"/api/v1/wanted/missing?page={page}&pageSize={pageSize}&includeArtist=true"
                    + "&monitored=true&sortKey=releaseDate&sortDirection=descending", ct);
                if (!doc.TryGetProperty("records", out var records)
                    || records.GetArrayLength() == 0) break;

                foreach (var album in records.EnumerateArray())
                {
                    if (albums++ >= maxAlbums) break;
                    wishes.AddRange(await TracksOfAsync(album, ct));
                }
                if (records.GetArrayLength() < pageSize) break;
            }
            log.LogInformation("Lidarr vermisst {Titel} Titel aus {Alben} Alben",
                wishes.Count, albums);
            return wishes;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            log.LogWarning("Lidarr nicht erreichbar: {Fehler}", ex.Message);
            return null;
        }
    }

    private async Task<List<Wish>> TracksOfAsync(JsonElement album, CancellationToken ct)
    {
        var list = new List<Wish>();
        var albumId = Int(album, "id");
        var artistId = Int(album, "artistId");
        var albumTitle = Str(album, "title");
        var artistName = album.TryGetProperty("artist", out var a) ? Str(a, "artistName") : null;

        // Importiert wird in die Ausgabe, die Lidarr beobachtet. Welche das
        // ist, steht nur am Album, nicht am Titel.
        int? releaseId = null;
        if (album.TryGetProperty("releases", out var releases))
            foreach (var r in releases.EnumerateArray())
                if (r.TryGetProperty("monitored", out var m) && m.ValueKind == JsonValueKind.True)
                    releaseId = Int(r, "id");

        if (albumId is null || artistId is null || releaseId is null
            || string.IsNullOrWhiteSpace(artistName)) return list;

        var tracks = await GetJsonAsync($"/api/v1/track?albumReleaseId={releaseId}", ct);
        foreach (var track in tracks.EnumerateArray())
        {
            var title = Str(track, "title");
            var trackId = Int(track, "id");
            if (string.IsNullOrWhiteSpace(title) || trackId is null) continue;
            if (track.TryGetProperty("hasFile", out var hf) && hf.ValueKind == JsonValueKind.True)
                continue;   // den hat Lidarr schon

            list.Add(new Wish
            {
                Artist = artistName,
                Title = title,
                Album = albumTitle,
                Source = "lidarr",
                NormalizedArtist = TitleMatcher.Normalize(artistName),
                NormalizedTitle = TitleMatcher.Normalize(title),
                LidarrArtistId = artistId,
                LidarrAlbumId = albumId,
                LidarrReleaseId = releaseId,
                LidarrTrackId = trackId,
            });
        }
        return list;
    }

    /// <summary>
    /// Uebergibt einen fertigen Mitschnitt an Lidarr. Die Datei muss allein
    /// in ihrem Ordner liegen, Lidarr liest fuer den Import den ganzen Ordner.
    /// </summary>
    public async Task<ImportResult> ImportAsync(string filePath, Wish wish, CancellationToken ct)
    {
        if (!Configured || !wish.InLidarr)
            return new ImportResult(false, null, "ohne Lidarr-Zuordnung");

        try
        {
            // Die Qualitaet laesst sich Lidarr selbst bestimmen, aus der
            // Datei. Ohne sie nimmt der Import nichts an, und die Nummern
            // dafuer unterscheiden sich zwischen Lidarr-Versionen.
            var folder = Path.GetDirectoryName(filePath)!;
            var scan = await GetJsonAsync(
                $"/api/v1/manualimport?folder={Uri.EscapeDataString(folder)}&filterExistingFiles=false", ct);
            var item = scan.EnumerateArray()
                .FirstOrDefault(i => Str(i, "path") == filePath);
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("quality", out var quality))
                return new ImportResult(false, null, "Lidarr sieht die Datei nicht (Pfad?)");

            var req = Request(HttpMethod.Post, "/api/v1/command");
            req.Content = JsonContent.Create(new
            {
                name = "ManualImport",
                importMode = "move",
                replaceExistingFiles = false,
                files = new[]
                {
                    new
                    {
                        path = filePath,
                        artistId = wish.LidarrArtistId,
                        albumId = wish.LidarrAlbumId,
                        albumReleaseId = wish.LidarrReleaseId,
                        trackIds = new[] { wish.LidarrTrackId!.Value },
                        quality,
                        indexerFlags = 0,
                        downloadId = "",
                        // Sonst waehlt Lidarr womoeglich eine andere Ausgabe
                        // des Albums, und der Titel waere wieder "fehlend".
                        disableReleaseSwitching = true,
                    },
                },
            });
            using var started = await http.SendAsync(req, ct);
            started.EnsureSuccessStatusCode();
            var command = await started.Content.ReadFromJsonAsync<JsonElement>(ct);
            var commandId = Int(command, "id")
                ?? throw new InvalidOperationException("Lidarr gab keine Auftragsnummer zurueck");

            var status = await WaitForCommandAsync(commandId, ct);
            if (status != "completed")
                return new ImportResult(false, null, $"Lidarr-Import endete mit \"{status}\"");

            // "completed" heisst nur, dass der Auftrag lief. Ob die Datei
            // genommen wurde, zeigt erst, dass sie nicht mehr hier liegt.
            if (File.Exists(filePath))
                return new ImportResult(false, null, "Lidarr hat die Datei abgelehnt");

            log.LogInformation("Von Lidarr uebernommen: {Interpret} - {Titel}", wish.Artist, wish.Title);
            return new ImportResult(true, await TrackFilePathAsync(wish.LidarrTrackId.Value, ct), null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            log.LogWarning("Uebergabe an Lidarr gescheitert: {Fehler}", ex.Message);
            return new ImportResult(false, null, $"Lidarr: {ex.Message}");
        }
    }

    private async Task<string> WaitForCommandAsync(int id, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddMinutes(3);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
            var status = Str(await GetJsonAsync($"/api/v1/command/{id}", ct), "status");
            if (status is "completed" or "failed" or "aborted" or "cancelled") return status;
        }
        return "Zeitueberschreitung";
    }

    /// <summary>Wo Lidarr die Datei hingelegt hat, fuer die Anzeige.</summary>
    private async Task<string?> TrackFilePathAsync(int trackId, CancellationToken ct)
    {
        try
        {
            var track = await GetJsonAsync($"/api/v1/track/{trackId}", ct);
            if (Int(track, "trackFileId") is not { } fileId || fileId == 0) return null;
            return Str(await GetJsonAsync($"/api/v1/trackfile/{fileId}", ct), "path");
        }
        catch (Exception) { return null; }   // nur Anzeige, kein Grund zum Scheitern
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
