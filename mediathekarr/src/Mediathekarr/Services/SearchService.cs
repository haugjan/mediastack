using Mediathekarr.Data;

namespace Mediathekarr.Services;

// Uebersetzt eine Newznab-Anfrage in eine Mediathek-Abfrage und die
// Treffer zurueck in Releases.
public sealed class SearchService(
    MediathekViewClient mediathek,
    ArrClient arr,
    Database db,
    ILogger<SearchService> log)
{
    public const int CatMovies = 2000;
    public const int CatTv = 5000;

    public async Task<IReadOnlyList<Release>> TvAsync(string query, int season, int episode, CancellationToken ct)
    {
        var items = await mediathek.QueryAsync(query, ct);
        if (items.Count == 0) return [];

        // Mit Staffel und Folge: das Sendedatum bei Sonarr erfragen und nur
        // Beitraege dieses Tages anbieten. Ohne diesen Schritt haette Sonarr
        // hunderte Treffer ohne Bezug zur gesuchten Folge.
        if (season > 0 && episode > 0)
        {
            var (air, title) = await arr.EpisodeAsync(query, season, episode, ct);
            if (air is null)
            {
                log.LogDebug("Kein Sendedatum fuer {Serie} S{S}E{E}", query, season, episode);
                return [];
            }
            var sameDay = items
                .Where(i => Math.Abs((DateOnly.FromDateTime(i.Published.LocalDateTime).DayNumber - air.Value.DayNumber)) <= 1)
                .ToList();
            return sameDay.Select(i => Make(
                ReleaseNamer.Tv(query, season, episode, title, i), CatTv, i)).ToList();
        }

        return items.Select(i => Make(ReleaseNamer.TvByDate(
            string.IsNullOrWhiteSpace(i.Topic) ? query : i.Topic, i), CatTv, i)).ToList();
    }

    public async Task<IReadOnlyList<Release>> MovieAsync(string query, string imdbId, CancellationToken ct)
    {
        var title = query;
        var year = 0;
        if (!string.IsNullOrWhiteSpace(imdbId))
        {
            var (t, y) = await arr.MovieAsync(imdbId, ct);
            if (!string.IsNullOrWhiteSpace(t)) { title = t; year = y; }
        }
        if (string.IsNullOrWhiteSpace(title)) return [];

        var items = await mediathek.QueryAsync(title, ct);
        // Beim Film zaehlt der Titel, nicht die Reihe: ein Beitrag, in dem
        // der Filmtitel nur im Thema steht, ist meist ein Bericht darueber.
        var hits = items.Where(i =>
            i.Title.Contains(title, StringComparison.OrdinalIgnoreCase) ||
            i.Topic.Contains(title, StringComparison.OrdinalIgnoreCase)).ToList();
        return hits.Select(i => Make(ReleaseNamer.Movie(title, year, i), CatMovies, i)).ToList();
    }

    public async Task<IReadOnlyList<Release>> FreeAsync(string query, CancellationToken ct)
    {
        var items = await mediathek.QueryAsync(query, ct);
        return items.Select(i => Make(ReleaseNamer.TvByDate(
            string.IsNullOrWhiteSpace(i.Topic) ? query : i.Topic, i), CatTv, i)).ToList();
    }

    private Release Make(string name, int category, MediathekItem item)
    {
        // Die Id muss stabil sein: dieselbe Sendung soll nach einem
        // Neustart dieselbe Id haben, sonst zeigt eine alte .nzb ins Leere.
        var id = Convert.ToHexString(
            System.Security.Cryptography.MD5.HashData(
                System.Text.Encoding.UTF8.GetBytes(item.BestUrl)))[..16].ToLowerInvariant();

        var r = new Release(id, name, category, item.Size, item.Published,
            item.BestUrl, item.UrlSubtitle, item.Channel);
        db.Remember(r);
        return r;
    }
}
