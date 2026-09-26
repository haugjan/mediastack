namespace Mediathekarr;

// Alles ueber Umgebungsvariablen, wie im uebrigen Stack. Die Vorgaben sind
// so gewaehlt, dass der Dienst ohne eine einzige Einstellung laeuft.
public sealed class Settings
{
    // Wohin Sonarr und Radarr ihre .nzb-Dateien legen, und wohin der
    // fertige Mitschnitt gehoert. Beide MUESSEN unter /data liegen, sonst
    // kopieren die Apps beim Import statt zu verlinken.
    public string BlackholeRoot { get; init; } = Env("MEDIATHEKARR_BLACKHOLE", "/data/mediathek/blackhole");
    public string CompleteRoot { get; init; } = Env("MEDIATHEKARR_COMPLETE", "/data/mediathek/complete");
    public string DbPath { get; init; } = Env("MEDIATHEKARR_DB", "/config/mediathekarr.db");

    // Die oeffentliche API von MediathekViewWeb. Sie fasst die Mediatheken
    // von ARD, ZDF, SRF, ORF und weiteren zusammen.
    public string ApiUrl { get; init; } = Env("MEDIATHEKARR_API", "https://mediathekviewweb.de/api/query");

    // Suchergebnisse pro Abfrage. Mehr macht die Trefferliste in Sonarr
    // unuebersichtlich, ohne mehr zu finden.
    public int MaxResults { get; init; } = EnvInt("MEDIATHEKARR_MAX_RESULTS", 60);

    // Beitraege unter dieser Laenge sind Nachrichtenschnipsel und
    // Trailer, keine Sendung. In Sekunden.
    public int MinDurationSeconds { get; init; } = EnvInt("MEDIATHEKARR_MIN_DURATION", 600);

    // Sender, die beruecksichtigt werden. Leer heisst: alle.
    public string[] Channels { get; init; } =
        Env("MEDIATHEKARR_CHANNELS", "ARD,ZDF,SRF,ORF,3Sat,arte,BR,NDR,WDR,SWR,MDR,HR,RBB,KiKA,DW,Funk,PHOENIX")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string Env(string key, string fallback)
    {
        var v = Environment.GetEnvironmentVariable(key);
        return string.IsNullOrWhiteSpace(v) ? fallback : v.Trim();
    }

    private static int EnvInt(string key, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(key), out var v) && v > 0 ? v : fallback;
}
