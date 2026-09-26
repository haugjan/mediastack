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
/// Durchlauf die Schnittstellen, der zweite schneidet mit "-c copy" genau
/// dort. Das ist verlustfrei und nebenbei schnell.
///
/// Die Falle beim Suchen: Formatradio blendet ueber. Zwischen zwei Titeln
/// gibt es keine Stille, eine Stillesuche findet dort nie etwas. Deshalb
/// steht die Laenge des Titels fest, bevor gesucht wird (aus Lidarr, sonst
/// der Abstand der beiden Titelmeldungen, die ja gleich stark verspaetet
/// sind). Offen ist dann nur noch, um wie viel die Meldungen zu spaet kamen,
/// und gesucht wird die Verschiebung, bei der BEIDE Enden auf einer leisen
/// Stelle liegen. Eine Senke an beiden Enden im richtigen Abstand ist ein
/// starkes Zeichen, eine einzelne leise Stelle im Lied nicht.
/// </summary>
public sealed partial class PostProcessor(Settings settings, ILogger<PostProcessor> log)
{
    [GeneratedRegex(@"[^\w\s.\-()&,']")]
    private static partial Regex UnsafeFileChars();

    /// <summary>Wo im Rohmitschnitt die Titelmeldungen kamen, und was bekannt ist.</summary>
    public sealed record CutHints(long StartMarkBytes, long EndMarkBytes, long TotalBytes,
                                  double? ExpectedSeconds, double? DelayPrior);

    /// <param name="Delay">Um wie viel die Meldung dem Ton hinterherhinkte.</param>
    public sealed record Result(bool Ok, string? Path, double Seconds, string? Reason,
                                double? Delay = null);

    // Lautstaerke in Schritten von 100 ms. Feiner bringt nichts, ein
    // MP3-Rahmen ist ohnehin 26 ms lang, und so bleibt die Suche billig.
    private const double Step = 0.1;
    private const int SampleRate = 8000;

    // Ohne gelernten Wert: die meisten Sender liegen bei ein paar Sekunden.
    private const double DefaultDelay = 4;
    // Frueher als 3 s vor der Meldung beginnt kein Titel, spaeter als 25 s
    // meldet kein Sender, den wir kennen.
    private const double MinDelay = -3, MaxDelay = 25;
    // Weicht die Laenge laut Meldungen um mehr ab, ist die Lidarr-Laenge eine
    // andere Fassung (Album statt Radio-Edit) und taugt nicht als Anker.
    private const double LengthTolerance = 15;
    // Wie stark der gelernte Wert zieht, in dB je Sekunde Abweichung. Er
    // entscheidet, wenn der Ton keine klare Senke hergibt. Mit 0,4 setzte
    // sich im Praxistest ein leiser Zwischenteil eines Dance-Titels gegen
    // den richtigen Uebergang durch und verschob den Schnitt um 8 s.
    private const double PriorWeight = 1.0;

    /// <param name="handoverDir">
    /// Gesetzt, wenn Lidarr die Datei uebernimmt: dann landet sie dort statt
    /// in der Bibliothek, und Lidarr sortiert sie selbst ein.
    /// </param>
    public async Task<Result> FinalizeAsync(string tempPath, string artist, string title,
                                            string? album, string? handoverDir, CutHints hints,
                                            CancellationToken ct)
    {
        // Die Laenge kommt aus dem dekodierten Ton, nicht aus ffprobe: bei
        // rohem MP3 und AAC schaetzt ffprobe sie nur aus der Bitrate.
        var loudness = await LoudnessAsync(tempPath, ct);
        if (loudness.Length == 0)
            return new Result(false, null, 0, "Datei liess sich nicht lesen");
        var duration = loudness.Length * Step;

        var total = Math.Max(1, hints.TotalBytes);
        var startMark = duration * hints.StartMarkBytes / total;
        var endMark = duration * hints.EndMarkBytes / total;
        var announced = endMark - startMark;

        if (announced < settings.MinTrackSeconds)
            return new Result(false, null, announced,
                $"zu kurz ({announced:F0}s), vermutlich Jingle oder Werbung");
        if (announced > settings.MaxTrackSeconds)
            return new Result(false, null, announced,
                $"zu lang ({announced:F0}s), vermutlich eine Sendung statt eines Titels");

        var (start, end, delay, anchor) = FindCut(loudness, duration, startMark, announced, hints);
        var trimmed = end - start;
        if (trimmed < settings.MinTrackSeconds)
            return new Result(false, null, trimmed, "nach dem Beschneiden zu kurz");
        log.LogInformation(
            "Schnitt {Interpret} - {Titel}: {Start:F1}s bis {Ende:F1}s, Meldung {Verzug:F1}s zu spaet, Laenge aus {Anker}",
            artist, title, start, end, delay, anchor);

        // Der Zielcontainer haengt am Codec, und daran haengen die Tags:
        // rohes AAC (ADTS) hat gar keinen Platz fuer Interpret und Titel,
        // ffmpeg verwirft -metadata dort stillschweigend. Die Datei laege
        // dann als "unbekannter Interpret" in der Bibliothek. MP4 kann es,
        // und das Umpacken ist verlustfrei.
        var codec = Path.GetExtension(tempPath).TrimStart('.').ToLowerInvariant();
        var extension = codec == "mp3" ? ".mp3" : ".m4a";

        var target = handoverDir is null
            ? LibraryPathFor(artist, title, album, extension)
            : Path.Combine(handoverDir, Clean($"{artist} - {title}") + extension);
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
        return new Result(true, target, trimmed, null, delay);
    }

