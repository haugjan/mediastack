using System.Diagnostics;
using System.Xml.Linq;
using Mediathekarr.Data;

namespace Mediathekarr.Services;

// Der Abholer. Sonarr und Radarr legen ueber ihren "Usenet Blackhole"
// Download-Client eine .nzb in den Ordner; darin steht nur unsere Id. Wir
// laden die Datei vom Sender herunter und legen sie unter dem
// Release-Namen im Fertig-Ordner ab. Von dort holt die App sie sich selbst.
//
// Warum Blackhole und kein nachgebauter SABnzbd: Blackhole ist eine
// eingebaute, dokumentierte Funktion der Apps. Ein nachgebauter
// Download-Client muesste deren komplette API mitspielen und braeche bei
// jeder Aenderung daran.
public sealed class BlackholeWatcher(
    Settings cfg, Database db, IHttpClientFactory httpFactory, ILogger<BlackholeWatcher> log)
    : BackgroundService
{
    private static readonly string[] Kinds = ["tv", "movies"];

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        foreach (var k in Kinds)
        {
            Directory.CreateDirectory(Path.Combine(cfg.BlackholeRoot, k));
            Directory.CreateDirectory(Path.Combine(cfg.CompleteRoot, k));
        }
        log.LogInformation("Beobachte {Pfad}", cfg.BlackholeRoot);

        while (!ct.IsCancellationRequested)
        {
            foreach (var kind in Kinds)
            {
                var dir = Path.Combine(cfg.BlackholeRoot, kind);
                foreach (var nzb in Directory.EnumerateFiles(dir, "*.nzb"))
                {
                    try { await HandleAsync(nzb, kind, ct); }
                    catch (Exception e) when (e is not OperationCanceledException)
                    {
                        log.LogError("Auftrag {Datei} fehlgeschlagen: {Fehler}", Path.GetFileName(nzb), e.Message);
                        Move(nzb, nzb + ".fehler");
                    }
                }
            }
            db.Prune(TimeSpan.FromDays(7));
            try { await Task.Delay(TimeSpan.FromSeconds(15), ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task HandleAsync(string nzbPath, string kind, CancellationToken ct)
    {
        var id = IdFrom(nzbPath);
        var release = id is null ? null : db.Find(id);
        if (release is null)
        {
            log.LogWarning("Zu {Datei} gibt es keinen bekannten Beitrag mehr", Path.GetFileName(nzbPath));
            Move(nzbPath, nzbPath + ".unbekannt");
            return;
        }

        var job = db.AddJob(release.Id, release.Name, kind);
        log.LogInformation("Hole {Name}", release.Name);

        // Erst in einen Nachbarordner laden, dann umbenennen: die App soll
        // nie eine halb geladene Datei sehen und importieren.
        var target = Path.Combine(cfg.CompleteRoot, kind, release.Name);
        var temp = target + ".teil";
        Directory.CreateDirectory(temp);
        var file = Path.Combine(temp, release.Name + ".mp4");

        long bytes;
        try
        {
            bytes = await DownloadAsync(release.VideoUrl, file, ct);
            if (!string.IsNullOrWhiteSpace(release.SubtitleUrl))
                await SubtitleAsync(release.SubtitleUrl, Path.Combine(temp, release.Name + ".ger.srt"), ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            db.FinishJob(job, "fehlgeschlagen", 0, e.Message);
            Directory.Delete(temp, true);
            Move(nzbPath, nzbPath + ".fehler");
            return;
        }

        if (Directory.Exists(target)) Directory.Delete(target, true);
        Directory.Move(temp, target);
        File.Delete(nzbPath);
        db.FinishJob(job, "fertig", bytes);
        log.LogInformation("Fertig: {Name} ({MB} MB)", release.Name, bytes / 1_000_000);
    }

    private async Task<long> DownloadAsync(string url, string path, CancellationToken ct)
    {
        var http = httpFactory.CreateClient("download");
        using var res = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        res.EnsureSuccessStatusCode();
        await using var src = await res.Content.ReadAsStreamAsync(ct);
        await using var dst = File.Create(path);
        await src.CopyToAsync(dst, ct);
        return dst.Length;
    }

    // Die Sender liefern TTML oder VTT. Plex und Bazarr wollen SRT, und
    // ffmpeg wandelt das ohne Umschweife um. Klappt es nicht, ist der
    // Mitschnitt trotzdem brauchbar - deshalb nur eine Warnung.
    private async Task SubtitleAsync(string url, string srtPath, CancellationToken ct)
    {
        var raw = Path.ChangeExtension(srtPath, ".quelle");
        try
        {
            await DownloadAsync(url, raw, ct);
            var p = Process.Start(new ProcessStartInfo("ffmpeg",
                $"-hide_banner -loglevel error -y -i \"{raw}\" \"{srtPath}\"") { RedirectStandardError = true });
            if (p is not null)
            {
                await p.WaitForExitAsync(ct);
                if (p.ExitCode != 0) log.LogDebug("Untertitel liessen sich nicht umwandeln");
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.LogDebug("Untertitel uebersprungen: {Fehler}", e.Message);
        }
        finally
        {
            if (File.Exists(raw)) File.Delete(raw);
        }
    }

    // Unsere Id steht im Kopf der .nzb. Der Rest der Datei ist nur da,
    // damit sie wie eine echte NZB aussieht.
    private static string? IdFrom(string path)
    {
        try
        {
            var doc = XDocument.Load(path);
            return doc.Descendants()
                .Where(e => e.Name.LocalName == "meta" && (string?)e.Attribute("type") == "mediathekarr-id")
                .Select(e => e.Value.Trim())
                .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void Move(string from, string to)
    {
        try { File.Move(from, to, true); } catch (IOException) { /* beim naechsten Lauf erneut */ }
    }
}
