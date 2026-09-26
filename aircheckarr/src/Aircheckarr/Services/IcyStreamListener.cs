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
    /// <summary>
    /// Ein fertig mitgeschnittener Titel samt Vor- und Nachlauf. Die beiden
    /// Marken sagen, an welcher Stelle der Datei die Titelmeldungen kamen;
    /// daran richtet die Nachbearbeitung den Schnitt aus. In Bytes statt
    /// Sekunden, weil die gemessene Bitrate nur ungefaehr stimmt; umgerechnet
    /// wird erst an der wirklichen Laenge der Datei.
    /// </summary>
    public sealed record Segment(string Artist, string Title, string TempPath,
                                 DateTime StartedAt, long StartMarkBytes,
                                 long EndMarkBytes, long TotalBytes);

    /// <summary>
    /// Wird bei jedem Titelwechsel gefragt, ob der neue Titel aufgenommen
    /// werden soll. Rueckgabe null heisst: durchlaufen lassen.
    /// </summary>
    public delegate long? ShouldRecord(string artist, string title);

    // Die Titelmeldung kommt je nach Sender bis zu zwanzig Sekunden zu spaet
    // (Encoder-Puffer, Automation meldet erst beim Einblenden). Mit wenigen
    // Sekunden Vorlauf fehlt dann der halbe erste Refrain, und das ist
    // nachtraeglich nicht zu retten. Zu viel Ton dagegen schneidet die
    // Nachbearbeitung einfach weg. Bei 320 kbit/s kosten 45 Sekunden rund
    // 1,8 MB Speicher je Sender.
    private const int PreRollSeconds = 45;

    // Aus demselben Grund laeuft die Aufnahme nach der naechsten Meldung noch
    // weiter: der Titel spielt ja genauso verspaetet zu Ende.
    private const int PostRollSeconds = 20;

    /// <summary>Eine laufende Aufnahme. Mehrere koennen sich ueberlappen.</summary>
    private sealed class Take
    {
        public required FileStream File { get; init; }
        public required string TempPath { get; init; }
        public required long WishId { get; init; }
        public required string Artist { get; init; }
        public required string Title { get; init; }
        public required DateTime StartedAt { get; init; }
        public required long StartMarkBytes { get; init; }
        public long Bytes { get; set; }
        public long? EndMarkBytes { get; set; }
        public long? StopAtBytes { get; set; }
    }

    /// <returns>
    /// false, wenn der Sender gar keine Titelmeldungen anbietet. Dann taugt
    /// er fuer diesen Zweck nicht, und die Aufnahmeleitung merkt ihn sich.
    /// </returns>
    public async Task<bool> ListenAsync(
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
            log.LogInformation("{Sender} sendet keine Titelmeldungen", station.Name);
            return false;
        }

        var bytesPerSecond = Math.Max(bitrateKbps, 64) * 1000 / 8;
        var preRollLimit = bytesPerSecond * PreRollSeconds;
        var postRollBytes = (long)bytesPerSecond * PostRollSeconds;

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var audio = new byte[metaInt];

        // Vorlauf-Puffer: die letzten Sekunden Ton, als Kette von Stuecken.
        // Er laeuft auch waehrend einer Aufnahme weiter, denn der naechste
        // Titel braucht seinen eigenen.
        var preRoll = new Queue<byte[]>();
        var preRollBytes = 0;
        var takes = new List<Take>();

        string? currentArtist = null, currentTitle = null;
        // Der erste gemeldete Titel laeuft beim Verbinden immer schon. Ihn
        // mitzuschneiden ergaebe ein Bruchstueck ab der Mitte, und genau
        // solche Torsi sind der Grund, warum Radiomitschnitte einen
        // schlechten Ruf haben. Also erst ab dem naechsten Wechsel.
        var firstTitleSeen = false;

        async Task FinishAsync(Take t)
        {
            takes.Remove(t);
            await t.File.FlushAsync(CancellationToken.None);
            await t.File.DisposeAsync();
            await onSegment(new Segment(t.Artist, t.Title, t.TempPath, t.StartedAt,
                                        t.StartMarkBytes, t.EndMarkBytes ?? t.Bytes, t.Bytes),
                            t.WishId);
        }

        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (!await ReadExactlyAsync(stream, audio, metaInt, ct)) break;

                var chunk = audio[..metaInt];
                preRoll.Enqueue(chunk);
                preRollBytes += chunk.Length;
                while (preRollBytes > preRollLimit && preRoll.Count > 1)
                    preRollBytes -= preRoll.Dequeue().Length;

                foreach (var t in takes)
                {
                    await t.File.WriteAsync(chunk, ct);
                    t.Bytes += chunk.Length;
                }
                foreach (var t in takes.Where(t => t.Bytes >= t.StopAtBytes).ToList())
                    await FinishAsync(t);

                var title = await ReadMetadataAsync(stream, ct);
                if (title is null) continue;                 // keine Aenderung gemeldet

                var split = TitleMatcher.SplitStreamTitle(title);
                if (split is null) continue;                 // Jingle, Werbung, Senderkennung

                var (artist, name) = split.Value;
                if (artist == currentArtist && name == currentTitle) continue;

                // Laufende Aufnahmen: hier endet der Titel laut Meldung. Sie
                // laufen noch um den Nachlauf weiter und werden dann fertig.
                foreach (var t in takes.Where(t => t.EndMarkBytes is null))
                {
                    t.EndMarkBytes = t.Bytes;
                    t.StopAtBytes = t.Bytes + postRollBytes;
                }

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

                var wishId = shouldRecord(artist, name);
                if (wishId is null) continue;

                Directory.CreateDirectory(workDir);
                // Rohmitschnitt im Codec des Senders. Der Zielcontainer
                // wird erst in der Nachbearbeitung gewaehlt.
                var tempPath = Path.Combine(workDir,
                    $"{station.Id}-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}.{station.MeasuredCodec ?? "mp3"}");
                var take = new Take
                {
                    File = File.Create(tempPath),
                    TempPath = tempPath,
                    WishId = wishId.Value,
                    Artist = artist,
                    Title = name,
                    StartedAt = DateTime.UtcNow,
                    StartMarkBytes = preRollBytes,
                };
                // Der Vorlauf ist der Grund, warum der Anfang nicht fehlt.
                foreach (var piece in preRoll)
                {
                    await take.File.WriteAsync(piece, ct);
                    take.Bytes += piece.Length;
                }
                takes.Add(take);
                log.LogInformation("Mitschnitt gestartet: {Interpret} - {Titel} auf {Sender}",
                    artist, name, station.Name);
            }

            // Der Sender hat aufgelegt. Was schon zu Ende gemeldet war und nur
            // noch im Nachlauf steckte, ist vollstaendig und wird abgelegt.
            foreach (var t in takes.Where(t => t.EndMarkBytes is not null).ToList())
                await FinishAsync(t);
        }
        finally
        {
            // Alles andere ist unvollstaendig und wird verworfen, nicht halb
            // in die Bibliothek gelegt.
            foreach (var t in takes)
            {
                await t.File.DisposeAsync();
                if (File.Exists(t.TempPath)) File.Delete(t.TempPath);
            }
        }
        return true;
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
