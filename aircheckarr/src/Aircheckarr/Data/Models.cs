namespace Aircheckarr.Data;

/// <summary>
/// Ein Sender aus dem Katalog von radio-browser.info.
///
/// Wichtig sind die beiden Bitraten: <see cref="CatalogBitrate"/> traegt
/// derjenige ein, der den Sender gemeldet hat, und dort stehen Werte wie
/// 64000 kbit/s oder Videostreams. Verlassen darf man sich nur auf
/// <see cref="MeasuredBitrate"/>, das kommt von ffprobe.
/// </summary>
public sealed record Station
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Url { get; init; }
    public string? Country { get; init; }
    public string? Tags { get; init; }
    public string? CatalogCodec { get; init; }
    public int CatalogBitrate { get; init; }

    public string? MeasuredCodec { get; init; }
    public int MeasuredBitrate { get; init; }
    public DateTime? MeasuredAt { get; init; }
    public string? MeasureError { get; init; }

    /// <summary>Wird mitgehoert. Setzt die Qualitaetspruefung.</summary>
    public bool Enabled { get; init; }

    public bool PassesQuality(Settings s) =>
        MeasuredAt is not null
        && MeasureError is null
        && MeasuredBitrate >= s.MinBitrateKbps
        && MeasuredCodec is not null
        && s.AllowedCodecs.Contains(MeasuredCodec);
}

/// <summary>Ein Titelwunsch. Quelle ist entweder die Oberflaeche oder Lidarr.</summary>
public sealed record Wish
{
    public long Id { get; init; }
    public required string Artist { get; init; }
    public required string Title { get; init; }
    public string? Album { get; init; }
    public string Source { get; init; } = "manual";
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime? FulfilledAt { get; init; }

    /// <summary>Zum Vergleich vorbereitete Fassung, siehe TitleMatcher.</summary>
    public string NormalizedArtist { get; init; } = "";
    public string NormalizedTitle { get; init; } = "";

    // Wo der Titel in Lidarr steht. Nur damit laesst sich ein Mitschnitt
    // gezielt diesem einen Titel zuordnen, statt auf Lidarrs eigene
    // Erkennung zu hoffen, die bei einem einzelnen Titel eines Albums gern
    // "passt nicht gut genug" sagt.
    public int? LidarrArtistId { get; init; }
    public int? LidarrAlbumId { get; init; }
    public int? LidarrReleaseId { get; init; }
    public int? LidarrTrackId { get; init; }

    public bool InLidarr =>
        LidarrArtistId is not null && LidarrAlbumId is not null
        && LidarrReleaseId is not null && LidarrTrackId is not null;
}

public enum CaptureState { Recording, Done, Rejected }

/// <summary>Ein Mitschnitt, erfolgreich oder verworfen.</summary>
public sealed record Capture
{
    public long Id { get; init; }
    public long? WishId { get; init; }
    public required string StationId { get; init; }
    public required string StationName { get; init; }
    public required string Artist { get; init; }
    public required string Title { get; init; }
    public DateTime StartedAt { get; init; }
    public double Seconds { get; init; }
    public int Bitrate { get; init; }
    public string? Codec { get; init; }
    public string? Path { get; init; }
    public CaptureState State { get; init; }

    /// <summary>Warum verworfen, oder ein Hinweis zum Ablegen.</summary>
    public string? Reason { get; init; }

    /// <summary>Lidarr hat die Datei uebernommen und selbst einsortiert.</summary>
    public bool ImportedByLidarr { get; init; }
}
