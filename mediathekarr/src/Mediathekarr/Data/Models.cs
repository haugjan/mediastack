namespace Mediathekarr.Data;

// Ein Beitrag, wie ihn MediathekViewWeb liefert.
public sealed record MediathekItem(
    string Channel,
    string Topic,
    string Title,
    string Description,
    long Timestamp,
    int Duration,
    long Size,
    string UrlVideo,
    string UrlVideoHd,
    string UrlVideoLow,
    string UrlSubtitle)
{
    // Die beste verfuegbare Fassung. Die Sender liefern HD nicht immer.
    public string BestUrl => !string.IsNullOrWhiteSpace(UrlVideoHd) ? UrlVideoHd
        : !string.IsNullOrWhiteSpace(UrlVideo) ? UrlVideo
        : UrlVideoLow;

    public DateTimeOffset Published => DateTimeOffset.FromUnixTimeSeconds(Timestamp);
}

// Was wir Sonarr und Radarr als "Release" anbieten. Die Id taucht in der
// .nzb wieder auf, darueber findet der Abholer den Beitrag zurueck.
public sealed record Release(
    string Id,
    string Name,
    int Category,
    long Size,
    DateTimeOffset Published,
    string VideoUrl,
    string SubtitleUrl,
    string Channel);

// Ein Auftrag, den Sonarr oder Radarr per Blackhole abgelegt hat.
public sealed record Job(
    long RowId,
    string ReleaseId,
    string ReleaseName,
    string Category,
    string Status,
    long Bytes,
    string Error,
    DateTimeOffset Created);
