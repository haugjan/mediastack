# Mediastack

Ein eigener Medienserver: Filme, Serien, Musik, Hörbücher und ein
Dokumentenarchiv. Läuft auf jedem Debian, Ubuntu oder Linux Mint.

## Installation

```bash
curl -fsSL https://github.com/haugjan/mediastack/releases/latest/download/mediastack.tar.gz | tar xz
cd mediastack
sudo ./setup.sh
```

Kein git, kein Konto, keine Anmeldung. Auf einem frisch installierten Debian
fehlt `curl` in der Minimalvariante, dann vorher einmal
`sudo apt install -y curl`.

Das ist alles. Das Skript fragt in einfachem Deutsch nach, installiert was
fehlt, und startet am Ende alles, was laufen kann.

Drei Eigenschaften, die den Unterschied machen:

**Du kannst es jederzeit erneut starten.** Es merkt sich alles in der `.env`,
fragt nur nach, was noch fehlt, und macht nichts doppelt. Hast du später ein
VPN-Abo, startest du es einfach nochmal, gibst den Schlüssel ein, fertig.

**Du brauchst am Anfang gar keine Zugänge.** Plex, Serien, Filme, Musik,
Hörbücher, Wunschliste und Dokumentenarchiv laufen sofort. Nur Torrents
brauchen ein VPN, Usenet ein Abo und schöne Web-Adressen eine Domain. Jedes
davon kannst du mit „nein" überspringen und später nachholen.

**Es kann nicht halb kaputt enden.** Fehlt ein Zugang, wird der betreffende
Container gar nicht gestartet, statt in einer Neustartschleife zu landen. Das
regeln Compose-Profile, die `setup.sh` selbst setzt. Bricht ein optionaler
Schritt ab, läuft der Rest weiter, und am Ende steht eine Liste, was nicht
geklappt hat und wie du es nachholst. Ausführliches steht in `setup.log`.

Am Ende bekommst du eine Liste der Adressen, die du aufrufen kannst, das
Paperless-Passwort und die drei oder vier Handgriffe, die noch im Browser
nötig sind.

### Was genau passiert

| Schritt | Inhalt |
|---|---|
| 1 | System prüfen, Docker und fehlende Pakete nachinstallieren, Ruhezustand abschalten |
| 2 | Speicherort wählen, freien Platz und **Hardlink-Fähigkeit** prüfen |
| 3 | Dienstbenutzer und Ordnerstruktur anlegen |
| 4 | Tailscale für den Zugriff von unterwegs (optional) |
| 5 | Eigene Web-Adressen: Azure-Anmeldung, Zone auswählen, Zugang und DNS-Einträge **automatisch** (optional) |
| 6 | Torrents über ProtonVPN und Usenet (beide optional) |
| 7 | Paperless-Dokumentenarchiv (optional) |
| 8 | Plex mit deinem Konto verbinden |
| 9 | Alles starten, **API-Schlüssel einsammeln**, **die Apps untereinander verkabeln**, VPN und Portweiterleitung gegenprüfen |

Schritt 2 ist der einzige, der hart abbricht. Können auf dem gewählten Pfad
keine Hardlinks angelegt werden, würde jeder fertige Download kopiert statt
verlinkt: doppelter Platzverbrauch, und das Weitergeben von Torrents endet
sofort. NTFS, exFAT und Netzlaufwerke können das nicht.

Schritt 9 erspart dir das Abtippen von sechs API-Schlüsseln. Die Apps legen
sie beim ersten Start selbst an, das Skript liest sie aus deren
Konfigurationsdateien und trägt sie ein.

Mit denselben Schlüsseln verkabelt es die Apps anschließend untereinander:
Download-Clients in Sonarr, Radarr und Lidarr (mit Host `gluetun`, der
häufigsten Fehlerquelle überhaupt), die Kategorien in qBittorrent, Hardlinks
und Umbenennen, Prowlarr an alle drei Apps, die deutschen Qualitätsprofile,
Plex-Bibliotheken samt Transcode-Ziel, das Bazarr-Sprachprofil Deutsch vor
Englisch und Overseerr an Sonarr und Radarr. Was im Browser bleibt, sind vier
Dinge, die niemand skripten kann: die Auswahl deiner Suchquellen in Prowlarr,
die Anmeldung von Overseerr an deinem Plex-Konto, das Admin-Konto in
Navidrome und, falls du Usenet nutzt, die Zugangsdaten deines Anbieters.

## Was drin ist

