# 1. Voraussetzungen auf dem bestehenden System

Zielsystem ist ein vorhandenes Linux Mint. Das meiste erledigt
`setup.sh`, dieses Dokument erklärt, was dabei passiert und was du selbst
entscheiden musst. Für die reine Installation brauchst du es nicht.

## Der schnelle Weg

```bash
sudo apt install -y curl

curl -fsSL https://github.com/haugjan/mediastack/releases/latest/download/mediastack.tar.gz | tar xz
cd mediastack && sudo ./setup.sh
```

Auf einem frisch installierten Debian fehlt `curl` in der Minimalvariante,
deshalb die erste Zeile.

Das Skript fragt nach, was es wissen muss, und ist beliebig oft
wiederholbar. Alles Ausführliche landet in `setup.log`.

## Drei Mint-Besonderheiten

**1. Docker braucht den Ubuntu-Codenamen.** Mint meldet in `/etc/os-release`
`ID=linuxmint` und einen eigenen Codenamen wie `wilma`. Das Docker-Repository
kennt den nicht, die Installation schlägt mit `404 Not Found` fehl. Der
passende Ubuntu-Codename steht in derselben Datei unter `UBUNTU_CODENAME`.
`setup.sh` liest genau den aus. Das ist die häufigste Ursache, warum
Docker-Anleitungen auf Mint nicht funktionieren.

**2. Mint schläft ein.** Es ist ein Desktop-System und legt sich nach
Untätigkeit schlafen. Ein Server, der schläft, ist kein Server: Plex ist weg,
Downloads stehen, die Timer feuern nicht. `setup.sh` maskiert
deshalb `sleep.target`, `suspend.target`, `hibernate.target` und
`hybrid-sleep.target`, und setzt bei Laptops `HandleLidSwitch=ignore`.

Die Desktop-Energieverwaltung musst du zusätzlich selbst umstellen, die läuft
in deiner Benutzersitzung: **Einstellungen → Energieverwaltung**, alle
Zeitschalter auf **Nie**, und den Bildschirmsperren-Timer nach Belieben.

**3. Der Desktop kostet RAM.** Cinnamon belegt im Leerlauf rund 1 bis 1.5 GB.
Bei 16 GB ist das egal, bei 8 GB wird es zusammen mit Paperless-OCR und
Plex-Transcoding eng. Falls du die Maschine ohnehin nur per SSH und Tailscale
bedienst, spart ein `sudo systemctl set-default multi-user.target` den
Desktop, ohne dass du etwas neu installieren musst. Rückgängig mit
`graphical.target`.

## Was `setup.sh` prüft und selbst erledigt

| Prüfung | Verhalten, wenn es fehlt |
|---|---|
| Distribution und Docker-Codename | Abbruch nur bei unbekannter Distribution |
| Docker und Compose v2 | wird nachinstalliert |
| Grundpakete (curl, git, jq, smartmontools, vainfo) | werden nachinstalliert |
| RAM | Hinweis, kein Abbruch |
| `/dev/dri/renderD128` und VA-API mit HEVC | Hinweis, Plex rechnet dann per CPU |
| Freier Platz am Speicherort | Hinweis ab 30 GB, Problem darunter |
| **Hardlink-Fähigkeit** | **harter Abbruch mit Erklärung** |
| Ruhezustand | wird abgeschaltet |
| Tailscale | wird auf Wunsch installiert und verbunden |

Der Hardlink-Test ist der einzige harte Abbruch, und das mit Absicht. Alles
andere führt zu einem Hinweis, während der Rest weiterläuft. Was nicht
geklappt hat, steht am Ende gesammelt in der Zusammenfassung und
ausführlich in `setup.log`.

Die Ports prüft `setup.sh` nicht mehr vorab. Ein Konflikt zeigt sich sofort
beim Start als nicht laufender Container, und das steht dann in der
Zusammenfassung.

## Der Hardlink-Test und warum er entscheidet

```
/mnt/data/                 <- EIN Dateisystem, EIN Docker-Mount als /data
├── torrents/              <- qBittorrent legt hier ab und seedet von hier
├── usenet/                <- SABnzbd
├── media/                 <- Plex und Navidrome lesen ausschliesslich hier
└── paperless/             <- data, media, consume, export
```

In **jedem** Container ist das ein einziges Volume, nämlich
`/mnt/data:/data`. Nicht `/downloads` und `/movies` getrennt.

