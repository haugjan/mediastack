# CLAUDE.md

Arbeitsgedächtnis für dieses Repo: was hier gilt, und warum. Der Rest steht
in `README.md` (Überblick) und `docs/` (Details pro Thema).

## Worum es geht

Ein Medienserver für daheim, installiert mit **einem** Befehl: `sudo ./setup.sh`.
Zielgruppe sind Leute ohne Linux-Erfahrung. Daraus folgt alles Weitere:

- **Mehrfach startbar.** `setup.sh` merkt sich alles in der `.env`, fragt nur
  nach, was fehlt, und macht nichts doppelt. Jede neue Funktion muss diesen
  Ablauf überleben, auch beim zehnten Lauf.
- **Nichts endet halb kaputt.** Fehlt ein Zugang, startet der Container gar
  nicht (Compose-Profil), statt in einer Neustartschleife zu landen.
- **Kein Zugang ist Pflicht.** Ohne VPN, Usenet, Domain und Paperless laufen
  trotzdem 16 Dienste.

## Sprache und Schreibweise

- Alles auf **Deutsch**, in einfachen Worten, ohne Fachjargon: Ausgaben,
  Kommentare, Dokumentation. Keine englischen Brocken, wo ein deutsches Wort
  reicht.
- **Markdown** (`README.md`, `docs/`) mit echten Umlauten.
- **Code und Konfiguration** (`setup.sh`, `scripts/`, `compose.yaml`,
  `Caddyfile`, `.env.example`, Vorlagen) bleiben **ASCII**: `ae`, `oe`, `ue`,
  `ss`. Das hält Terminals, Heredocs und Editoren aus jeder Ecke ruhig.
- Kommentare erklären das **Warum**, nicht das Was. Die vorhandenen Dateien
  sind der Maßstab: ein Kopfkommentar pro Datei, der den Zweck und die eine
  Falle nennt, die man sonst tritt.
- `setup.sh` ist mit **Tabs** eingerückt, YAML mit zwei Leerzeichen.
- **Commit-Nachrichten auf Englisch**, Betreff im Imperativ und knapp
  ("Run the Paperless Redis as the media user"), Rumpf auf ~72 Zeichen
  umgebrochen und in der Reihenfolge: Symptom, Ursache, Lösung.

## Architektur, die drei Dinge, die man wissen muss

1. **qBittorrent hat kein eigenes Netz.** `network_mode: service:gluetun`.
   Sonarr und Radarr erreichen es unter `http://gluetun:8080`. Häufigster
   Fehler im ganzen Stack, die CI prüft es deshalb hart.
2. **Plex und Caddy laufen im Host-Netz.** Plex wegen GDM-Discovery und
   eigenem Fernzugriff, Caddy weil es `tailscale0` sehen muss, um private
   Subdomains per `bind` daran zu fesseln. Caddy spricht seine Upstreams
   folglich über `127.0.0.1:<port>` an.
3. **`/data` ist ein einziger Mount.** Nur so wird beim Import gehardlinkt
   statt kopiert. Paperless hat seinen eigenen Baum unter `PAPERLESS_ROOT`.

Die Sicherheitsarchitektur ist zweistufig: `bind {$TAILSCALE_IP}` sorgt dafür,
dass am öffentlichen Interface für private Namen gar kein Listener existiert,
`private_only` prüft zusätzlich `remote_ip`. **Öffentlich ist einzig
`requests.<domain>` (Overseerr).** Das ist keine Stilfrage, die CI stellt es
fest.

Dazu kommt ein dritter Listener für Geräte im Haus ohne Tailscale:
`*.lan.<domain>` auf **Port 8443**, und der ist wieder zweistufig abgesichert.
Erstens der Port: der Router leitet nur 80 und 443 weiter, 8443 also nicht.
Zweitens `home_only`, das nur `{$LAN_SUBNET}` durchlässt — enger als
`private_only`, weil ein Notebook auch mal in einem fremden WLAN steht und
dort derselbe private Adressbereich gilt.

Dieser Block bindet bewusst an **keine** feste Adresse. Eine Bindung an die
LAN-Adresse wäre strenger, macht Caddy aber unstartbar, sobald der Rechner in
einem anderen Netz hängt: die Adresse gibt es dann nicht mehr, und der ganze
Proxy landet in der Neustartschleife. Die CI prüft beides — dass dieser
Listener auf 8443 sitzt und dass er nur das eigene Subnetz durchlässt.

## Compose-Profile

**Jeder Dienst hat sein eigenes Profil**, benannt wie er selbst. Ohne
Profile startet deshalb gar nichts — `COMPOSE_PROFILES` in der `.env` ist die
Liste dessen, was laufen soll, und `setup.sh` schreibt sie aus der Auswahl,
die es gleich zu Beginn abfragt. Von Hand hantiert damit niemand.

Drei Profile stehen für mehr als einen Container, weil die Teile einzeln
nicht lauffähig wären:

| Profil | Dienste | Braucht |
|---|---|---|
| `torrent` | Gluetun, qBittorrent | ProtonVPN-Schlüssel |
| `docs` | Paperless samt Postgres, Redis, Gotenberg, Tika | nichts |
| `proxy` | Caddy | Domain und Azure DNS |

Alle übrigen sind eins zu eins: `plex`, `sonarr`, `radarr`, `lidarr`,
`prowlarr`, `bazarr`, `navidrome`, `audiobookshelf`, `overseerr`, `tautulli`,
`homepage`, `uptime`, `cleanuparr`, `unpackerr`, `recyclarr`, `backfill`,
`usenet`, `radio`, `kometa`, `scrutiny`.

Neuer Dienst: **immer ein eigenes Profil und ein Eintrag im `CATALOG` von
`setup.sh`**, sonst lässt er sich nicht abwählen. Die CI schlägt an, wenn ein
Dienst ohne Profil auftaucht — dann liefe er an der Auswahl vorbei.
Zugangsabhängige Dienste bleiben zusätzlich aus, solange der Zugang fehlt:
ohne Profil laufen hieße ohne Zugang in die Neustartschleife.

## setup.sh

Rund 2300 Zeilen, neun Schritte, ein einziger davon bricht hart ab.

- **Ganz vorne steht die Auswahl.** Vor Schritt 1 zeigt `choose` eine Liste
  aller Dienste und Optionen, alles angekreuzt, Nummern schalten um. Die
  Tabelle dafür ist `CATALOG` (Schlüssel, Gruppe, Name, Beschreibung, Profil,
  Kachel), abgefragt wird sie überall mit `want <schlüssel>`. Kometa und
  Scrutiny sind bewusst vorab abgewählt, sie brauchen Handarbeit und liefen
  sonst in eine Neustartschleife. Beim zweiten Lauf spiegelt die Vorauswahl
  den Ist-Zustand, damit ein erneuter Start nichts wieder anschaltet, das
  bewusst weg sollte.
- Daraus folgt: **keine „willst du X?"-Fragen mehr** in den Schritten. Wer
  ausgewählt hat, wird nur noch nach dem gefragt, was von außen kommt —
  Schlüssel, Zugangsdaten, Domain.

- `set -uo pipefail`, **`set -e` ist mit Absicht nicht gesetzt**: ein
  fehlgeschlagener optionaler Schritt darf die Installation nicht abbrechen.
- Fehler sammeln statt abbrechen: `try "Was" befehl ...` loggt nach
  `setup.log` und legt Misslungenes über `problem()` in `PROBLEMS`, die
  Abschlussliste. Nur `stop()` beendet, und das praktisch nur in Schritt 2
  (keine Hardlinks möglich → jeder Download wäre eine Kopie).
- Ausgabe ausschließlich über `step/ok/info/note/bad`, Eingabe über
  `ask/ask_yn/ask_secret/ask_choice`. Die lesen von `/dev/tty`, damit das
  Skript auch in einer Pipe funktioniert.
- Zustand lebt in der `.env`: `env_get`, `env_set`, `env_ask_if_empty`. Nie
  direkt in die Datei schreiben.
- Eingebettetes Python steht immer im Heredoc `<<'PY' ... PY`. Die CI schneidet
  genau dieses Muster heraus und kompiliert es; ein anderer Marker wird nicht
  geprüft.
- Neuer Dienst mit Kachel: `PORT[...]` **und** `SUB[...]` oben im Skript
  ergänzen, sonst scheitert der CI-Check, der jeden `__URL_X__`-Platzhalter
  aus `homepage/services.yaml.tmpl` gegen `setup.sh` gegenprüft.
- Schritt 9 sammelt die API-Schlüssel selbst aus den Konfigurationsdateien
  der Apps ein. Nichts abtippen lassen, was auslesbar ist.
- Schritt 9 verkabelt die Apps anschließend über ihre eigenen APIs
  (Download-Clients, Prowlarr, Plex, Bazarr, Overseerr). Zwei Python-Helfer
  im Heredoc erledigen das Muster „Schema holen, Feld ändern,
  zurückschicken", statt JSON von Hand zu bauen — das überlebt App-Updates.
  Jeder Aufruf gibt **3** zurück, wenn schon alles stand: nur so kann die
  Ausgabe zwischen „eingetragen" und „war schon da" unterscheiden.
- Uptime Kuma ist die Ausnahme: keine API, alles über socket.io. Monitore
  werden direkt in `config/uptime-kuma/kuma.db` geschrieben, und nur im
  **gestoppten** Zustand, sonst überschreibt Kuma sie beim Beenden aus dem
  Speicher. Ohne Konto im Browser gibt es keine `user_id`, dann wird der
  Schritt übersprungen.
- Was `setup.sh` einträgt, ist immer die **containerinterne** Adresse
  (`http://sonarr:8989`, für qBittorrent `gluetun:8080`). Was es selbst
  aufruft, geht über `localhost:<veröffentlichter Port>`.
