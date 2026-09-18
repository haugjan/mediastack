# 8. Musik: Spotify-Ersatz mit Navidrome und Lidarr

Drei Bausteine mit klar getrennten Aufgaben:

| Baustein | Aufgabe |
|---|---|
| **Lidarr + Tubifarry** | Wunschzettel und Beschaffung |
| **Navidrome** | Server mit Subsonic-API |
| **Symfonium** (Android) | Client fürs Handy und fürs Auto |

## Navidrome

Läuft bereits nach `sudo ./setup.sh` und liest `/data/media/music`
**nur lesend**, dieselben Dateien, die Lidarr einsortiert und Plex indexiert.
Alle drei stören sich nicht.

Erster Aufruf auf `https://music.example.com`, dort das Admin-Konto anlegen.
Der Scan läuft danach stündlich (`ND_SCANSCHEDULE: 1h`).

Warum Navidrome und nicht einfach Plex: Plex' Musikteil ist der schwächste
Bereich von Plex. Compilations werden auseinandergerissen, Klassik ist ein
Trauerspiel, mehrere Ausgaben eines Albums verschmelzen. Navidrome behandelt
die Tags so, wie sie in den Dateien stehen, und kostet dich rund 50 MB RAM.
Plexamp darf trotzdem parallel auf dieselbe Bibliothek zugreifen.

## Symfonium einrichten (Android, für Android Auto)

Symfonium kostet einmalig rund 5 EUR im Play Store und ist die beste Wahl auf
Android, weil es Navidrome, Plex und Jellyfin gleichzeitig anbinden kann.

1. **Quelle hinzufügen** → Typ `Subsonic`, URL `https://music.example.com`,
   dein Navidrome-Benutzer.
2. **Tailscale muss auf dem Handy laufen.** `music.example.com` löst auf die
   Tailscale-Adresse auf und ist ohne VPN nicht erreichbar. Tailscale läuft
   auf Android als dauerhafter VPN-Dienst, das ist Einrichten und Vergessen.
3. **Android Auto aktivieren**: Einstellungen → Android Auto. Danach
   erscheint Symfonium im Medienmenü des Autos, sobald das Handy per
   Android Auto verbunden ist, kabelgebunden oder drahtlos.
4. **Offline-Sync einrichten**, und das ist der wichtigste Punkt:
   Einstellungen → Downloads → Playlists oder Alben für den Offline-Zugriff
   auswählen.

Zu Punkt 4: Gotthard, jeder zweite Tunnel und die halbe A1 erledigen jede
Streaming-Verbindung. Im Auto ist Offline-Caching die Lösung, nicht ein
stabileres VPN. Lade die Alben, die du wirklich hörst, dauerhaft aufs Gerät.

Unter iOS gibt es Symfonium nicht. Dort sind **Amperfy** (kostenlos, Open
Source, CarPlay) für Navidrome oder **Plexamp** (im Plex Pass enthalten,
CarPlay, sehr poliert) die Kandidaten.

## Transcoding fürs Datenvolumen

Navidrome kann unterwegs herunterrechnen. Unter `Einstellungen → Transcoding`
ein Profil auf Opus 96 oder 128 kbit/s anlegen und in Symfonium für
Mobilfunk auswählen. Im Auto hörst du den Unterschied durch die Werksanlage
nicht, du sparst aber deutlich Datenvolumen. Für heruntergeladene Titel gilt
das nicht, die bleiben im Original.

## Lidarr und Tubifarry

Lidarr läuft in diesem Stack bewusst auf dem **`develop`**-Tag. Der Grund:
Tubifarry installiert sich über `System → Plugins`, und dieses Menü existiert
nur in plugin-fähigen Builds. Der Preis ist ein Entwicklungszweig mit
höherem Regressionsrisiko. Sonarr und Radarr bleiben deshalb auf `latest`.

`ffmpeg`, `nodejs` und `npm` werden per LinuxServer-Mod in den Container
nachinstalliert, Tubifarry braucht alle drei.

### Installation

