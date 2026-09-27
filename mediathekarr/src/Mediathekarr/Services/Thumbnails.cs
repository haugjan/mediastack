using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace Mediathekarr.Services;

// Vorschaubilder fuer die Kacheln beim Stoebern. Die Filmliste hat keine
// Bilder, aber jede Sendung hat eine Webseite, und fast jede Webseite nennt
// ihr Vorschaubild im Kopf als "og:image" - fuer Facebook und Co.
//
// Geholt wird erst, wenn der Browser eine Kachel wirklich zeigt, und dann
// auf der Platte behalten: 11 000 Sendungen auf Vorrat abzurufen hiesse,
// die Sender-Webseiten zu ueberrennen. Wer kein Bild hat, bekommt eine
// Markierung und wird eine Woche lang nicht erneut gefragt.
public sealed partial class Thumbnails(Settings cfg, IHttpClientFactory httpFactory, ILogger<Thumbnails> log)
{
    [GeneratedRegex(@"<meta[^>]+(?:property|name)=[""']og:image[""'][^>]+content=[""']([^""']+)[""']|<meta[^>]+content=[""']([^""']+)[""'][^>]+(?:property|name)=[""']og:image[""']",
        RegexOptions.IgnoreCase)]
    private static partial Regex OgImage();

    private readonly SemaphoreSlim _slots = new(4);
    private readonly ConcurrentDictionary<string, Task<string?>> _inFlight = new();

    private string Dir => Path.Combine(Path.GetDirectoryName(cfg.DbPath)!, "bilder");

    /// <summary>Pfad der zwischengespeicherten Bilddatei, oder null ohne Bild.</summary>
    public Task<string?> GetAsync(string website)
    {
        if (!Uri.TryCreate(website, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https"))
            return Task.FromResult<string?>(null);

        var key = Data.MediathekItem.IdFor(website);
        var file = Path.Combine(Dir, key + ".jpg");
        var none = Path.Combine(Dir, key + ".kein");
        if (File.Exists(file)) return Task.FromResult<string?>(file);
        if (File.Exists(none) && File.GetLastWriteTimeUtc(none) > DateTime.UtcNow.AddDays(-7))
            return Task.FromResult<string?>(null);

        // Zeigen zehn Kacheln dieselbe Seite, wird sie nur einmal geholt. Ohne
        // das Token der Anfrage: bricht der erste Browser ab, warten die
        // anderen trotzdem auf dasselbe Bild. Begrenzt ist es durch das
        // Zeitlimit des HttpClient.
        return _inFlight.GetOrAdd(key, _ => FetchAsync(website, file, none, CancellationToken.None)
            .ContinueWith(t => { _inFlight.TryRemove(key, out Task<string?>? _); return t.IsCompletedSuccessfully ? t.Result : null; },
                          TaskScheduler.Default));
    }

    private async Task<string?> FetchAsync(string website, string file, string none, CancellationToken ct)
    {
        await _slots.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(Dir);
            var http = httpFactory.CreateClient("bilder");
            var html = await ReadLimitedAsync(http, website, 600_000, ct);
            var m = OgImage().Match(html);
            var src = m.Success ? System.Net.WebUtility.HtmlDecode(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value) : null;
            // PHOENIX schreibt dort eine unausgefuellte Vorlage hin
            // ("{{ meta.image ... }}"). Nur echte Adressen gelten.
            if (src is null || !Uri.TryCreate(new Uri(website), src, out var img)
                || img.Scheme is not ("http" or "https"))
            {
                await File.WriteAllTextAsync(none, "", ct);
                return null;
            }

            using var res = await http.GetAsync(img, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!res.IsSuccessStatusCode || res.Content.Headers.ContentType?.MediaType?.StartsWith("image/") != true
                || res.Content.Headers.ContentLength > 3_000_000)
            {
                await File.WriteAllTextAsync(none, "", ct);
                return null;
            }
            var temp = file + ".teil";
            await using (var dst = File.Create(temp))
                await (await res.Content.ReadAsStreamAsync(ct)).CopyToAsync(dst, ct);
            File.Move(temp, file, true);
            return file;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or UriFormatException)
        {
            log.LogDebug("Kein Bild fuer {Seite}: {Fehler}", website, e.Message);
            try { await File.WriteAllTextAsync(none, "", CancellationToken.None); } catch (IOException) { }
            return null;
        }
        finally { _slots.Release(); }
    }

    private static async Task<string> ReadLimitedAsync(HttpClient http, string url, int limit, CancellationToken ct)
    {
        using var res = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        res.EnsureSuccessStatusCode();
        await using var s = await res.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[limit];
        var total = 0;
        int n;
        // Das og:image steht im Kopf der Seite; den Rest braucht es nicht.
        while (total < limit && (n = await s.ReadAsync(buffer.AsMemory(total), ct)) > 0) total += n;
        return System.Text.Encoding.UTF8.GetString(buffer, 0, total);
    }
}