- `config/paperless-db` und `config/paperless-redis` sind vom rekursiven
  `chown` ausgenommen. Postgres (uid 70) und Redis (uid 999) richten ihre
  Ordner selbst ein; nimmt man sie mit, verlieren sie die Rechte an ihren
  eigenen Dateien und Paperless antwortet mit 500 — bei weiterhin grünem
  Healthcheck, weil `pg_isready` nur den Socket prüft.

## aircheckarr

Der einzige selbstgeschriebene Dienst im Stack, C# auf .NET 9, eigener
Unterordner mit eigenem Dockerfile. Baut nur, wenn das Profil `radio` aktiv
ist. Regeln dort: englische Bezeichner (das ist C#), deutsche Kommentare,
ASCII wie überall sonst. Einzige Fremdabhängigkeit ist
`Microsoft.Data.Sqlite`; Messen, Schneiden und Taggen macht `ffmpeg`.

Zwei Dinge, die man nicht kaputtmachen darf, weil sie im Praxislauf teuer
erkauft wurden: der **erste Titel nach dem Verbinden wird übersprungen**
(er läuft schon, der Mitschnitt wäre ein Torso), und **AAC wandert nach
M4A**, weil rohes ADTS keinen Platz für Tags hat und `-metadata` dort
stillschweigend verfällt.

## .env

- **Wird nie committet** (in `.gitignore`, die CI prüft es).
- **Wird nie gesourct.** `scripts/lib-env.sh` liest sie so, wie Compose sie
  versteht: Zeile für Zeile `KEY=Wert`. Ein `source` würde Werte mit `$`
  oder Klammern als Befehl ausführen.
- Die echte Domain steht **ausschließlich** dort unter `BASE_DOMAIN`. In
  Repo und Doku überall `example.com` bzw. `example.ch`.
- Neue Variable → `.env.example` mit Kommentar ergänzen und kennzeichnen, ob
  sie erkannt, generiert oder **MANUELL** ist.

## Prüfen vor dem Commit

Die CI (`.github/workflows/release.yml`) läuft bei jedem Push und PR. Lokal
gibt es `docker` und `python3`, aber **kein shellcheck**:

```bash
bash -n setup.sh && for f in scripts/*.sh; do bash -n "$f"; done
python3 -m py_compile scripts/backfill.py
sed 's/^\([A-Z_]*\)=$/\1=dummy/' .env.example > /tmp/s.env
docker compose --env-file /tmp/s.env config -q
```

Was die CI außerdem zusichert, und was man deshalb nicht kaputtmachen darf:
Profile schirmen die optionalen Dienste ab (ohne Profile ≥ 15 Dienste, aber
kein `gluetun`, `qbittorrent`, `sabnzbd`, `paperless`, `caddy`), qBittorrent
im Gluetun-Namespace, Plex und Caddy im Host-Netz, jeder Dienst mit
`restart`-Policy, das Port-Forwarding-Kommando von Gluetun einzeilig, der
Caddyfile baut und gibt nur Overseerr öffentlich frei, die Dashboard-Vorlage
ergibt in beiden Zugriffsarten gültiges YAML.

## Veröffentlichen

```bash
git tag v1.1.0 && git push origin v1.1.0
```

Das Archiv entsteht mit `git archive`, enthält also nur versionierte Dateien
und beachtet `export-ignore` aus `.gitattributes`. `.env` und `config/` sind
darin **nicht enthalten** — deshalb kann `scripts/update.sh` einfach
darüberkopieren, ohne Ausnahmeregeln für Benutzerdaten.

## Kleinigkeiten mit Geschichte

- **Renovate:** Postgres-Hauptversionen sind bewusst gesperrt (braucht Dump
  und Reinladen), Lidarr läuft auf `develop` wegen Tubifarry und bekommt
  einen eigenen PR.
- **Kein Huntarr.** Das Repo ist tot, der Fork nennt Telemetrie und
  obfuskierten Code. So ein Tool bekäme die API-Schlüssel aller Apps.
  Ersatz ist `scripts/backfill.py`, ~200 Zeilen Standardbibliothek. Die
  Drosselung (`BACKFILL_ITEMS_PER_CYCLE`) ist der ganze Sinn: eine pauschale
  Suche über die Bibliothek holt eine Indexer-Sperre.
- **Paperless und OneDrive sind zwei Einbahnstraßen**, kein Abgleich.
  `media/` wird nie synchronisiert, Paperless besitzt diese Pfade und die
  Datenbank verweist darauf.
- **Docker-Ports umgehen ufw** (`DOCKER-USER` greift vorher). Es schützen der
  Router und Caddys `bind`, nicht die Host-Firewall.
- **Deutsche Inhalte zuerst**, Englisch als Rückfall. Das macht Recyclarr mit
  den deutschen TRaSH-Profilen, Feinheiten in `docs/03-deutsche-profile.md`.
