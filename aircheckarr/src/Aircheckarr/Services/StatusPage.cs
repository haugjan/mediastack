using System.Text.Json;
using System.Text.RegularExpressions;

namespace Aircheckarr.Services;

/// <summary>
/// Liest den laufenden Titel eines Senders von seiner Statusseite, ohne den
/// Audiostrom zu oeffnen. So lassen sich Hunderte Sender fuer ein paar
/// Kilobyte je Abfrage beobachten, wo ein offener Strom rund um die Uhr
/// 128 bis 320 kbit/s kostet.
///
/// Drei Arten gibt es: Icecast (/status-json.xsl), Shoutcast 2
/// (/stats?json=1) und Shoutcast 1 (/7.html). Welche ein Server hat, wird
/// einmal ausprobiert und gemerkt. Gut die Haelfte der Sender hat gar
/// keine erreichbare, die koennen nur mitgehoert werden.
///
/// Die Falle: ein Icecast-Server fuehrt oft Dutzende Sender. Der Titel muss
/// zum Mountpoint des Stroms passen, sonst beobachtet man den Nachbarn.
/// </summary>
public sealed partial class StatusPage(HttpClient http)
{
    public const string None = "none", Icecast = "icecast",
                        Shoutcast2 = "shoutcast2", Shoutcast1 = "shoutcast1";

