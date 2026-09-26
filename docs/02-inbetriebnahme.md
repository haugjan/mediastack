# 2. Inbetriebnahme

`setup.sh` nimmt dir den mechanischen Teil ab: es sammelt die API-Schlüssel
selbst ein und verkabelt die Apps anschließend untereinander. Übrig bleibt,
was an deinen Konten hängt und sich deshalb nicht skripten lässt.

> Die Adressen unten stehen in der Domain-Form (`https://sonarr.example.com`).
> Hast du beim Installer keine Domain eingerichtet, nimm stattdessen
> `http://<ip-des-servers>:<port>`. Die Portnummern stehen in der
> Zusammenfassung, die `setup.sh` am Ende ausgibt, und auf der Startseite.

## Was der Installer selbst macht

```bash
sudo ./setup.sh
```

Schritt 9 startet alles, wartet bis die Dienste antworten, und **liest dann
die API-Schlüssel aus deren Konfigurationsdateien aus** (`config.xml` bei den
*arr-Apps, `sabnzbd.ini` bei SABnzbd, `config.yaml` bei Bazarr,
`Preferences.xml` bei Plex). Die Werte landen in der `.env`.

Mit diesen Schlüsseln richtet es danach ein:

| Bereich | Was eingetragen wird |
|---|---|
| Sonarr, Radarr, Lidarr | Stammordner, Download-Clients, Hardlinks, Umbenennen, Extra Files |
| qBittorrent | Kategorien `tv`, `movies`, `music`, `books` samt Zielpfad, Queueing, festes Passwort |
| Prowlarr | Verbindung zu allen drei *arr-Apps mit vollem Abgleich |
| Recyclarr | die deutschen TRaSH-Profile, einmal durchgeschrieben |
| Plex | Transcode-Ziel `/transcode`, Bibliotheken Filme, Serien, Musik |
| Bazarr | Sonarr und Radarr, Sprachprofil Deutsch vor Englisch, Synchronisierung |
| Overseerr | Sonarr und Radarr mit dem deutschen Qualitätsprofil |
| Tautulli | API freigegeben, damit die Plex-Kachel Zahlen zeigt |
| Uptime Kuma | ein Monitor je Dienst, sobald das Konto angelegt ist |

Ebenfalls automatisch: der Vergleich der Gluetun-IP mit der Host-IP, und die
Gegenprobe, ob ProtonVPN wirklich einen eingehenden Port weiterleitet.

Jeder dieser Schritte prüft vorher, ob es schon eingerichtet ist. Ein zweiter
Lauf ändert deshalb nichts und überschreibt auch nicht, was du selbst
angepasst hast.

## Vier Werte musst du selbst eintragen

Danach fragt `setup.sh` in Schritt 5 bis 8 nach. Von Hand in `.env` geht es auch:

| Variable | Woher |
|---|---|
| `PROTON_WG_PRIVATE_KEY` | Proton → Downloads → WireGuard, dabei **NAT-PMP und P2P aktivieren** |
| `PLEX_CLAIM` | <https://www.plex.tv/claim/>, nur 4 Minuten gültig |
| `ACME_EMAIL` | deine Adresse für Let's Encrypt |
| `AZURE_*` | Service Principal, siehe [docs/07](07-azure-dns.md) |

Alles davon ist optional. Was du in der Auswahl am Anfang abwählst, wird gar
nicht erst erfragt; und lässt du einen Wert leer, läuft der Rest trotzdem, und
du holst es später mit einem weiteren `sudo ./setup.sh` nach.

Bei ProtonVPN ist das Häkchen bei NAT-PMP der Punkt, an dem es meistens
schiefgeht. Ohne das bekommt qBittorrent keinen eingehenden Port und seedet
nur zu Peers, die sich von selbst melden. Der Installer meldet das am Ende
als Problem, statt es stillschweigend zu übergehen — der Tunnel selbst steht
ja, der Fehler fällt sonst monatelang nicht auf.

Zwei Dinge, die man dazu wissen muss:

**Die Haken gehören zum Schlüssel.** Bei WireGuard werden NAT-PMP und P2P
beim *Erzeugen* der Konfiguration festgelegt und sind Teil des Zugangs.
Nachträglich umstellen geht nicht — es braucht einen neuen Schlüssel. Der
alte bleibt dabei gültig, du bekommst einfach einen zweiten. Findet der
Installer am Ende keinen weitergeleiteten Port, bietet er direkt an, einen
neuen einzutragen; sonst käme man nicht weiter, weil ein zweiter Lauf nicht
erneut nach etwas fragt, das schon in der `.env` steht.

