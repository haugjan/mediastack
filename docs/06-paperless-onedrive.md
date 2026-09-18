# 6. Paperless und OneDrive

## Die Grundregel

Paperless hat vier Verzeichnisse, und sie gehören verschiedenen Leuten:

| Verzeichnis | Wer bestimmt den Inhalt | OneDrive |
|---|---|---|
| `consume/` | du, von außen | **rein**, einbahnig |
| `media/` | Paperless allein | **niemals** |
| `data/` | Paperless allein (Suchindex) | **niemals** |
| `export/` | `document_exporter` | **raus**, einbahnig |

`media/` ist das Archiv. Paperless benennt die Dateien nach
`PAPERLESS_FILENAME_FORMAT` und die Datenbank verweist auf genau diese Pfade.
Ein Zwei-Wege-Sync darauf ist ein Datenverlust mit Ankündigung:

- OneDrive legt bei Konflikten Kopien wie `Rechnung-DESKTOP-PC.pdf` an. Paperless
  kennt sie nicht, sie liegen als Müll im Archiv.
- Ein halb hochgeladenes PDF sieht für Paperless aus wie eine beschädigte
  Datei.
- Wird eine Datei auf einem anderen Gerät gelöscht, propagiert das ins
  Archiv, während die Datenbank weiter darauf zeigt. Paperless zeigt das
  Dokument dann an und kann es nicht öffnen.

Deshalb zwei getrennte Einbahnstraßen statt eines Abgleichs. Das ist nicht
nur sicherer, es ist auch einfacher zu verstehen, wenn mal etwas fehlt.

```
OneDrive:/Scans ────── rclone move (runter, Quelle wird geleert) ─────► consume/
                                                                          │
                                                                Paperless verarbeitet,
                                                                loescht aus consume/
                                                                          ▼
                                                                       media/   (bleibt lokal)
                                                                          │
                                                                 document_exporter
                                                                          ▼
OneDrive:/Paperless/Spiegel  ◄── rclone sync (Klartext) ───────────── export/
OneDrive:/Paperless/Backup   ◄── rclone sync (verschluesselt) ───────── export/
```

## Schritt 1: rclone mit OneDrive verbinden

Das muss als **dein Desktop-Benutzer** laufen, weil die OAuth-Anmeldung
einen Browser öffnet. Mint hat einen, das ist hier praktisch.

```bash
rclone config
```

- `n` für einen neuen Remote
- Name: `onedrive`
- Storage: `onedrive`
- `client_id` und `client_secret` leer lassen
- Region: `global`
- Beim Kontotyp **OneDrive Personal** wählen, nicht Business
- `y` für die Browser-Anmeldung, dort mit dem privaten Konto anmelden
- Das angebotene Laufwerk bestätigen

Testen:

```bash
rclone lsd onedrive:
rclone mkdir onedrive:Scans
rclone mkdir onedrive:Paperless
```

## Schritt 2: Das verschlüsselte Remote anlegen

```bash
rclone config
```

- `n`, Name: `onedrive-crypt`
- Storage: `crypt`
- `remote`: `onedrive:Paperless/Verschluesselt`
- **Dateinamen verschlüsseln**: `standard`
- **Verzeichnisnamen verschlüsseln**: `true`
- Passwort: eigenes vergeben, oder `g` für ein generiertes
- Salt (zweites Passwort): ebenfalls setzen

> **Dieses Passwort und das Salt gehören in deinen Passwortmanager, jetzt
> sofort.** Ohne sie ist das Backup unwiederbringlich verloren. Ein
> verschlüsseltes Backup, dessen Schlüssel nur auf dem Server liegt, den es
> absichern soll, ist kein Backup.

## Schritt 3: Konfiguration für die Timer bereitstellen

Die systemd-Units laufen als root, deine rclone-Konfiguration liegt aber in
deinem Home. Einmal kopieren:

```bash
sudo mkdir -p /etc/rclone
sudo cp ~/.config/rclone/rclone.conf /etc/rclone/rclone.conf
sudo chmod 600 /etc/rclone/rclone.conf
sudo rclone --config /etc/rclone/rclone.conf lsd onedrive:   # Gegenprobe
```

Nach jeder Änderung an den Remotes musst du das wiederholen. Das ist der
Preis dafür, dass die Timer als Systemdienst laufen und nicht an deiner
Desktop-Sitzung hängen.

## Schritt 4: Timer aktivieren

```bash
sudo systemctl enable --now paperless-inbox.timer
sudo systemctl enable --now paperless-export.timer
sudo systemctl enable --now mediastack-backup.timer
systemctl list-timers 'paperless-*' 'mediastack-*'
```

| Timer | Takt | Was er tut |
|---|---|---|
| `paperless-inbox` | alle 5 min | holt Scans aus OneDrive, leert die Quelle |
| `paperless-export` | täglich 03:30 | exportiert und spiegelt in beide Ziele |
| `mediastack-backup` | täglich 04:30 | packt `config/` und lädt es verschlüsselt hoch |

Einen Lauf von Hand anstoßen und zusehen:

```bash
sudo systemctl start paperless-inbox.service
journalctl -u paperless-inbox.service -f
```

## Der Arbeitsablauf im Alltag

Auf dem Handy die OneDrive-App, Scan in den Ordner `Scans` legen. Die
Microsoft-Lens-Funktion in der OneDrive-App macht brauchbare Scans mit
Randerkennung, ein separater Scanner-App-Kauf ist nicht nötig. Spätestens
fünf Minuten später ist das Dokument in Paperless, mit OCR auf Deutsch, und
aus `Scans` verschwunden.

`PAPERLESS_CONSUMER_SUBDIRS_AS_TAGS` ist eingeschaltet. Legst du in OneDrive
einen Unterordner `Scans/Versicherung` an, bekommt alles darin automatisch
das Tag `Versicherung`. Das ist der billigste Weg, beim Einwerfen schon zu
sortieren.

`--min-age 1m` im Inbox-Skript sorgt dafür, dass rclone eine Datei erst
anfasst, wenn sie eine Minute alt ist. Ohne das greift es ein PDF ab, das
vom Handy gerade erst halb hochgeladen ist.

## Wiederherstellung

Der Ernstfall, einmal durchgespielt. Paperless ist offiziell so gebaut, dass
der Export vollständig ist: Dokumente, Metadaten, Tags, Korrespondenten,
Benutzer.

```bash
# 1. Backup herunterladen (verschluesselt, rclone entschluesselt beim Lesen)
rclone sync onedrive-crypt:Paperless/Backup /mnt/data/paperless/export

# 2. Frische Instanz starten
docker compose up -d paperless-db paperless-redis paperless

# 3. Importieren
docker compose exec -T paperless document_importer /usr/src/paperless/export
```

Den Klartext-Spiegel brauchst du dafür nicht, er ist nur für den schnellen
Zugriff am Handy da. Genau deshalb gibt es beide.

**Probiere das einmal aus, bevor du dich darauf verlässt.** Ein Backup, das
nie zurückgespielt wurde, ist eine Vermutung.

## Speicherplatz

OneDrive Personal ist kostenlos bei 5 GB. Du legst hier zweimal denselben
Export ab, also rechne mit dem doppelten Volumen deiner Dokumente plus dem
`config/`-Archiv. Für ein paar tausend Seiten Papier reicht das, aber prüfe
es, bevor du eine große Altablage einscannst. Mit Microsoft 365 Personal
sind es 1 TB und die Frage erledigt sich.