    [GeneratedRegex(@"<body[^>]*>(.*?)</body>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Body();

    [GeneratedRegex(@"""listenurl""\s*:\s*""([^""]*)""")]
    private static partial Regex ListenUrl();

    [GeneratedRegex(@"""(title|artist)""\s*:\s*""((?:[^""\\]|\\.)*)""")]
    private static partial Regex TitleField();

    /// <summary>
    /// Probiert die drei Arten der Reihe nach, liefert die erste, die einen
    /// Titel hat. None nur bei eindeutiger Antwort (404, keine Statusseite);
    /// null, wenn es an Netz oder Zeit lag. Sonst stempelte ein einzelner
    /// langsamer Moment einen Sender dauerhaft als unbeobachtbar ab - im
    /// Praxistest genau so passiert.
    /// </summary>
    public async Task<string?> DetectAsync(string streamUrl, CancellationToken ct)
    {
        var unsure = false;
        foreach (var kind in (string[])[Icecast, Shoutcast2, Shoutcast1])
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(await ReadCoreAsync(streamUrl, kind, ct))) return kind;
            }
            catch (HttpRequestException e) when (e.StatusCode is not null) { /* eindeutig: gibt es nicht */ }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { unsure = true; }
            catch (Exception e) when (e is JsonException or InvalidOperationException) { /* keine Statusseite */ }
        }
        return unsure ? null : None;
    }

    /// <summary>Der laufende Titel als "Interpret - Titel", oder null.</summary>
    public async Task<string?> ReadTitleAsync(string streamUrl, string kind, CancellationToken ct)
    {
        try { return await ReadCoreAsync(streamUrl, kind, ct); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException
                                     or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    private async Task<string?> ReadCoreAsync(string streamUrl, string kind, CancellationToken ct)
    {
        if (!Uri.TryCreate(streamUrl, UriKind.Absolute, out var stream)) return null;
        var root = $"{stream.Scheme}://{stream.Authority}";
        return kind switch
        {
            Icecast => IcecastTitle(await GetAsync($"{root}/status-json.xsl", ct), stream.AbsolutePath),
            Shoutcast2 => Shoutcast2Title(await GetAsync($"{root}/stats?sid=1&json=1", ct)),
            Shoutcast1 => Shoutcast1Title(await GetAsync($"{root}/7.html", ct)),
            _ => null,
        };
    }

    private async Task<string> GetAsync(string url, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // Grosszuegig: im Praxistest brauchte allein die Namensaufloesung
        // ueber Tailscales DNS bis zu drei Sekunden. Mit sechs Sekunden
        // galten deshalb halbe Sendernetze als "unsicher".
        cts.CancelAfter(TimeSpan.FromSeconds(15));
        using var res = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        res.EnsureSuccessStatusCode();
        // Eine Statusseite ist klein. Wer hier Megabytes liefert, schickt in
        // Wahrheit den Audiostrom, und den wollen wir gerade nicht.
        if (res.Content.Headers.ContentType?.MediaType is { } type && type.StartsWith("audio/"))
            throw new InvalidOperationException("Audiostrom statt Statusseite");
        var body = await res.Content.ReadAsStringAsync(cts.Token);
        return body.Length > 512_000 ? throw new InvalidOperationException("zu gross") : body;
    }

    private static string? IcecastTitle(string json, string streamPath)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        // Icecast schreibt gern rohe Steuerzeichen in Titel, und grosse
        // Server (SRG) liefern die Seite abgeschnitten. Dann im Text suchen.
        catch (JsonException) { return IcecastTitleLenient(json, streamPath); }
        using var _ = doc;
        if (!doc.RootElement.TryGetProperty("icestats", out var stats)
            || !stats.TryGetProperty("source", out var source)) return null;

        var sources = source.ValueKind == JsonValueKind.Array
            ? source.EnumerateArray().ToList() : [source];

        // Den passenden Mountpoint suchen. Nur wenn der Server genau einen
        // Sender fuehrt, darf es auch ohne Treffer dieser eine sein.
        JsonElement? pick = null;
        foreach (var s in sources)
            if (s.TryGetProperty("listenurl", out var l) && l.GetString() is { } lu
                && Uri.TryCreate(lu, UriKind.Absolute, out var u)
                && string.Equals(u.AbsolutePath.TrimEnd('/'), streamPath.TrimEnd('/'),
                                 StringComparison.OrdinalIgnoreCase))
                pick = s;
        if (pick is null && sources.Count == 1) pick = sources[0];
        if (pick is not { } p) return null;

        var title = Str(p, "title");
        var artist = Str(p, "artist");
        if (!string.IsNullOrWhiteSpace(artist) && !string.IsNullOrWhiteSpace(title))
            return $"{artist} - {title}";
        return string.IsNullOrWhiteSpace(title) ? Str(p, "yp_currently_playing") : title;
    }

    /// <summary>
    /// Nachsichtige Suche: den eigenen Mountpoint im Text finden und Titel
    /// und Interpret aus demselben Block in geschweiften Klammern lesen.
    /// </summary>
    private static string? IcecastTitleLenient(string json, string streamPath)
    {
        foreach (Match m in ListenUrl().Matches(json))
        {
            if (!Uri.TryCreate(m.Groups[1].Value, UriKind.Absolute, out var u)
                || !string.Equals(u.AbsolutePath.TrimEnd('/'), streamPath.TrimEnd('/'),
                                  StringComparison.OrdinalIgnoreCase)) continue;

            var from = json.LastIndexOf('{', m.Index);
            var to = json.IndexOf('}', m.Index);
            if (from < 0 || to < 0) return null;
            string? title = null, artist = null;
            foreach (Match f in TitleField().Matches(json[from..to]))
            {
                string value;
                try { value = JsonSerializer.Deserialize<string>($"\"{f.Groups[2].Value}\"") ?? ""; }
                catch (JsonException) { value = f.Groups[2].Value; }
                if (f.Groups[1].Value == "title") title = value; else artist = value;
            }
            return !string.IsNullOrWhiteSpace(artist) && !string.IsNullOrWhiteSpace(title)
                ? $"{artist} - {title}" : title;
        }
        return null;
    }

    private static string? Shoutcast2Title(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return Str(doc.RootElement, "songtitle");
    }

    // 7.html ist eine Zeile: Hoerer,Status,Spitze,Maximum,Eindeutige,Bitrate,Titel.
    // Der Titel selbst darf Kommas enthalten, deshalb alles ab dem siebten Feld.
    private static string? Shoutcast1Title(string html)
    {
        var m = Body().Match(html);
        var parts = (m.Success ? m.Groups[1].Value : html).Split(',');
        return parts.Length >= 7 ? System.Net.WebUtility.HtmlDecode(string.Join(',', parts[6..])).Trim() : null;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