**Portweiterleitung gibt es nur im Bezahlplan.** Auf einem kostenlosen Konto
lehnt Proton NAT-PMP immer ab. Im Protokoll von gluetun sieht das so aus:

```
ERROR [vpn] starting port forwarding service: getting external IPv4 address:
read udp 10.2.0.2:...->10.2.0.1:5351: recvfrom: connection refused
```

## Was im Browser bleibt

Fünf Dinge, und alle hängen an einem Konto oder an einer Auswahl, die dir
niemand abnehmen kann.

### 1. Suchquellen in Prowlarr

**Indexers → Add Indexer.** Das ist der einzige Schritt, ohne den gar nichts
gefunden wird. Aufteilung nach [docs/03](03-deutsche-profile.md):
deutschsprachige Torrent-Quellen mit Priorität 10, Usenet-Indexer mit
Priorität 25.

Nach dem Speichern schiebt Prowlarr jeden Indexer automatisch in Sonarr,
Radarr und Lidarr — die Verbindung dahin steht schon.

**Indexer hinter Cloudflare**, 1337x zum Beispiel, antworten Prowlarr sonst
nur mit `403`. Dafür läuft FlareSolverr mit, ein kopfloser Browser, der die
Prüfung löst. Der Installer trägt ihn in Prowlarr ein und legt dabei den Tag
`flaresolverr` an. Damit er für einen Indexer greift, muss dieser Indexer
**denselben Tag tragen** — im Indexer unter *Tags* eintragen, sonst läuft die
Abfrage weiter ohne ihn und scheitert.

Der Dienst kostet ein paar hundert MB Arbeitsspeicher. Wer keine solchen
Indexer nutzt, wählt ihn in der Auswahl am Anfang ab.

### 2. Overseerr anmelden

`https://requests.example.com` öffnen und mit dem Plex-Konto anmelden. Sonarr
und Radarr sind darin bereits eingetragen, samt Stammordner und dem Profil
`[German] HD Bluray + WEB`.

### 3. Navidrome

`https://music.example.com`, beim ersten Aufruf das Admin-Konto anlegen. Der
Rest steht in [docs/08](08-musik.md).

### 4. SABnzbd, falls du Usenet nutzt

`https://sab.example.com`, Assistent durchlaufen, Zugangsdaten deines
Anbieters eintragen. Die Download-Clients in Sonarr, Radarr und Lidarr trägt
der Installer ein, sobald der Schlüssel in der `.env` steht — also beim
nächsten `sudo ./setup.sh`.

Unter **Config → Folders** gehören Temporary Download Folder auf
`/data/usenet/incomplete` und Completed Download Folder auf
`/data/usenet/complete`, unter **Config → Categories** die vier Kategorien
`tv`, `movies`, `music`, `books` mit Folder `/data/usenet/complete/<name>`.
Unter **Config → Switches** lohnt **Direct Unpack**, das entpackt parallel
zum Download statt danach.

### 5. Uptime Kuma

`https://status.example.com`, beim ersten Aufruf Benutzername und Passwort
vergeben. Danach einmal `sudo ./setup.sh` — dann legt der Installer für
jeden laufenden Dienst einen Monitor an. Vorher geht das nicht: ohne Konto
gibt es niemanden, dem die Monitore gehören könnten.

Kuma hat als einzige App im Stack keine Schnittstelle dafür, alles läuft
über den Browser. Der Installer schreibt deshalb direkt in seine Datenbank,
und zwar nur, solange der Container steht — Kuma hält die Monitore im
Speicher und würde sie beim Beenden zurückschreiben.

### Und Paperless

`https://paperless.example.com`, Anmeldung mit `PAPERLESS_ADMIN_USER` und
`PAPERLESS_ADMIN_PASSWORD` aus der `.env`, beides hat der Installer erzeugt.
OneDrive verbinden: [docs/06](06-paperless-onedrive.md).

## Plex und die Hardware

Plex ist mit deinem Konto verbunden, die drei Bibliotheken stehen, und das
Umrechnen läuft über `/transcode`, also über den Arbeitsspeicher statt über
die SSD.

