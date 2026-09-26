namespace Aircheckarr.Services;

/// <summary>
/// Hoert einen Sender mit und schneidet an den Titelwechseln.
///
/// Warum das besser ist als streamripper: dort wird blind nach Zeit oder
/// nach Stille geschnitten, und beides geht bei Ueberblendungen schief.
/// Shoutcast- und Icecast-Sender melden den laufenden Titel im Datenstrom
/// selbst (ICY-Metadaten), und dieser Wechsel ist die genaue Schnittmarke.
///
/// Zwei Dinge, die man dabei wissen muss:
///
///   1. Die Meldung kommt oft ein paar Sekunden ZU SPAET, der Titel laeuft
///      dann schon. Deshalb laeuft immer ein Vorlauf-Puffer der letzten
///      Sekunden mit, der beim Aufnahmestart vorangestellt wird. Genau das
///      ist der Grund fuer abgeschnittene Anfaenge bei anderen Werkzeugen.
///   2. Aufgenommen wird nur, was auf der Wunschliste steht. Alles andere
///      laeuft durch den Puffer und wird vergessen, statt die Platte zu
///      fuellen.
/// </summary>
public sealed class IcyStreamListener(ILogger<IcyStreamListener> log)
{
    /// <summary>Ein fertig mitgeschnittener Titel.</summary>
    public sealed record Segment(string Artist, string Title, string TempPath,
                                 DateTime StartedAt, double Seconds);

    /// <summary>
    /// Wird bei jedem Titelwechsel gefragt, ob der neue Titel aufgenommen
    /// werden soll. Rueckgabe null heisst: durchlaufen lassen.
    /// </summary>
    public delegate long? ShouldRecord(string artist, string title);

    private const int PreRollSeconds = 6;

