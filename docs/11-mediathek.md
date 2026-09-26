# 11. Mediatheken als Quelle mit Mediathekarr

ARD, ZDF, SRF, ORF, 3Sat, arte und die Dritten stellen enorme Mengen
kostenlos bereit — Filme, Serien, Dokumentationen, Tatort. Auf keinem
Tracker liegt das so vollständig, und rechtlich ist es die sauberste Quelle,
die dieser Stack hat.

Mediathekarr macht daraus eine **Suchquelle**, keinen zweiten Wunschzettel.
Du wünschst weiter in Overseerr, Sonarr und Radarr suchen wie immer, und die
Mediathek ist einfach ein Indexer neben den Trackern.

## Wie es zusammenhängt

```
Overseerr ──► Sonarr / Radarr ──► Prowlarr ──► Mediathekarr ──► MediathekViewWeb
                     │                              │
                     │ legt .nzb ab                 │ lädt vom Sender
                     ▼                              ▼
          /data/mediathek/blackhole/ ──────► /data/mediathek/complete/
                     │
                     └──► Import per Hardlink in die Bibliothek
```

Zwei Hälften, beide von `setup.sh` eingerichtet:

**Der Indexer.** Mediathekarr meldet sich als Newznab-Quelle an — dieselbe
Schnittstelle, die auch Usenet-Suchdienste benutzen. Prowlarr verteilt sie an
Sonarr und Radarr.

**Der Abholer.** Greift eine App zu, legt sie eine `.nzb` in einen Ordner.
Mediathekarr sieht sie dort, lädt die Sendung vom Sender und stellt sie unter
dem Release-Namen fertig daneben. Die App importiert von dort per Hardlink.

Das ist der eingebaute „Usenet Blackhole"-Weg der *arr-Apps. Ein
nachgebauter Download-Client müsste deren komplette API mitspielen und bräche
bei jeder Änderung daran.

## Der Trick mit Staffel und Folge

Die Mediatheken kennen keine Staffeln und Folgen, nur Sendedaten. Sonarr
sucht aber nach `Tatort S2026E12` und erkennt nichts anderes.

Mediathekarr fragt deshalb bei Sonarr nach, **wann** diese Folge lief, sucht
in der Mediathek nach dem Datum und benennt den Treffer dann so, wie Sonarr
ihn erwartet. Dafür braucht es `SONARR_API_KEY`, den `setup.sh` ohnehin
einsammelt. Fehlt er, fällt alles auf Datumsnamen zurück — die passen nur bei
täglichen Sendungen.

Bei Filmen macht Radarr dasselbe Problem andersherum: Es lehnt Releases ohne
Jahreszahl ab. Mediathekarr holt das Jahr über die IMDb-Id aus Radarr.

## Was drin ist und was nicht

| | |
|---|---|
| **Gut** | Tatort und Krimis, Dokumentationen, Kinderprogramm, Reportagen, viele Filme, alles auf Deutsch, oft mit Untertiteln |
| **Schlecht** | Internationale Serien, aktuelle Kinofilme, alles Lizenzierte |
| **Gar nicht** | Was älter als die Verweildauer ist. Die Sender nehmen ihre Beiträge nach Wochen bis Monaten wieder herunter |

Die Qualität liegt je nach Sender bei 480p bis 1080p. Mediathekarr schätzt sie
aus der Bitrate statt pauschal „1080p" darüberzuschreiben — sonst sortieren
die Qualitätsprofile falsch.

Ausgelassen werden Beiträge unter zehn Minuten (Nachrichtenschnipsel, Teaser)
sowie Zweitfassungen mit Audiodeskription, Hörfassung und Gebärdensprache:
Für Sonarr sind die nicht von der Hauptfassung zu unterscheiden, und dann
landet eine Fassung mit eingesprochener Bildbeschreibung in der Bibliothek.

## Priorität

Der Indexer läuft mit Priorität 40 und der Download-Client mit 10, beides
bewusst hinten. Was es bei Usenet oder auf einem Tracker gibt, ist meist
besser aufgelöst und ohne Sendungslogo in der Ecke. Die Mediathek ist der
Rückfall, nicht die erste Wahl — aber ein Rückfall, den niemand sperrt.

## Bedienung

Unter `https://mediathek.example.com` oder `http://<ip>:8098` siehst du, was
abgeholt wurde, und kannst eine Suche ausprobieren: Dort steht genau das, was
Sonarr und Radarr angeboten bekämen. Gesteuert wird der Dienst aus den Apps
heraus, die Oberfläche liest nur mit.

## Wenn nichts gefunden wird

**Serie mit Staffel und Folge:** Prüfe in Sonarr, ob die Folge ein
Sendedatum hat. Ohne Datum kann Mediathekarr nicht suchen.

**Die Sendung ist zu alt.** Verweildauer prüfen: In der Oberfläche nach dem
Titel suchen. Kommt dort nichts, hat der Sender sie heruntergenommen.

**Zu kurz.** Unter zehn Minuten wird ausgelassen. `MEDIATHEKARR_MIN_DURATION`
in der `.env` senkt die Schwelle.
