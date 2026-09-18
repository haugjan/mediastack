# 5. Bücher und Hörbücher

Das ist die schwächste Stelle des ganzen Stacks, und zwar nicht wegen der
Konfiguration, sondern weil es hier kein ausgereiftes Werkzeug mehr gibt.

## Der Stand

**Readarr ist eingestellt.** Es war das *arr für Bücher und Hörbücher, die
Entwicklung wurde beendet. Es gibt Community-Forks, aber keinen, der sich als
klarer Nachfolger etabliert hat, und das Metadaten-Backend war immer das
eigentliche Problem: Buchdaten sind viel unsauberer als Film- und Seriendaten,
Ausgaben, Übersetzungen und Hörbuchfassungen desselben Titels lassen sich
schlecht automatisch unterscheiden.

Deshalb steht in diesem Stack **Audiobookshelf** als Server, aber kein
Automatisierungs-*arr. Die Beschaffung bleibt manuell.

## Was eingebaut ist

Audiobookshelf läuft auf `http://<server>:13378` und bedient zwei Bibliotheken:

| Bibliothek | Ordner im Container | Ordner auf dem Host |
|---|---|---|
| Hörbücher | `/audiobooks` | `/mnt/data/media/audiobooks` |
| E-Books | `/books` | `/mnt/data/media/books` |

Audiobookshelf ist für Hörbücher ausgesprochen gut: es merkt sich den
Fortschritt pro Nutzer und Gerät, unterstützt Kapitelmarken, variable
Geschwindigkeit und Schlaftimer, und hat brauchbare eigene Apps für iOS und
Android. Für E-Books ist es solide, aber kein Calibre-Ersatz.

Erwartete Ordnerstruktur für Hörbücher:

```
/mnt/data/media/audiobooks/
└── Sebastian Fitzek/
    └── Der Heimweg/
        ├── Der Heimweg - 01.m4b
        └── cover.jpg
```

`Autor/Titel/Dateien` ist das Schema, das Audiobookshelf ohne Nachhilfe
erkennt. `.m4b` ist das bevorzugte Format, weil Kapitelmarken und Metadaten
darin steckenbleiben.

## Wenn du doch automatisieren willst

Drei Wege, absteigend nach Aufwand-Nutzen-Verhältnis:

**1. Calibre-Web Automated.** Ein überwachter Eingangsordner: alles, was dort
landet, wird konvertiert, mit Metadaten versehen und in die Calibre-Bibliothek
einsortiert. Kein Sucher, aber die Ablage läuft von selbst. Für E-Books der
pragmatischste Weg.

```yaml
# In compose.yaml ergaenzen, falls gewuenscht:
  calibre-web-automated:
    <<: *base
    image: crocodilestick/calibre-web-automated:latest
    container_name: calibre-web-automated
    environment: *env
    volumes:
      - ./config/cwa:/config
      - ${DATA_ROOT}/media/books:/calibre-library
      - ${DATA_ROOT}/torrents/books:/cwa-book-ingest
    ports:
      - 8083:8083
```

**2. LazyLibrarian.** Das älteste noch gepflegte Projekt in dieser Ecke, kann
Usenet und Torrents ansteuern und arbeitet mit Goodreads- und
Google-Books-Metadaten. Funktioniert, fühlt sich aber an wie Software von 2015
und braucht Geduld bei der Einrichtung.

**3. Ein Readarr-Fork.** Technisch die vertrauteste Oberfläche, wenn du die
*arr-Logik schon kennst. Das Risiko ist, dass die Metadaten-Server der Forks
irgendwann verschwinden und du wieder bei null anfängst. Ich würde darauf nicht
bauen.

## Empfehlung

Für Hörbücher: Audiobookshelf reicht völlig, Beschaffung manuell. Hörbücher
kommen ohnehin in Schüben und nicht wöchentlich wie Serienepisoden, der
Automatisierungsgewinn ist klein.

Für E-Books: Calibre-Web Automated dazunehmen, sobald dich das manuelle
Einsortieren nervt. Der Eingangsordner-Ansatz ist der beste
Aufwand-Nutzen-Kompromiss in diesem Bereich.

Lidarr für Musik ist übrigens der gleiche Fall in mild: es funktioniert, aber
MusicBrainz-Matching geht bei Compilations, Live-Alben und Deluxe-Editionen
regelmäßig daneben. Erwarte dort mehr Handarbeit als bei Sonarr und Radarr.