    public async Task ListenAsync(
        Data.Station station, string workDir, int bitrateKbps,
        ShouldRecord shouldRecord,
        Func<Segment, long, Task> onSegment,
        Action<string, string>? onTitle,
        CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.Add("Icy-MetaData", "1");
        http.DefaultRequestHeaders.Add("User-Agent", "aircheckarr/1.0");

        using var response = await http.GetAsync(station.Url,
            HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        if (!TryGetMetaInt(response, out var metaInt))
        {
            // Ohne Metadaten laesst sich nicht schneiden. Das ist kein
            // Fehler des Senders, er taugt nur fuer diesen Zweck nicht.
            log.LogInformation("{Sender} sendet keine Titelmeldungen, wird uebersprungen",
                station.Name);
            return;
        }

        var bytesPerSecond = Math.Max(bitrateKbps, 64) * 1000 / 8;
        var preRollLimit = bytesPerSecond * PreRollSeconds;

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var audio = new byte[metaInt];

        // Vorlauf-Puffer: die letzten Sekunden Ton, als Kette von Stuecken.
        var preRoll = new Queue<byte[]>();
        var preRollBytes = 0;

        string? currentArtist = null, currentTitle = null;
        // Der erste gemeldete Titel laeuft beim Verbinden immer schon. Ihn
        // mitzuschneiden ergaebe ein Bruchstueck ab der Mitte, und genau
        // solche Torsi sind der Grund, warum Radiomitschnitte einen
        // schlechten Ruf haben. Also erst ab dem naechsten Wechsel.
        var firstTitleSeen = false;
        FileStream? recording = null;
        long? recordingWishId = null;
        var recordedBytes = 0L;
        var startedAt = DateTime.UtcNow;
        string? tempPath = null;

        async Task FinishAsync()
        {
            if (recording is null) return;
            await recording.FlushAsync(ct);
            await recording.DisposeAsync();
            recording = null;

            var seconds = (double)recordedBytes / bytesPerSecond;
            var seg = new Segment(currentArtist ?? "", currentTitle ?? "",
                                  tempPath!, startedAt, seconds);
            await onSegment(seg, recordingWishId!.Value);
            recordingWishId = null;
            tempPath = null;
            recordedBytes = 0;
        }

        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (!await ReadExactlyAsync(stream, audio, metaInt, ct)) break;

                if (recording is not null)
                {
                    await recording.WriteAsync(audio.AsMemory(0, metaInt), ct);
                    recordedBytes += metaInt;
                }
                else
                {
                    var copy = audio[..metaInt];
                    preRoll.Enqueue(copy);
                    preRollBytes += copy.Length;
                    while (preRollBytes > preRollLimit && preRoll.Count > 1)
                        preRollBytes -= preRoll.Dequeue().Length;
                }

                var title = await ReadMetadataAsync(stream, ct);
                if (title is null) continue;                 // keine Aenderung gemeldet

                var split = TitleMatcher.SplitStreamTitle(title);
                if (split is null) continue;                 // Jingle, Werbung, Senderkennung

                var (artist, name) = split.Value;
                if (artist == currentArtist && name == currentTitle) continue;

                await FinishAsync();                          // vorherigen Titel abschliessen

                currentArtist = artist;
                currentTitle = name;
                onTitle?.Invoke(artist, name);

                if (!firstTitleSeen)
                {
                    firstTitleSeen = true;
                    log.LogDebug("{Sender} spielt gerade {Interpret} - {Titel}, wird uebersprungen",
                        station.Name, artist, name);
                    continue;
                }

                recordingWishId = shouldRecord(artist, name);
                if (recordingWishId is null) continue;

                Directory.CreateDirectory(workDir);
                // Rohmitschnitt im Codec des Senders. Der Zielcontainer
                // wird erst in der Nachbearbeitung gewaehlt.
                tempPath = Path.Combine(workDir,
                    $"{station.Id}-{DateTime.UtcNow:yyyyMMddHHmmss}.{station.MeasuredCodec ?? "mp3"}");
                recording = File.Create(tempPath);
                startedAt = DateTime.UtcNow;
                recordedBytes = 0;

                // Der Vorlauf ist der Grund, warum der Anfang nicht fehlt.
                foreach (var chunk in preRoll)
                {
                    await recording.WriteAsync(chunk, ct);
                    recordedBytes += chunk.Length;
                }
                log.LogInformation("Mitschnitt gestartet: {Interpret} - {Titel} auf {Sender}",
                    artist, name, station.Name);
            }
        }
        finally
        {
            // Ein laufender Mitschnitt beim Abbruch ist unvollstaendig und
            // wird verworfen, nicht halb in die Bibliothek gelegt.
            if (recording is not null)
            {
                await recording.DisposeAsync();
                if (tempPath is not null && File.Exists(tempPath)) File.Delete(tempPath);
            }
        }
    }

    private static bool TryGetMetaInt(HttpResponseMessage response, out int metaInt)
    {
        metaInt = 0;
        if (!response.Headers.TryGetValues("icy-metaint", out var values)
            && !response.Content.Headers.TryGetValues("icy-metaint", out values))
            return false;
        return int.TryParse(values.FirstOrDefault(), out metaInt) && metaInt > 0;
    }

    /// <summary>
    /// Liest den Metadatenblock. Erstes Byte ist die Laenge in
    /// Sechzehnerschritten, 0 heisst "nichts Neues" und ist der Normalfall.
    /// </summary>
    private static async Task<string?> ReadMetadataAsync(Stream stream, CancellationToken ct)
    {
        var lengthByte = new byte[1];
        if (!await ReadExactlyAsync(stream, lengthByte, 1, ct)) return null;
        var length = lengthByte[0] * 16;
        if (length == 0) return null;

        var buffer = new byte[length];
        if (!await ReadExactlyAsync(stream, buffer, length, ct)) return null;

        var text = System.Text.Encoding.UTF8.GetString(buffer).TrimEnd('\0').Trim();
        const string marker = "StreamTitle='";
        var start = text.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0) return null;
        start += marker.Length;
        var end = text.IndexOf("';", start, StringComparison.Ordinal);
        if (end < 0) end = text.LastIndexOf('\'');
        return end > start ? text[start..end] : null;
    }

    private static async Task<bool> ReadExactlyAsync(Stream stream, byte[] buffer,
                                                     int count, CancellationToken ct)
    {
        var read = 0;
        while (read < count)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read, count - read), ct);
            if (n == 0) return false;   // Sender hat aufgelegt
            read += n;
        }
        return true;
    }
}