    /// <summary>
    /// Sucht die Verschiebung der Titelmeldung gegen den Ton. Bewertet wird
    /// jede Verschiebung danach, wie leise es an beiden Enden ist, plus ein
    /// Aufschlag fuer den Abstand zum bisher gelernten Wert des Senders.
    /// </summary>
    private static (double Start, double End, double Delay, string Anchor) FindCut(
        double[] loudness, double duration, double startMark, double announced, CutHints hints)
    {
        var length = announced;
        var anchor = "den Titelmeldungen";
        if (hints.ExpectedSeconds is { } expected
            && Math.Abs(expected - announced) <= LengthTolerance)
        {
            length = expected;
            anchor = "Lidarr";
        }

        var prior = hints.DelayPrior ?? DefaultDelay;
        double? best = null;
        var bestScore = double.MaxValue;
        for (var d = MinDelay; d <= MaxDelay; d += Step)
        {
            var s = startMark - d;
            var e = s + length;
            if (s < 0 || e > duration) continue;
            var score = Dip(loudness, s) + Dip(loudness, e) + PriorWeight * Math.Abs(d - prior);
            if (score >= bestScore) continue;
            bestScore = score;
            best = d;
        }

        // Passt gar keine Verschiebung in die Datei (Sender hat frueh
        // aufgelegt), bleibt der gelernte Wert, gekappt an den Dateigrenzen.
        var delay = best ?? prior;
        var start = Math.Clamp(startMark - delay, 0, duration);
        var end = Math.Clamp(start + length, start, duration);
        return (start, end, delay, anchor);
    }

    /// <summary>
    /// Wie tief die leiseste Stelle in der Sekunde um den Zeitpunkt unter
    /// dem Pegel der zehn Sekunden drumherum liegt, in dB (negativ = Senke).
    /// Relativ statt absolut: sonst gewinnt in einem ruhigen Lied jede leise
    /// Strophe gegen den Uebergang, der dort nur ein paar dB tiefer liegt.
    /// </summary>
    private static double Dip(double[] loudness, double at)
    {
        var from = Math.Max(0, (int)((at - 0.5) / Step));
        var to = Math.Min(loudness.Length - 1, (int)((at + 0.5) / Step));
        var min = 0.0;
        for (var i = from; i <= to; i++) min = Math.Min(min, loudness[i]);

        var wFrom = Math.Max(0, (int)((at - 5) / Step));
        var wTo = Math.Min(loudness.Length - 1, (int)((at + 5) / Step));
        var around = loudness[wFrom..(wTo + 1)];
        Array.Sort(around);
        var median = around.Length > 0 ? around[around.Length / 2] : 0;
        return min - median;
    }

    /// <summary>
    /// Lautstaerke des Mitschnitts in dB je 100 ms. Dekodiert wird nur zum
    /// Messen, einkanalig mit 8 kHz: fuer Lautstaerke reicht das, und fuer
    /// fuenf Minuten sind es knapp fuenf Megabyte.
    /// </summary>
    private static async Task<double[]> LoudnessAsync(string path, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("ffmpeg")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in (string[])["-v", "error", "-i", path, "-ac", "1",
                                     "-ar", SampleRate.ToString(CultureInfo.InvariantCulture),
                                     "-f", "s16le", "-"])
            psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("ffmpeg liess sich nicht starten");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromMinutes(2));

        var pcm = new MemoryStream();
        var stderr = proc.StandardError.ReadToEndAsync(cts.Token);
        try
        {
            await proc.StandardOutput.BaseStream.CopyToAsync(pcm, cts.Token);
            await proc.WaitForExitAsync(cts.Token);
            await stderr;
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(true); } catch { /* war schon fort */ }
            return [];
        }

        var samples = pcm.GetBuffer().AsSpan(0, (int)pcm.Length);
        var perStep = (int)(SampleRate * Step);
        var steps = samples.Length / 2 / perStep;
        var result = new double[steps];
        for (var i = 0; i < steps; i++)
        {
            double sum = 0;
            for (var k = 0; k < perStep; k++)
            {
                var off = (i * perStep + k) * 2;
                double v = (short)(samples[off] | (samples[off + 1] << 8));
                sum += v * v;
            }
            var rms = Math.Sqrt(sum / perStep) / 32768.0;
            result[i] = rms > 0 ? Math.Max(-90, 20 * Math.Log10(rms)) : -90;
        }
        return result;
    }

    private static string Clean(string s)
    {
        var c = UnsafeFileChars().Replace(s.Trim(), "").Trim();
        return c.Length == 0 ? "Unbekannt" : c[..Math.Min(c.Length, 100)];
    }

    /// <summary>Ablage ohne Lidarr, und Rueckfall, wenn Lidarr ablehnt.</summary>
    public string LibraryPathFor(string artist, string title, string? album, string extension)
    {
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
