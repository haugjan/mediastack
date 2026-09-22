# Aircheckarr

Schneidet gewünschte Titel aus Webradios mit. Du trägst ein, was dir fehlt,
und sobald es irgendwo läuft, liegt es danach getaggt in der Bibliothek.

Ein „Aircheck" ist im Rundfunk der Mitschnitt einer Sendung. Genau das macht
das Programm, nur gezielt: es hört mehrere Sender gleichzeitig mit und nimmt
ausschließlich auf, was auf der Wunschliste steht.

## Warum nicht streamripper

`streamripper` schneidet blind nach Zeit oder nach Stille. Bei überblendeten
Übergängen geht beides schief, und deshalb hat das Verfahren zu Recht einen
schlechten Ruf: abgeschnittene Anfänge, Moderation im Lied, Torsi in der
Bibliothek.

Aircheckarr macht drei Dinge anders:

**Geschnitten wird an den ICY-Metadaten.** Shoutcast- und Icecast-Sender
melden den laufenden Titel im Datenstrom selbst. Dieser Wechsel ist die
genaue Schnittmarke, kein geschätzter Zeitpunkt.

**Ein Vorlauf-Puffer fängt die Verzögerung ab.** Die Titelmeldung kommt oft
ein paar Sekunden zu spät, der Titel läuft dann schon. Deshalb laufen immer
die letzten sechs Sekunden Ton mit und werden beim Aufnahmestart
vorangestellt.

**Der erste Titel nach dem Verbinden wird übersprungen.** Er läuft bereits,
ein Mitschnitt wäre ein Bruchstück ab der Mitte.

Dazu kommen Längenfilter (kürzer als 70 Sekunden ist ein Jingle, länger als
15 Minuten eine Sendung) und ein Schnitt an der Stille an beiden Rändern.

## Qualität

Die Bitrate im Senderkatalog ist Selbstauskunft und oft grober Unfug — dort
stehen 64000 kbit/s und Videostreams. Aircheckarr misst deshalb jeden Sender
mit `ffprobe` nach und verwendet nur die gemessenen Werte. Was unter der
eingestellten Grenze liegt, wird gar nicht erst mitgehört.

Geschnitten wird **verlustfrei**: ein erster Durchlauf sucht die Stille, der
zweite schneidet mit `-c copy` genau dort. Ein Radiostream ist schon
verlustbehaftet, ihn zum Schneiden erneut zu kodieren kostete ein zweites Mal
Qualität.

Der Zielcontainer hängt am Codec: MP3 bleibt MP3, AAC wandert nach M4A.
Rohes AAC hat keinen Platz für Tags, die Datei läge sonst als „unbekannter
Interpret" in der Bibliothek.

## Wünsche

Zwei Quellen:

- **Von Hand** in der Oberfläche: Interpret, Titel, optional Album.
- **Aus Lidarr**: der Knopf holt die fehlenden Alben über Lidarrs API und
  löst sie in einzelne Titel auf. Lidarr denkt in Alben, ein Radiomitschnitt
  ist ein einzelner Titel — diese Lücke schließt der Import.

Verglichen wird nicht auf Gleichheit. Sender schreiben denselben Titel auf
ein Dutzend Arten, deshalb wird normalisiert (Kleinschreibung, Akzente,
Klammerzusätze, `feat.`, Satzzeichen) und dann mit Levenshtein auf
Ähnlichkeit geprüft, in beiden Leserichtungen.

## Einstellungen

Alles über Umgebungsvariablen, wie im übrigen Stack.

| Variable | Vorgabe | Bedeutung |
|---|---|---|
| `AIRCHECKARR_LIBRARY` | `/data/media/music` | wohin fertige Mitschnitte kommen |
| `AIRCHECKARR_WORK` | `/data/work` | Arbeitsverzeichnis für laufende Aufnahmen |
| `AIRCHECKARR_DB` | `/config/aircheckarr.db` | Datenbank |
| `AIRCHECKARR_MIN_BITRATE` | `128` | Mindestbitrate, **gemessen** |
| `AIRCHECKARR_CODECS` | `mp3,aac` | zugelassene Codecs |
| `AIRCHECKARR_MAX_STATIONS` | `12` | gleichzeitig mitgehörte Sender |
| `AIRCHECKARR_MATCH_THRESHOLD` | `0.86` | ab welcher Ähnlichkeit ein Treffer gilt |
| `AIRCHECKARR_MIN_SECONDS` | `70` | kürzer ist ein Jingle |
| `AIRCHECKARR_MAX_SECONDS` | `900` | länger ist eine Sendung |
| `LIDARR_URL`, `LIDARR_API_KEY` | — | für den Wunschimport und den Suchlauf |

## Aufbau

```
Program.cs                  Schnittstelle und Verdrahtung
Settings.cs                 Umgebungsvariablen
Data/Models.cs              Sender, Wunsch, Mitschnitt
Data/Database.cs            SQLite, handgeschriebenes SQL
Services/RadioBrowserClient Senderkatalog von radio-browser.info
Services/QualityProbe       misst Codec und Bitrate mit ffprobe nach
Services/IcyStreamListener  hört mit, schneidet an den Titelwechseln
Services/TitleMatcher       Normalisieren und Ähnlichkeit
Services/PostProcessor      Ränder schneiden, Tags, einsortieren
Services/LidarrClient       Wünsche holen, Suchlauf anstoßen
Services/RecordingCoordinator  hält den Betrieb am Laufen
```

Einzige Fremdabhängigkeit ist `Microsoft.Data.Sqlite`. Messung, Schnitt und
Tags macht `ffmpeg`, das ohnehin im Container liegt.

## Selbst bauen und laufen lassen

```bash
docker build -t aircheckarr .
docker run --rm -p 8099:8099 \
  -e AIRCHECKARR_DB=/tmp/a.db -e AIRCHECKARR_WORK=/tmp/work \
  -e AIRCHECKARR_LIBRARY=/tmp/lib aircheckarr
```

Im Stack übernimmt das `setup.sh`, sobald das Profil `radio` aktiv ist.

## Zur Rechtslage

In der Schweiz ist die Privatkopie nach Art. 19 URG erlaubt, auch aus
Quellen, die einem nicht gehören. Der Mitschnitt einer frei empfangbaren
Radiosendung für den eigenen Gebrauch fällt darunter — anders als der
Download von YouTube ist hier keine Nutzungsbedingung im Weg, die man
verletzen könnte. Weitergeben darf man die Aufnahmen trotzdem nicht.

Dies ist keine Rechtsberatung.
