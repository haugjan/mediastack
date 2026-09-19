# 2. Inbetriebnahme

`setup.sh` nimmt dir den mechanischen Teil ab, inklusive des Einsammelns
aller API-Keys. Was bleibt, ist die Konfiguration in den Weboberflächen, die
sich nicht sinnvoll skripten lässt.

> Die Adressen unten stehen in der Domain-Form (`https://sonarr.example.com`).
> Hast du beim Installer keine Domain eingerichtet, nimm stattdessen
> `http://<ip-des-servers>:<port>`. Die Portnummern stehen in der
> Zusammenfassung, die `setup.sh` am Ende ausgibt, und auf der Startseite.

## Was der Installer selbst macht

```bash
sudo ./setup.sh
```

In Schritt 9 passiert unter anderem das hier: es startet Sonarr,
Radarr, Lidarr, Prowlarr, Bazarr und SABnzbd, wartet, bis sie antworten, und
**liest dann die API-Keys aus deren Konfigurationsdateien aus** (`config.xml`
bei den *arr-Apps, `sabnzbd.ini` bei SABnzbd, `config.yaml` bei Bazarr). Die
Werte landen automatisch in der `.env`, und die abhängigen Container werden
danach mit den frischen Schlüsseln neu gestartet. Das Abtippen von sechs
Schlüsseln entfällt.

Ebenfalls automatisch: der Vergleich der Gluetun-IP mit der Host-IP. Sind
beide gleich, läuft der Torrent-Traffic am Tunnel vorbei, und der Installer
meldet das als FAIL.

## Vier Werte musst du selbst eintragen

Danach fragt `setup.sh` in Schritt 5 bis 8 nach. Von Hand in `.env` geht es auch:

| Variable | Woher |
|---|---|
| `PROTON_WG_PRIVATE_KEY` | Proton → Downloads → WireGuard, dabei **NAT-PMP und P2P aktivieren** |
| `PLEX_CLAIM` | <https://www.plex.tv/claim/>, nur 4 Minuten gültig |
| `ACME_EMAIL` | deine Adresse für Let's Encrypt |
| `AZURE_*` | Service Principal, siehe [docs/07](07-azure-dns.md) |

Alles davon ist optional. Sagst du beim Installer „nein", läuft der Rest
trotzdem, und du holst es später mit einem weiteren `sudo ./setup.sh` nach.

Bei ProtonVPN ist das Häkchen bei NAT-PMP der Punkt, an dem es meistens
schiefgeht. Ohne das bekommt qBittorrent keinen eingehenden Port und seedet
nur zu Peers, die sich von selbst melden.

## qBittorrent

Anmelden mit `QBIT_USER` und `QBIT_PASS` aus der `.env`. Das Passwort setzt
`setup.sh` selbst: es meldet sich einmal mit dem temporären Passwort aus dem
Log an, vergibt ein festes und schaltet dabei **Bypass authentication for
clients on localhost** ein.

Klappt das nicht (etwa weil du vorher schon ein eigenes Passwort gesetzt
hast), von Hand: temporäres Passwort aus dem Log holen,

```bash
docker compose logs qbittorrent | grep -i "temporary password"
```

und die beiden WebUI-Zeilen unten selbst setzen.

Dann `https://qbit.example.com` und unter **Tools → Options**:

| Bereich | Einstellung | Wert |
|---|---|---|
| Downloads | Default Save Path | `/data/torrents` |
| Downloads | Pre-allocate disk space | aus |
| BitTorrent | Torrent Queueing | an, max 5 aktive Downloads |
| WebUI | **Bypass authentication for clients on localhost** | **an** (setzt `setup.sh`) |
| WebUI | Benutzername und Passwort | setzt `setup.sh`, steht als `QBIT_USER` / `QBIT_PASS` in der `.env` |

Der Haken bei **Bypass authentication for clients on localhost** ist nicht
optional. Gluetun schiebt den weitergeleiteten Port per API-Aufruf von
`127.0.0.1` hinein. Ohne den Haken scheitert das an der Anmeldung, und dein
Seeding-Port bleibt zu.

Kategorien anlegen, genau so benannt:

| Kategorie | Save Path |
|---|---|
| `tv` | `/data/torrents/tv` |
| `movies` | `/data/torrents/movies` |
| `music` | `/data/torrents/music` |
| `books` | `/data/torrents/books` |

Danach Port-Forwarding prüfen:

```bash
docker compose restart gluetun qbittorrent
docker compose logs gluetun | grep -i "port forward"
```

Der Wert muss in qBittorrent unter **Options → Connection** als Listening
Port stehen.

## SABnzbd

`https://sab.example.com`, Assistent durchlaufen, Usenet-Zugangsdaten
eintragen. Unter **Config → Folders**:

| Feld | Wert |
|---|---|
| Temporary Download Folder | `/data/usenet/incomplete` |
| Completed Download Folder | `/data/usenet/complete` |

Unter **Config → Categories** die vier Kategorien `tv`, `movies`, `music`,
`books` anlegen, jeweils Folder `/data/usenet/complete/<name>`.

Unter **Config → Switches** lohnt **Direct Unpack**, das entpackt parallel
zum Download statt danach.

## Prowlarr

**Settings → Apps**, je eine Verbindung. Adressen aus Sicht der Container:

| App | Prowlarr Server | App Server |
|---|---|---|
| Sonarr | `http://prowlarr:9696` | `http://sonarr:8989` |
| Radarr | `http://prowlarr:9696` | `http://radarr:7878` |
| Lidarr | `http://prowlarr:9696` | `http://lidarr:8686` |