```
Overseerr  ──► Wünsche eintragen (auch für Familie und Freunde)
                  │
Sonarr / Radarr / Lidarr  ──► was fehlt, welche Qualität
                  │
Prowlarr  ──► Suchquellen
                  ├──► qBittorrent  (im VPN-Tunnel)
                  └──► SABnzbd      (Usenet, direkt)
                          │
                  Unpackerr, Cleanuparr  ──► entpacken, Hänger aufräumen
                          ▼
                  Import per Hardlink
                          │
   ├──► Plex             Filme und Serien, auch von unterwegs
   ├──► Navidrome        Musik, Subsonic-API fürs Handy und Auto
   ├──► Audiobookshelf   Hörbücher und E-Books
   ├──► Bazarr           Untertitel, Deutsch vor Englisch
   └──► Tautulli, Kometa Statistiken, Poster und Sammlungen

Paperless-ngx   Dokumente mit Volltextsuche, OCR auf Deutsch
Homepage        Startseite mit Live-Status
Uptime Kuma     läuft alles noch
```

Deutschsprachige Inhalte werden bevorzugt, Englisch ist der Rückfall. Das
macht Recyclarr mit den deutschen TRaSH-Profilen, die Feineinstellung steht
in [docs/03](docs/03-deutsche-profile.md).

## Zugriff von unterwegs

Der Schnitt ist nicht drinnen gegen draußen, sondern **wer** zugreift.

| Wer | Was | Wie |
|---|---|---|
| Familie und Freunde | Wünsche eintragen, Filme schauen | Overseerr öffentlich, Plex über seine eigenen Server |
| nur du | alles andere | Tailscale, ohne offenen Port am Router |

Mit einer Domain bei Azure DNS bekommt jeder Dienst eine eigene Adresse mit
echtem Zertifikat, auch die, die aus dem Internet gar nicht erreichbar sind.
Das geht, weil Caddy die DNS-01-Challenge nutzt und dafür nichts erreichbar
sein muss.

Die privaten Adressen sind auf zwei Ebenen dicht: Caddy bindet sie per `bind`
an die Tailscale-Adresse, am öffentlichen Interface existiert für sie also gar
kein Listener. Dazu kommt eine `remote_ip`-Prüfung. Ein DNS-Eintrag allein
hätte nicht genügt, weil jemand mit Kenntnis deiner Heim-IP sonst einfach
`Host: paperless.deine-domain.ch` an Port 443 schicken könnte.

Ohne Domain erreichst du alles über `http://<ip>:<port>`. Funktioniert
genauso, sieht nur weniger schön aus.

Vom Handy erreichst du das alles über die Tailscale-App, ohne dass ein Port
offen ist. Wie das eingerichtet wird, welche Apps sich lohnen und der eine
DNS-Fallstrick dabei: [docs/09](docs/09-handy-und-unterwegs.md).

Deine Domain steht **nirgends in diesem Repo**, sondern ausschließlich in
deiner lokalen `.env` unter `BASE_DOMAIN`, und die ist von Git ausgeschlossen.
`setup.sh` fragt in Schritt 5 danach. In der Dokumentation steht überall
`example.com` als Platzhalter.

## Dokumente und OneDrive

Zwei Einbahnstraßen statt eines Abgleichs:

```
OneDrive:/Scans ──► consume/ ──► Paperless ──► media/  (bleibt lokal)
                                                  │
                          export/ ──► OneDrive (Klartext + verschlüsselt)
```

**Paperless' `media/` wird niemals synchronisiert.** Paperless besitzt diese
Pfade und die Datenbank verweist darauf. Ein Zwei-Wege-Sync bringt
Konfliktkopien, halb hochgeladene Dateien und propagierte Löschungen ins
Archiv. Einrichtung in [docs/06](docs/06-paperless-onedrive.md).

## Die drei häufigsten Stolperfallen

**1. qBittorrent heißt `gluetun`.** In Sonarr und Radarr ist der
Download-Client-Host `gluetun`, nicht `qbittorrent`. Der Container hat kein
eigenes Netz, er lebt im Netz von Gluetun.

**2. Ein Speicherort, nicht zwei.** Downloads und Bibliothek müssen auf
derselben Festplatte liegen, sonst wird kopiert statt verlinkt. `setup.sh`
testet das und bricht ab.

**3. Docker-Ports umgehen ufw.** Veröffentlichte Container-Ports landen in der
`DOCKER-USER`-Kette, die vor ufw greift. Was schützt, sind der Router und
Caddys `bind`, nicht die Host-Firewall.

## Warum kein Huntarr

