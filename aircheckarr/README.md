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

**Die ICY-Metadaten sind der Anker.** Shoutcast- und Icecast-Sender
melden den laufenden Titel im Datenstrom selbst. Die Meldung kommt aber je
nach Sender bis zu zwanzig Sekunden zu spät (Encoder-Puffer, Automation
meldet erst beim Einblenden). Deshalb wird großzügig aufgenommen: 45
Sekunden Vorlauf vor der Meldung, 20 Sekunden Nachlauf nach der nächsten.

**Geschnitten wird an der Länge, nicht an der Stille.** Formatradio blendet
über, zwischen zwei Titeln gibt es keine Stille. Also steht die Länge des
Titels vorher fest: aus Lidarr (MusicBrainz), oder aus dem Abstand der
beiden Titelmeldungen, die ja gleich stark verspätet sind. Offen ist dann
nur noch, um wie viel die Meldungen zu spät kamen. Gesucht wird die
Verschiebung, bei der **beide** Enden auf einer Senke liegen, gemessen
gegen die zehn Sekunden drumherum. Eine Senke an beiden Enden im richtigen
Abstand ist ein starkes Zeichen, eine leise Strophe mitten im Lied nicht.

**Die Verzögerung wird je Sender gelernt.** Sie ist bei einem Sender
ziemlich fest. Jeder gelungene Schnitt schärft den Wert, und er entscheidet,
wenn der Ton keine klare Senke hergibt. Weicht die Lidarr-Länge um mehr als
15 Sekunden von den Meldungen ab, spielt der Sender eine andere Fassung
(Radio-Edit), und die Meldungen gelten.

**Der erste Titel nach dem Verbinden wird übersprungen.** Er läuft bereits,
ein Mitschnitt wäre ein Bruchstück ab der Mitte.

Dazu kommen Längenfilter: kürzer als 70 Sekunden ist ein Jingle, länger als
15 Minuten eine Sendung.

## Qualität

Die Bitrate im Senderkatalog ist Selbstauskunft und oft grober Unfug — dort
stehen 64000 kbit/s und Videostreams. Aircheckarr misst deshalb jeden Sender
mit `ffprobe` nach und verwendet nur die gemessenen Werte. Was unter der
eingestellten Grenze liegt, wird gar nicht erst mitgehört.

Geschnitten wird **verlustfrei**: ein erster Durchlauf sucht die
Schnittstellen, der zweite schneidet mit `-c copy` genau dort. Ein Radiostream ist schon
verlustbehaftet, ihn zum Schneiden erneut zu kodieren kostete ein zweites Mal
Qualität.

Der Zielcontainer hängt am Codec: MP3 bleibt MP3, AAC wandert nach M4A.
Rohes AAC hat keinen Platz für Tags, die Datei läge sonst als „unbekannter
Interpret" in der Bibliothek.

## Wünsche

Zwei Quellen:

- **Aus Lidarr**, von selbst. Alle 15 Minuten gleicht Aircheckarr Lidarrs
  Liste *Fehlend* ab und löst jedes Album in seine fehlenden Titel auf.
  Wünschen heißt also: in Lidarr ein Album beobachten, wie gewohnt. Bekommt
  Lidarr einen Titel über Torrent oder Usenet schneller, verschwindet der
  Wunsch beim nächsten Abgleich wieder.
- **Von Hand** in der Oberfläche: Interpret, Titel, optional Album. Für
  alles, was Lidarr nicht kennt.

Verglichen wird nicht auf Gleichheit. Sender schreiben denselben Titel auf
ein Dutzend Arten, deshalb wird normalisiert (Kleinschreibung, Akzente,
Klammerzusätze, `feat.`, Satzzeichen) und dann mit Levenshtein auf
Ähnlichkeit geprüft, in beiden Leserichtungen.

## Aircheckarr als Quelle für Lidarr

Ein Mitschnitt zu einem Lidarr-Wunsch landet nicht einfach im
Musikordner. Er geht über Lidarrs **manuellen Import** zurück, zusammen mit
den genauen Nummern von Interpret, Album, Ausgabe und Titel. Lidarr benennt
die Datei um, sortiert sie ein und hakt den Titel ab, genau wie bei einem
Download.

Warum nicht Lidarrs eigene Erkennung: ein einzelner Titel eines Albums
„passt" für sie nie gut genug („Couldn't find similar album") und bliebe
liegen. Aircheckarr weiß aber schon, welcher Titel es ist, und sagt es
Lidarr direkt.