**Hardwarebeschleunigtes Umrechnen ist eine Plex-Pass-Funktion.** Ohne Abo
rechnet Plex per CPU, auch wenn die Einstellung gesetzt ist und die
Grafikeinheit durchgereicht wurde. Ob dein Rechner überhaupt eine nutzbare
hat, sagt dir `setup.sh` in Schritt 1; wenn nicht, hilft
[docs/04](04-hardware.md).

## Wenn du doch von Hand ran musst

Der Installer trägt nichts ein, was schon dasteht. Hast du selbst etwas
angepasst, bleibt es. Umgekehrt: ist ein Schritt fehlgeschlagen, steht er am
Ende in der Problemliste, und ein erneutes `sudo ./setup.sh` holt ihn nach.

Für den Fall, dass du es selbst machen willst, die Werte:

**Download-Clients** (Settings → Download Clients → +):

| Feld | qBittorrent | SABnzbd |
|---|---|---|
| Host | **`gluetun`** | `sabnzbd` |
| Port | `8080` | `8080` (containerintern, nicht 8081) |
| Zugang | `QBIT_USER` / `QBIT_PASS` aus der `.env` | API-Key aus der `.env` |
| Category | `tv` bzw. `movies` bzw. `music` | dieselben |

Nicht `qbittorrent` als Host eintragen. Der Container hat kein eigenes Netz,
er lebt im Namespace von Gluetun. Unter dem Namen `qbittorrent` existiert im
Docker-Netz nichts.

**Stammordner:** Sonarr `/data/media/tv`, Radarr `/data/media/movies`,
Lidarr `/data/media/music`.

**qBittorrent-Kategorien:** `tv`, `movies`, `music`, `books`, jeweils mit
Save Path `/data/torrents/<name>`.

**Prowlarr → Apps:** Prowlarr Server `http://prowlarr:9696`, App Server
`http://sonarr:8989` bzw. `:7878` bzw. `:8686`.

**Medienverwaltung:** Use Hardlinks an, Import Extra Files `srt,sub,idx`,
Rename an.

**Qualitätsprofile** neu schreiben:

```bash
docker compose run --rm recyclarr sync --preview   # zeigt nur
docker compose run --rm recyclarr sync             # schreibt
```

**qBittorrent-Passwort**, falls der Installer es nicht setzen konnte (etwa
weil du vorher schon eines vergeben hast): temporäres Passwort aus dem Log
holen und die beiden WebUI-Zeilen selbst setzen.

```bash
docker compose logs qbittorrent | grep -i "temporary password"
```

Der Haken bei **Bypass authentication for clients on localhost** ist dabei
nicht optional. Gluetun schiebt den weitergeleiteten Port per API-Aufruf von
`127.0.0.1` hinein. Ohne den Haken scheitert das an der Anmeldung, und dein
Seeding-Port bleibt zu.

## Stolperfallen, kurz gefasst

| Symptom | Ursache |
|---|---|
| Sonarr erreicht qBittorrent nicht | Host ist `gluetun`, nicht `qbittorrent` |
| qBittorrent-WebUI aus dem LAN tot | `LAN_SUBNET` falsch, Gluetuns Firewall blockt |
| Import kopiert statt zu verlinken | zwei Mounts statt einem `/data` |
| Seeding-Port bleibt zu | Localhost-Bypass fehlt oder NAT-PMP war nicht aktiv |
| „Permission denied" beim Import | `PUID`/`PGID` passen nicht zu `/mnt/data` |
| Plex rechnet per CPU | kein Plex Pass, oder `RENDER_GID` falsch |
| Homepage zeigt „Bad Request" | Hostname fehlt in `HOMEPAGE_ALLOWED_HOSTS` |
| Caddy startet nicht | `TAILSCALE_IP` leer, Tailscale war beim `--prepare` nicht verbunden |
| Lidarr hat kein Plugins-Menü | Container nicht auf dem `develop`-Tag |
| Scans erscheinen nicht in Paperless | `/etc/rclone/rclone.conf` fehlt oder ist veraltet |
| Bazarr lädt keine Untertitel | Sprachprofil fehlt; Bazarr meldet dabei keinen Fehler |
| Recyclarr schreibt nichts, endet aber mit 0 | zwei Instanzen gleich benannt, siehe `recyclarr/recyclarr.yml` |
| Paperless antwortet mit 500 | `config/paperless-db` oder `-redis` wurden umgechownt |