Ein Hardlink bedeutet: die Datei liegt einmal physisch da, ist aber unter
zwei Pfaden sichtbar. qBittorrent seedet weiter aus `torrents/`, Plex liest
aus `media/`, und es kostet null zusätzlichen Platz. Sobald es zwei Mounts
sind, sieht der Kernel zwei Dateisysteme, Sonarr kann nicht mehr verlinken
und **kopiert** stattdessen. Ergebnis: doppelter Platzbedarf und das Seeding
endet, weil die Originaldatei nach dem Import verschwindet.

Das `setgid`-Bit (`chmod 2775`) auf allen Ordnern sorgt dafür, dass neue
Dateien die Gruppe `media` erben. Zusammen mit `UMASK=002` verhindert das die
ewigen Rechteprobleme beim Import.

## QuickSync

```bash
vainfo | grep -E "VAProfileH264|VAProfileHEVC"
getent group render | cut -d: -f3
```

Erscheinen H264- und HEVC-Profile, kann Plex die iGPU nutzen. Dass der
Mint-Desktop dieselbe GPU für die Anzeige verwendet, stört nicht.

Aktivieren musst du es danach in Plex selbst: **Einstellungen → Transcoder →
Hardwarebeschleunigte Kodierung verwenden**. Das Häkchen ist nicht
automatisch gesetzt, obwohl das Gerät durchgereicht ist. Das ist die
häufigste Ursache für „warum ist meine CPU bei 100 Prozent".

## Tailscale

```bash
sudo tailscale up --ssh
tailscale ip -4
```

`setup.sh` erledigt das in Schritt 4 selbst. Von Hand geht es auch, und
nachtragen kannst du es jederzeit:

```bash
sudo tailscale up --ssh
sudo ./setup.sh          # erkennt die IP dann und ergaenzt nur das
```

## Firewall, und was ufw hier nicht tut

**Veröffentlichte Docker-Ports umgehen ufw.** Docker schreibt seine Regeln in
die `DOCKER-USER`-Kette, die vor den ufw-Regeln greift. Wer `ufw deny 8989`
setzt und sich sicher fühlt, irrt.

Was die Admin-Oberflächen tatsächlich schützt, sind zwei andere Dinge:

- Am Router sind **ausschließlich 80 und 443** weitergeleitet.
- Caddy fesselt die privaten Subdomains per `bind` an das
  Tailscale-Interface, sie haben am öffentlichen Interface keinen Listener.

ufw ist trotzdem sinnvoll gegen das LAN:

```bash
sudo apt install -y ufw
sudo ufw default deny incoming
sudo ufw default allow outgoing
sudo ufw allow in on tailscale0
sudo ufw allow from 192.168.1.0/24 to any port 22 proto tcp
sudo ufw allow 80,443/tcp
sudo ufw enable
```

Optionale Härtung gegen den Docker-Bypass, falls du auch aus dem LAN
abriegeln willst:

```bash
sudo iptables -I DOCKER-USER -i tailscale0 -j RETURN
sudo iptables -I DOCKER-USER -s 192.168.1.0/24 -j RETURN
sudo iptables -A DOCKER-USER -j DROP
sudo apt install -y iptables-persistent
```

## Platten für Scrutiny

```bash
lsblk -dno NAME,SIZE,MODEL
```

Die `devices:`-Liste im `scrutiny`-Block der `compose.yaml` muss zu dieser
Ausgabe passen, sonst startet der Container nicht. Genau deshalb steckt
Scrutiny im Profil `extras` und läuft nicht automatisch mit. Einschalten,
wenn die Liste stimmt:

```bash
# in .env: COMPOSE_PROFILES=...,extras
docker compose up -d scrutiny
```

Bei Platten in USB-Gehäusen funktioniert SMART häufig nicht, weil die
USB-SATA-Bridge die Befehle nicht durchlässt:

```bash
sudo smartctl -a -d sat /dev/sdX
```

Kommt nichts Sinnvolles, kann Scrutiny diese Platte nicht überwachen.

## Backup

Der `mediastack-backup.timer` sichert `config/` und `.env` nachts
verschlüsselt nach OneDrive, siehe
[docs/06-paperless-onedrive.md](06-paperless-onedrive.md). Darin stecken die
Plex-Bibliothek mit Sehfortschritt, alle Profile, Indexer-Zugänge und die
Paperless-Datenbank. Die Medien selbst sind ersetzbar, das hier nicht.