Die eine Voraussetzung: beide Container sehen die Datei unter demselben
Pfad. Im Stack ist das so, beide hängen `/data` gleich ein. Lehnt Lidarr
trotzdem ab, landet der Mitschnitt wie ein Wunsch von Hand direkt in der
Bibliothek, und in der Liste *Mitschnitte* steht der Grund.

## Sender

Welche Sender mithören, bestimmst du. Unter *Sender suchen* lässt sich der
Katalog nach Name, Stilrichtung und Land durchsuchen; beliebig viele
Treffer ankreuzen und gemeinsam übernehmen. In der Senderliste markierst du
mehrere auf einmal (mit gedrückter Umschalttaste einen ganzen Bereich) und
schaltest sie gemeinsam an, aus oder entfernst sie. Abgewählte Sender
werden sofort getrennt.

Mitgehört wird, was ausgewählt ist **und** die Messung besteht. Sind mehr
Sender bereit als `AIRCHECKARR_MAX_STATIONS`, gehen die Plätze nach Eignung:
zuerst Sender mit Treffern, denn dort laufen die Wünsche erfahrungsgemäß,
dann noch ungeprüfte, damit jeder einmal zeigen kann, was er meldet, zuletzt
der Rest nach Bitrate. Ein geeigneterer Sender verdrängt einen schwächeren,
aber nie mitten in einer Aufnahme.

Sender, die nach einer Stunde keine zwei verschiedenen Titel gemeldet haben,
senden nur ihren Namen oder gar nichts. Sie werden für eine Woche
aussortiert („Sendet keine Titel") und machen den Platz frei. Mithören aus
und wieder an gibt ihnen sofort eine neue Chance.

### Mithören und Beobachten

Ausgewählte Sender arbeiten auf zwei Arten, zu sehen unter *Aktivität*:

- **Hört mit:** Der Audiostrom ist offen, bis zu `AIRCHECKARR_MAX_STATIONS`
  Sender gleichzeitig. Nur diese können aufnehmen. Jeder kostet rund um die
  Uhr seine Bitrate, zwölf Sender also etwa 2,5 Mbit/s.
- **Beobachtet:** Alle übrigen ausgewählten Sender, deren Server eine
  Statusseite hat (Icecast, Shoutcast). Alle 15 Sekunden wird nur der
  laufende Titel gelesen, ein paar Kilobyte. Aufnehmen geht so nicht: bis
  der Titel dort erkannt ist, läuft er schon. Aber ein Wunsch auf einem
  beobachteten Sender zählt als Treffer, und der Sender rückt bei der
  Platzvergabe nach vorn. So findet sich mit der Zeit von selbst, wo die
  Wünsche laufen.

Große Sendernetze (Heart, Gold, FFH, alles über streamtheworld) haben keine
erreichbare Statusseite und können nur mitgehört werden. Ob ein Server eine
hat, wird einmal geprüft und nach einem Tag erneut, falls nicht.

### Anhören

Unter *Mitschnitte* und bei erfüllten *Wünschen* spielt ein Knopf den Titel
direkt im Browser ab. Ausgeliefert wird nur, was als Mitschnitt verbucht ist
und in der Musikbibliothek liegt. Hat Lidarr die Datei später umbenannt,
findet der Knopf sie nicht mehr.

Neu ausgewählte Sender werden vor allen anderen gemessen. Nur beim
allerersten Start wählt Aircheckarr selbst die 400 beliebtesten Sender vor,
damit ohne einen Klick etwas passiert.

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
| `AIRCHECKARR_WATCH` | `1` | übrige ausgewählte Sender über ihre Statusseite beobachten, `0` = aus |
| `AIRCHECKARR_WATCH_SECONDS` | `15` | wie oft beobachtete Sender abgefragt werden |
| `AIRCHECKARR_LIDARR_SYNC` | `15` | Abgleich mit Lidarr alle so viele Minuten, `0` = nur auf Knopfdruck |
| `AIRCHECKARR_LIDARR_ALBUMS` | `250` | höchstens so viele fehlende Alben, die jüngsten zuerst |
| `LIDARR_URL`, `LIDARR_API_KEY` | — | für Wunschliste und Import |

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
Services/LidarrClient       Fehlliste abgleichen, Mitschnitte importieren
Services/RecordingCoordinator  hält den Betrieb am Laufen
wwwroot/                    Oberfläche im Stil von Sonarr, ohne Build-Schritt
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
