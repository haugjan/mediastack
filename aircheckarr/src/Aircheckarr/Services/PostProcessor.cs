using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Aircheckarr.Services;

/// <summary>
/// Macht aus einem Rohmitschnitt eine Datei, die in einer Bibliothek nicht
/// stoert: Raender abschneiden, Tags setzen, einsortieren.
///
/// Entscheidend ist, dass NICHT neu kodiert wird. Ein Radiostream ist schon
/// verlustbehaftet; ihn zum Schneiden noch einmal durch einen Kodierer zu
/// schicken, kostet ein zweites Mal Qualitaet. Stattdessen sucht ein erster
/// Durchlauf die Stille an den Raendern, der zweite schneidet mit
/// "-c copy" genau dort. Das ist verlustfrei und nebenbei schnell.
/// </summary>
public sealed partial class PostProcessor(Settings settings, ILogger<PostProcessor> log)
{
    [GeneratedRegex(@"silence_end:\s*([0-9.]+)")]
    private static partial Regex SilenceEnd();

    [GeneratedRegex(@"silence_start:\s*([0-9.]+)")]
    private static partial Regex SilenceStart();

    [GeneratedRegex(@"[^\w\s.\-()&,']")]
    private static partial Regex UnsafeFileChars();

    public sealed record Result(bool Ok, string? Path, double Seconds, string? Reason);

    public async Task<Result> FinalizeAsync(string tempPath, string artist, string title,
                                            string? album, CancellationToken ct)
    {
        var duration = await GetDurationAsync(tempPath, ct);
        if (duration <= 0)
            return new Result(false, null, 0, "Datei liess sich nicht lesen");

        if (duration < settings.MinTrackSeconds)
            return new Result(false, null, duration,
                $"zu kurz ({duration:F0}s), vermutlich Jingle oder Werbung");

        if (duration > settings.MaxTrackSeconds)
            return new Result(false, null, duration,
                $"zu lang ({duration:F0}s), vermutlich eine Sendung statt eines Titels");

        var (start, end) = await DetectTrimAsync(tempPath, duration, ct);
        var trimmed = end - start;
        if (trimmed < settings.MinTrackSeconds)
            return new Result(false, null, trimmed, "nach dem Beschneiden zu kurz");

        // Der Zielcontainer haengt am Codec, und daran haengen die Tags:
        // rohes AAC (ADTS) hat gar keinen Platz fuer Interpret und Titel,
        // ffmpeg verwirft -metadata dort stillschweigend. Die Datei laege
        // dann als "unbekannter Interpret" in der Bibliothek. MP4 kann es,
        // und das Umpacken ist verlustfrei.
        var codec = Path.GetExtension(tempPath).TrimStart('.').ToLowerInvariant();
        var extension = codec == "mp3" ? ".mp3" : ".m4a";

        var target = BuildTargetPath(artist, title, album, extension);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);

        var args = new List<string>
        {
            "-hide_banner", "-loglevel", "error", "-y",
            "-ss", start.ToString("F3", CultureInfo.InvariantCulture),
            "-to", end.ToString("F3", CultureInfo.InvariantCulture),
            "-i", tempPath,
            "-c", "copy",
        };
        // ADTS-Rahmen passen nicht unveraendert in einen MP4-Container.
        if (extension == ".m4a") { args.Add("-bsf:a"); args.Add("aac_adtstoasc"); }
        args.AddRange([
            "-metadata", $"artist={artist}",
            "-metadata", $"title={title}",
            "-metadata", $"album={album ?? "Radio-Mitschnitte"}",
            "-metadata", "comment=Mitschnitt aus dem Webradio (aircheckarr)",
            target,
        ]);

        var (code, stderr) = await RunAsync("ffmpeg", args, TimeSpan.FromMinutes(2), ct);
        if (code != 0 || !File.Exists(target))
        {
            if (File.Exists(target)) File.Delete(target);
            return new Result(false, null, trimmed, $"ffmpeg brach ab: {stderr[..Math.Min(160, stderr.Length)]}");
        }

        log.LogInformation("Abgelegt: {Pfad} ({Sekunden:F0}s)", target, trimmed);
        return new Result(true, target, trimmed, null);
    }

    /// <summary>
    /// Sucht Stille am Anfang und am Ende. Ueberblendet ein Sender hart,
    /// gibt es keine, dann bleibt der Schnitt eben an den Rohgrenzen.
    /// </summary>
    private async Task<(double Start, double End)> DetectTrimAsync(
        string path, double duration, CancellationToken ct)
    {
        var (_, stderr) = await RunAsync("ffmpeg",
            ["-hide_banner", "-nostats", "-i", path,
             "-af", "silencedetect=noise=-45dB:d=0.35", "-f", "null", "-"],
            TimeSpan.FromMinutes(2), ct);

        var start = 0.0;
        var end = duration;

        // Nur Stille, die WIRKLICH am Rand liegt, darf weg. Eine Pause
        // mitten im Lied bleibt selbstverstaendlich drin.
        var firstEnd = SilenceEnd().Match(stderr);
        if (firstEnd.Success
            && double.TryParse(firstEnd.Groups[1].Value, NumberStyles.Float,
                               CultureInfo.InvariantCulture, out var se)
            && se is > 0 and < 4)
            start = se;

        foreach (Match m in SilenceStart().Matches(stderr))
            if (double.TryParse(m.Groups[1].Value, NumberStyles.Float,
                                CultureInfo.InvariantCulture, out var ss)
                && ss > duration - 4 && ss > start)
                end = ss;

        return (start, Math.Max(end, start));
    }

    private async Task<double> GetDurationAsync(string path, CancellationToken ct)
    {
        var (code, output) = await RunAsync("ffprobe",
            ["-v", "error", "-show_entries", "format=duration",
             "-of", "default=noprint_wrappers=1:nokey=1", path],
            TimeSpan.FromSeconds(30), ct, captureStdout: true);
        return code == 0 && double.TryParse(output.Trim(), NumberStyles.Float,
                                            CultureInfo.InvariantCulture, out var d) ? d : 0;
    }

    private string BuildTargetPath(string artist, string title, string? album, string extension)
    {
        string Clean(string s)
        {
            var c = UnsafeFileChars().Replace(s.Trim(), "").Trim();
            return c.Length == 0 ? "Unbekannt" : c[..Math.Min(c.Length, 100)];
        }

        // Aufbau wie in der uebrigen Bibliothek: Interpret/Album/Titel.
        // Radiomitschnitte gehoeren zu keinem Album, deshalb ein eigener
        // Ordner statt eines erfundenen Albumnamens.
        return Path.Combine(settings.LibraryPath, Clean(artist),
                            Clean(album ?? "Radio-Mitschnitte"),
                            Clean($"{artist} - {title}") + extension);
    }

    private static async Task<(int Code, string Output)> RunAsync(
        string file, IEnumerable<string> args, TimeSpan timeout,
        CancellationToken ct, bool captureStdout = false)
    {
        var psi = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"{file} liess sich nicht starten");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            var stdout = proc.StandardOutput.ReadToEndAsync(cts.Token);
            var stderr = proc.StandardError.ReadToEndAsync(cts.Token);
            await proc.WaitForExitAsync(cts.Token);
            return (proc.ExitCode, captureStdout ? await stdout : await stderr);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(true); } catch { /* war schon fort */ }
            return (-1, "Zeitueberschreitung");
        }
    }
}
