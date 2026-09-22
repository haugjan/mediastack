using System.Diagnostics;
using System.Text.Json;

namespace Aircheckarr.Services;

/// <summary>
/// Misst nach, was ein Sender wirklich liefert.
///
/// Der Katalog ist Selbstauskunft und faellt regelmaessig auseinander: dort
/// stehen 64000 kbit/s, Videostreams und tote Adressen. Ein Qualitaetsfilter,
/// der dem glaubt, filtert nichts. Deshalb hoert ffprobe kurz hinein und
/// liefert Codec und Bitrate aus dem Datenstrom selbst.
/// </summary>
public sealed class QualityProbe(ILogger<QualityProbe> log)
{
    public sealed record Result(string? Codec, int BitrateKbps, string? Error);

    public async Task<Result> ProbeAsync(string url, CancellationToken ct)
    {
        // -analyzeduration/-probesize klein halten: bei 60000 Sendern zaehlt
        // jede Sekunde, und fuer Codec und Bitrate genuegen wenige Sekunden.
        var psi = new ProcessStartInfo("ffprobe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in (string[])
                 ["-v", "error", "-print_format", "json", "-show_streams",
                  "-select_streams", "a:0", "-analyzeduration", "3000000",
                  "-probesize", "512000", "-i", url])
            psi.ArgumentList.Add(a);

        try
        {
            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException("ffprobe liess sich nicht starten");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));

            var stdout = await proc.StandardOutput.ReadToEndAsync(timeout.Token);
            await proc.WaitForExitAsync(timeout.Token);

            if (proc.ExitCode != 0 || string.IsNullOrWhiteSpace(stdout))
                return new Result(null, 0, "Sender antwortet nicht oder liefert keinen Ton");

            using var doc = JsonDocument.Parse(stdout);
            if (!doc.RootElement.TryGetProperty("streams", out var streams)
                || streams.GetArrayLength() == 0)
                return new Result(null, 0, "kein Tonspur im Stream");

            var s = streams[0];
            var codec = s.TryGetProperty("codec_name", out var c) ? c.GetString() : null;

            var bitrate = 0;
            if (s.TryGetProperty("bit_rate", out var b) && int.TryParse(b.GetString(), out var raw))
                bitrate = raw / 1000;

            // Nicht jeder Stream meldet bit_rate. Dann aus Abtastrate und
            // Kanaelen zu schaetzen waere geraten, also lieber ehrlich 0
            // zurueckgeben und den Sender nicht in den Filter lassen.
            if (codec is not null) codec = codec.ToLowerInvariant();
            return new Result(codec, bitrate,
                bitrate == 0 ? "Sender meldet keine Bitrate" : null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new Result(null, 0, "Zeitueberschreitung beim Messen");
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Messung fehlgeschlagen fuer {Url}", url);
            return new Result(null, 0, ex.Message);
        }
    }
}
