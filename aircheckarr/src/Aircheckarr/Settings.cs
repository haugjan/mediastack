namespace Aircheckarr;

/// <summary>
/// Alle Einstellungen kommen aus Umgebungsvariablen, so wie im uebrigen
/// Stack auch. Kein Konfigurationsdialog, keine Datei, die auseinanderlaufen
/// kann: was in der .env steht, gilt.
/// </summary>
public sealed class Settings
{
    /// <summary>Wohin fertige Mitschnitte geschrieben werden.</summary>
    public string LibraryPath { get; init; } = "/data/media/music";

    /// <summary>Arbeitsverzeichnis fuer laufende Aufnahmen.</summary>
    public string WorkPath { get; init; } = "/data/work";

    /// <summary>Datenbank mit Stationen, Wuenschen und Mitschnitten.</summary>
    public string DatabasePath { get; init; } = "/config/aircheckarr.db";

    /// <summary>Wie viele Sender gleichzeitig mitgehoert werden.</summary>
    public int MaxConcurrentStations { get; init; } = 12;

    /// <summary>
    /// Mindestbitrate in kbit/s. Gemeint ist die GEMESSENE, nicht die im
    /// Katalog eingetragene: dort stehen Fantasiewerte bis 64000.
    /// </summary>
    public int MinBitrateKbps { get; init; } = 128;

    /// <summary>Zugelassene Codecs, alles andere wird verworfen.</summary>
    public string[] AllowedCodecs { get; init; } = ["mp3", "aac"];

    /// <summary>Ab welcher Aehnlichkeit ein Titel als Treffer gilt (0..1).</summary>
    public double MatchThreshold { get; init; } = 0.86;

    /// <summary>Kuerzer als das ist kein Lied, sondern ein Jingle.</summary>
    public int MinTrackSeconds { get; init; } = 70;

    /// <summary>Laenger als das ist eine Sendung, kein Lied.</summary>
    public int MaxTrackSeconds { get; init; } = 900;

    public string? LidarrUrl { get; init; }
    public string? LidarrApiKey { get; init; }

    /// <summary>
    /// Wie oft Lidarrs Fehlliste abgeglichen wird, in Minuten. 0 schaltet
    /// den Abgleich ab; der Knopf in der Oberflaeche geht trotzdem.
    /// </summary>
    public int LidarrSyncMinutes { get; init; } = 15;

    /// <summary>
    /// Hoechstens so viele fehlende Alben, die juengsten zuerst. Jedes kostet
    /// eine Anfrage an Lidarr, und tausend Wuensche machen jeden Titelwechsel
    /// teurer, ohne dass mehr davon im Radio liefe.
    /// </summary>
    public int LidarrMaxAlbums { get; init; } = 250;

    /// <summary>Katalogquelle. Ein Spiegel, falls de1 einmal ausfaellt.</summary>
    public string RadioBrowserUrl { get; init; } = "https://de1.api.radio-browser.info";

    public static Settings FromEnvironment()
    {
        string? Get(string key) =>
            Environment.GetEnvironmentVariable(key) is { Length: > 0 } v ? v : null;

        int GetInt(string key, int fallback) =>
            int.TryParse(Get(key), out var v) ? v : fallback;

        double GetDouble(string key, double fallback) =>
            double.TryParse(Get(key), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : fallback;

        return new Settings
        {
            LibraryPath = Get("AIRCHECKARR_LIBRARY") ?? "/data/media/music",
            WorkPath = Get("AIRCHECKARR_WORK") ?? "/data/work",
            DatabasePath = Get("AIRCHECKARR_DB") ?? "/config/aircheckarr.db",
            MaxConcurrentStations = GetInt("AIRCHECKARR_MAX_STATIONS", 12),
            MinBitrateKbps = GetInt("AIRCHECKARR_MIN_BITRATE", 128),
            AllowedCodecs = (Get("AIRCHECKARR_CODECS") ?? "mp3,aac")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(c => c.ToLowerInvariant()).ToArray(),
            MatchThreshold = GetDouble("AIRCHECKARR_MATCH_THRESHOLD", 0.86),
            MinTrackSeconds = GetInt("AIRCHECKARR_MIN_SECONDS", 70),
            MaxTrackSeconds = GetInt("AIRCHECKARR_MAX_SECONDS", 900),
            LidarrUrl = Get("LIDARR_URL"),
            LidarrApiKey = Get("LIDARR_API_KEY"),
            LidarrSyncMinutes = Math.Max(0, GetInt("AIRCHECKARR_LIDARR_SYNC", 15)),
            LidarrMaxAlbums = Math.Max(1, GetInt("AIRCHECKARR_LIDARR_ALBUMS", 250)),
            RadioBrowserUrl = Get("AIRCHECKARR_RADIOBROWSER") ?? "https://de1.api.radio-browser.info",
        };
    }
}