Huntarr war als gedrosselte Nachsuche geplant. Das Repo `plexguide/Huntarr.io`
liefert inzwischen 404. Der Nachfolge-Fork
[elfhosted/newtarr](https://github.com/elfhosted/newtarr) nennt Telemetrie,
obfuskierten Code und Bedenken rund um Zugangsdaten als Grund für die
Abspaltung.

Relevant ist das, weil so ein Tool per Design die API-Schlüssel **aller**
Apps bekommt. Die Funktion selbst ist überschaubar, das macht
[`scripts/backfill.py`](scripts/backfill.py) in rund 200 lesbaren Zeilen ohne
Fremdabhängigkeiten.

## Betrieb

```bash
docker compose ps                        # was laeuft
docker compose logs -f <dienst>          # Protokoll
docker compose run --rm recyclarr sync   # Qualitaetsprofile schreiben
sudo ./setup.sh                          # nachtraeglich erweitern
```

`docker compose up -d` startet immer genau die Bereiche, die in
`COMPOSE_PROFILES` in der `.env` stehen. Das setzt `setup.sh`, du musst nie
mit Profilen hantieren.

Updates der Container kommen als Renovate-Pull-Request. Nach dem Merge:

```bash
git pull && docker compose up -d
```

Den Stack selbst aktualisiert `sudo ./setup.sh` automatisch: bevor es
irgendetwas tut, schaut es nach einem neueren Release, spielt es ein und
startet sich in der neuen Version neu. Ist GitHub nicht erreichbar, läuft es
mit der vorhandenen Version weiter. In einem Git-Checkout wird nichts
überschrieben, dort gilt `git pull`. Nur aktualisieren, ohne Setup:

```bash
./scripts/update.sh
```

Das Skript vergleicht die installierte Version mit dem neuesten Release und bricht ab, wenn schon alles aktuell
ist. Vor dem Überschreiben prüft es das Archiv: `setup.sh` ausführbar,
`compose.yaml` vorhanden, Syntax in Ordnung, und **keine `.env` und kein
`config/` darin**. Deine Daten bleiben dabei nicht durch Ausnahmeregeln
verschont, sondern weil sie im Archiv gar nicht enthalten sind. Die alte
`compose.yaml` wird als `compose.yaml.vor-<version>` gesichert, damit du
`diff` laufen lassen kannst.

## Neue Version veröffentlichen

```bash
git tag v1.1.0 && git push origin v1.1.0
```

Die Pipeline prüft alles durch, packt `mediastack.tar.gz`, testet das Archiv
durch Auspacken und legt das Release samt Prüfsummen an. Geprüft wird unter
anderem, dass qBittorrent im Netz von Gluetun bleibt, dass die optionalen
Dienste ohne Profil nicht starten, und dass Caddy wirklich nur Overseerr
öffentlich ausliefert.

## Aufbau des Repos

```
setup.sh                      der Installer, ein Befehl, mehrfach startbar
.github/workflows/release.yml CI bei jedem Push, Release-Archiv bei jedem Tag
compose.yaml                  27 Dienste, optionale ueber Profile abgesichert
.env.example                  Vorlage, setup.sh fuellt das meiste selbst
renovate.json                 Update-Policy, Postgres-Major bewusst gesperrt
caddy/Dockerfile              Caddy-Build mit dem Azure-DNS-Modul
caddy/Caddyfile               oeffentlich vs. an tailscale0 gebunden
recyclarr/recyclarr.yml       deutsche TRaSH-Profile (Recyclarr v8)
homepage/services.yaml.tmpl   Dashboard-Vorlage, setup.sh setzt die Links
systemd/                      Timer fuer OneDrive und Sicherung
scripts/update.sh             neue Version holen, ohne git und ohne gh
scripts/backfill.py           gedrosselte Nachsuche, Huntarr-Ersatz
scripts/paperless-inbox.sh    OneDrive -> consume/
scripts/paperless-export.sh   export/ -> OneDrive, Klartext und verschluesselt
scripts/backup-config.sh      config/ verschluesselt sichern
docs/01-host-setup.md         Systemdetails, Hardlinks, Grafik, Firewall
docs/02-inbetriebnahme.md     was nach dem Installer im Browser bleibt
docs/03-deutsche-profile.md   Sprachlogik, Delay Profiles, Quellenaufteilung
docs/04-hardware.md           Hardware-Empfehlung, warum kein Raspberry Pi
docs/05-buecher.md            Readarr-Nachfolge, Audiobookshelf
docs/06-paperless-onedrive.md rclone, die zwei Einbahnstrassen, Restore
docs/07-azure-dns.md          was setup.sh bei Azure macht, und wie von Hand
docs/08-musik.md              Navidrome, Client fuers Auto, Tubifarry
docs/09-handy-und-unterwegs.md Zugriff per Tailscale, Apps, DNS-Fallstrick
config/                       Laufzeitzustand, nicht in Git, gehoert ins Backup
```

## Was noch offen ist

- Hardware: ob die CPU Videos in Hardware umrechnen kann, sagt dir `setup.sh`
  in Schritt 1. Falls nicht: [docs/04](docs/04-hardware.md).
- Usenet-Anbieter und Suchdienste sind noch nicht gewählt.
- Kometa braucht eine `config/kometa/config.yml` mit Plex-URL und Token.
- Scrutiny braucht die zu `lsblk` passende `devices:`-Liste in `compose.yaml`.
- Immich für Fotos ist der nächste sinnvolle Baustein, aber noch nicht drin.