1. `https://lidarr.example.com` → **System → Plugins**
2. In das GitHub-Feld `https://github.com/TypNull/Tubifarry` eintragen,
   **Install** klicken
3. Lidarr startet neu

Erscheint **System → Plugins** nicht, ist der Container nicht auf dem
`develop`-Tag. Prüfen mit `docker compose images lidarr`.

### YouTube als Quelle

Unter **Settings → Download Clients → Add → Youtube** einrichten.

> **YouTube wehrt sich aktiv.** Das steht so im README von Tubifarry:
> automatisierte Downloader werden erkannt und blockiert. Dagegen braucht es
> Cookie-Authentifizierung aus einer angemeldeten Browsersitzung plus den
> Trusted-Session-Generator.

Vorgehen:

1. **Ein Wegwerf-Google-Konto anlegen.** Nicht dein eigenes. Konten, die für
   automatisierte Downloads verwendet werden, werden auch mal gesperrt, und
   das willst du nicht bei dem Konto, an dem dein Handy hängt.
2. Im Browser die Erweiterung **cookies.txt** installieren, mit dem
   Wegwerf-Konto bei YouTube anmelden, Cookies exportieren.
3. Die Datei nach `config/lidarr/cookies.txt` legen und in Tubifarry den
   Pfad eintragen.
4. Den Trusted-Session-Generator gemäß Tubifarry-Dokumentation aktivieren.
   Dafür ist `nodejs` im Container.

Rechne damit, dass das ein- bis zweimal im Jahr bricht, wenn YouTube etwas
ändert. Das ist keine Fehlkonfiguration, das ist die Natur der Sache.

### Spotify-Playlists importieren

Tubifarry kann Spotify-Playlists lesen und in Lidarr-Wünsche übersetzen.
Unter **Settings → Import Lists → Spotify** die Playlist-URL eintragen.
Damit ist der Umzug von Spotify weg genau ein Arbeitsschritt: deine
bestehenden Playlists werden zur Einkaufsliste.

### Der Wunschzettel

Lidarr selbst ist der Wunschzettel. **Artists → Add New**, Interpret suchen,
Alben auf `monitored` setzen. Den Rest erledigt die Kette. Eine
Overseerr-ähnliche Oberfläche für Musik gibt es nicht in brauchbarer Form,
Lidarr direkt über Tailscale ist der pragmatische Weg.

## Qualität, ehrlich eingeordnet

YouTube liefert Opus mit typisch 128 bis 160 kbit/s. Über eine
Auto-Werksanlage hörst du das nicht. Über anständige Kopfhörer liegt es hörbar
unter CD-Niveau, besonders bei Becken und Ausklängen.

Für Alben, die dir wirklich wichtig sind, lohnt ein Kauf bei **Bandcamp**:
DRM-frei, in FLAC, die Künstler bekommen den Großteil. Heruntergeladenes ZIP
entpacken nach `/mnt/data/media/music/<Interpret>/<Album>/`, Lidarr erkennt
es beim nächsten Scan und übernimmt es in die Bibliothek.

Weitere saubere Quellen, die sich einzubinden lohnen: eigene CD-Rips, das
Live Music Archive im Internet Archive mit bandfreigegebenen Mitschnitten,
sowie Free Music Archive und Jamendo für CC-lizenziertes Material.

Webradio-Mitschnitte über `streamripper` klingen in der Theorie gut, liefern
in der Praxis aber Übergänge, Moderation und abgeschnittene Anfänge. Für eine
Bibliothek ist das unbrauchbar.

## Zur Rechtslage, kurz

In der Schweiz ist die Privatkopie nach Art. 19 URG erlaubt, auch aus
Quellen, die dir nicht gehören. Die Schweiz ist damit deutlich liberaler als
Deutschland, Abmahnwellen wegen privaten Konsums gibt es hier nicht.

Der Download von YouTube verstößt allerdings gegen deren
Nutzungsbedingungen. Das ist eine vertragliche Frage zwischen dir und Google,
keine urheberrechtliche. Praktische Folge: das Konto kann gesperrt werden,
weshalb oben das Wegwerf-Konto steht.

Dies ist keine Rechtsberatung.