Die API-Keys stehen schon in der `.env`, du kannst sie von dort kopieren.

Dann **Indexers → Add Indexer**. Aufteilung nach
[docs/03](03-deutsche-profile.md): deutschsprachige Torrent-Quellen mit
Priorität 10, die beiden Usenet-Indexer mit Priorität 25.

Nach dem Speichern werden die Indexer automatisch in alle drei Apps
geschoben.

## Download-Clients in Sonarr, Radarr und Lidarr

Jeweils **Settings → Download Clients → +**.

**qBittorrent**, und hier scheitern die meisten:

| Feld | Wert |
|---|---|
| Host | **`gluetun`** |
| Port | `8080` |
| Username / Password | wie in qBittorrent gesetzt |
| Category | `tv` bzw. `movies` bzw. `music` |

Nicht `qbittorrent` als Host eintragen. Der Container hat kein eigenes Netz,
er lebt im Namespace von Gluetun. Unter dem Namen `qbittorrent` existiert im
Docker-Netz nichts.

**SABnzbd:**

| Feld | Wert |
|---|---|
| Host | `sabnzbd` |
| Port | `8080` (containerintern, nicht 8081) |
| API Key | aus der `.env` |
| Category | `tv` bzw. `movies` bzw. `music` |

## Root-Folder und Medienverwaltung

| App | Root Folder |
|---|---|
| Sonarr | `/data/media/tv` |
| Radarr | `/data/media/movies` |
| Lidarr | `/data/media/music` |

Unter **Settings → Media Management** aktivieren:

- **Use Hardlinks instead of Copy**: an
- **Import Extra Files**: `srt,sub,idx`
- **Rename Episodes / Movies**: an

## Plex

`http://<server>:32400/web`. Bibliotheken:

| Bibliothek | Typ | Ordner | Sprache |
|---|---|---|---|
| Filme | Movies | `/data/media/movies` | Deutsch |
| Serien | TV Shows | `/data/media/tv` | Deutsch |
| Musik | Music | `/data/media/music` | Deutsch |

**Einstellungen → Transcoder:**

- Hardwarebeschleunigte Kodierung verwenden: **an**
- Transcoder temporary directory: `/transcode`

Ohne das Häkchen transcodiert Plex per CPU, obwohl die GPU durchgereicht ist.

Plex-Token für Kometa und Tautulli: bei einem Medium **Get Info → View XML**,
in der URL steht `X-Plex-Token=...`. Wert als `PLEX_TOKEN` in die `.env`.

## Overseerr

`https://requests.example.com`, Anmeldung mit dem Plex-Konto. Dann Sonarr und
Radarr verbinden, dasselbe Muster: `http://sonarr:8989` mit dem API-Key,
Quality Profile `[German] HD Bluray + WEB`, Root Folder `/data/media/tv`.

Den API-Key von Overseerr selbst findest du unter **Settings → General**, er
gehört als `OVERSEERR_API_KEY` in die `.env`, damit das Homepage-Widget
funktioniert.

## Bazarr

**Settings → Sonarr** und **→ Radarr** mit den Container-Adressen verbinden.
Unter **Settings → Languages** ein Profil mit Deutsch auf Position 1 und
Englisch auf Position 2 anlegen, Cutoff auf Deutsch, als Default setzen.

Unter **Settings → Subtitles**: **Use embedded subtitles** und **Automatic
Subtitles Synchronization** einschalten.

## Paperless

`https://paperless.example.com`. Anmeldung mit `PAPERLESS_ADMIN_USER` und
`PAPERLESS_ADMIN_PASSWORD` aus der `.env`, beides hat der Installer erzeugt.

OneDrive verbinden: [docs/06](06-paperless-onedrive.md).

## Navidrome

`https://music.example.com`, beim ersten Aufruf das Admin-Konto anlegen. Der
Rest steht in [docs/08](08-musik.md).

## Profile schreiben

```bash
docker compose run --rm recyclarr sync --preview   # zeigt nur
docker compose run --rm recyclarr sync             # schreibt
```

Die Feineinstellung für „Deutsch bevorzugt, Englisch als Fallback" steht in
[docs/03](03-deutsche-profile.md) und ist nicht optional. Ohne sie bevorzugen
Sonarr und Radarr weiter englische Releases.

## Stolperfallen, kurz gefasst

| Symptom | Ursache |
|---|---|
| Sonarr erreicht qBittorrent nicht | Host ist `gluetun`, nicht `qbittorrent` |
| qBittorrent-WebUI aus dem LAN tot | `LAN_SUBNET` falsch, Gluetuns Firewall blockt |
| Import kopiert statt zu verlinken | zwei Mounts statt einem `/data` |
| Seeding-Port bleibt zu | Localhost-Bypass fehlt oder NAT-PMP war nicht aktiv |
| „Permission denied" beim Import | `PUID`/`PGID` passen nicht zu `/mnt/data` |
| Plex transcodiert per CPU | Häkchen in Plex nicht gesetzt oder `RENDER_GID` falsch |
| Homepage zeigt „Bad Request" | Hostname fehlt in `HOMEPAGE_ALLOWED_HOSTS` |
| Caddy startet nicht | `TAILSCALE_IP` leer, Tailscale war beim `--prepare` nicht verbunden |
| Lidarr hat kein Plugins-Menü | Container nicht auf dem `develop`-Tag |
| Scans erscheinen nicht in Paperless | `/etc/rclone/rclone.conf` fehlt oder ist veraltet |
