using System.Diagnostics;

namespace Mediathekarr.Services;

// Laedt Video und Untertitel vom Sender. Gemeinsam fuer beide Wege: den
// Blackhole-Abholer fuer Sonarr und Radarr und den Direkt-Download aus der
// Oberflaeche. Der Fortschritt geht an einen Rueckruf, damit die Anzeige
// mitzaehlen kann, ohne dass diese Klasse wissen muss, wer zuschaut.
//
// Die Falle: nicht jeder Sender liefert eine Datei. SRF und teils ORF geben
// eine HLS-Wiedergabeliste (.m3u8) heraus, und wer die einfach herunterlaedt,
// hat vier Kilobyte Text unter einem .mp4-Namen. Solche Adressen setzt
// ffmpeg aus ihren Stuecken zusammen, ohne neu zu kodieren.
public sealed class Fetcher(IHttpClientFactory httpFactory, ILogger<Fetcher> log)
{
    /// <param name="durationSeconds">
    /// Laenge der Sendung, nur fuer den Fortschritt bei Wiedergabelisten:
    /// dort ist die Dateigroesse vorher unbekannt.
    /// </param>
    public async Task<long> DownloadAsync(string url, string path, CancellationToken ct,
                                          Action<long, long>? progress = null,
                                          int durationSeconds = 0)
    {
        if (IsPlaylist(url)) return await StreamAsync(url, path, durationSeconds, progress, ct);

        var http = httpFactory.CreateClient("download");
        using var res = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        res.EnsureSuccessStatusCode();
        var total = res.Content.Headers.ContentLength ?? 0;
        await using var src = await res.Content.ReadAsStreamAsync(ct);
        await using var dst = File.Create(path);

        var buffer = new byte[1 << 16];
        long done = 0;
        int n;
        while ((n = await src.ReadAsync(buffer, ct)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, n), ct);
            done += n;
            progress?.Invoke(done, total);
        }
        return done;
    }

    private static bool IsPlaylist(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u)
        && u.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase);

    private static async Task<long> StreamAsync(string url, string path, int durationSeconds,
                                                Action<long, long>? progress, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("ffmpeg")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        // -f mp4, weil die Datei waehrend des Ladens auf ".teil" endet und
        // ffmpeg das Format sonst am Namen raten wuerde. aac_adtstoasc, weil
        // die HLS-Stuecke AAC im ADTS-Rahmen tragen, den MP4 nicht aufnimmt.
        foreach (var a in (string[])["-hide_banner", "-loglevel", "error", "-nostats", "-y",
                                     "-i", url, "-c", "copy", "-bsf:a", "aac_adtstoasc",
                                     "-f", "mp4", "-progress", "pipe:1", path])
            psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("ffmpeg liess sich nicht starten");
        var stderr = proc.StandardError.ReadToEndAsync(ct);
        try
        {
            // ffmpeg meldet laufend die geschriebene Groesse und die Zeit im
            // Film. Daraus laesst sich die Endgroesse hochrechnen.
            long size = 0;
            string? line;
            while ((line = await proc.StandardOutput.ReadLineAsync(ct)) is not null)
            {
                if (line.StartsWith("total_size=", StringComparison.Ordinal))
                    _ = long.TryParse(line[11..], out size);
                else if (line.StartsWith("out_time_us=", StringComparison.Ordinal)
                         && long.TryParse(line[12..], out var us) && us > 0 && durationSeconds > 0)
                    progress?.Invoke(size, (long)(size * (durationSeconds * 1_000_000.0 / us)));
            }
            await proc.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(true); } catch { /* war schon fort */ }
            throw;
        }

        var error = await stderr;
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"ffmpeg: {error.Trim().Split('\n').LastOrDefault()}");
        return new FileInfo(path).Length;
    }

    // Die Sender liefern TTML oder VTT. Plex und Bazarr wollen SRT, und
    // ffmpeg wandelt das ohne Umschweife um. Klappt es nicht, ist der
    // Mitschnitt trotzdem brauchbar - deshalb nur eine Warnung.
    public async Task SubtitleAsync(string url, string srtPath, CancellationToken ct)
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
}
