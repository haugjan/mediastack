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
OneDrive:/Paperless/Spiegel        ◄── rclone sync (Klartext) ─────── export/
OneDrive:/Paperless/Verschluesselt ◄── rclone sync (verschluesselt) ─ export/
```

Die Ordnernamen sind Vorschläge. `setup.sh` fragt beim ersten Einrichten
nach allen dreien, Enter übernimmt den Vorschlag. Bei jedem späteren Lauf
zeigt es die aktuellen Ordner und fragt, ob du sie ändern willst. Zwei
Grenzen gibt es dabei:

- **Kein Ordner darf in einem anderen liegen.** Der Spiegel wird per
  `rclone sync` abgeglichen und löscht alles, was nicht aus Paperless
  kommt. Läge der Eingang darin, wären die Scans weg.
- **Der verschlüsselte Ordner steht nach dem ersten Einrichten fest.** Sein
  Ort ist Teil des Remotes `onedrive-crypt`. Ein neuer Ort hieße ein leeres
  Backup neben dem alten.

Wechselst du Eingang oder Spiegel, verschiebt das Skript nichts. Der neue
Spiegel entsteht beim nächsten Export von selbst, den alten kannst du in
OneDrive löschen. Im alten Eingang holt niemand mehr etwas ab.

Im verschlüsselten Ordner liegen zwei Unterordner, in OneDrive nur als
Zeichensalat zu sehen: `Paperless/Backup` für die Dokumente und
`Mediastack/config` für die nächtliche Sicherung der App-Einstellungen.

## Einrichtung

Das macht `sudo ./setup.sh` in Schritt 7. Ein einziger Handgriff bleibt bei
dir: die Anmeldung bei Microsoft läuft über OAuth im Browser, und ein
Passwortfeld lässt sich nicht automatisieren.

Was das Skript selbst erledigt:

| | |
|---|---|
| rclone installieren | aus der Paketquelle, bei zu alter Version über rclone.org |
| Remote `onedrive` | startet die Anmeldung, du bestätigst im Browser |
| Remote `onedrive-crypt` | Passwort und Salt erzeugt es selbst |
| Ordner in OneDrive | fragt nach Eingang, Spiegel und verschlüsseltem Ordner und legt sie an |
| `/etc/rclone/rclone.conf` | Kopie für die Timer, Rechte `600` |
| Die drei Timer | `enable --now`, passend zu dem, was du eingerichtet hast |

Bei der Anmeldung fragt rclone drei Dinge:

- **Kontotyp**: `OneDrive Personal`, nicht Business und nicht SharePoint
- **Region**: `global`
- **Laufwerk**: das angebotene bestätigen

Öffnet sich kein Browser, steht im Text eine Adresse mit
`127.0.0.1:53682`. Die kopierst du in einen Browser **auf diesem Rechner**,
denn rclone wartet lokal auf die Antwort.

### Der Schlüssel für das verschlüsselte Backup

Am Ende zeigt das Skript Passwort und Salt des Remotes `onedrive-crypt` an,
**genau einmal**.

> **Beides gehört sofort in deinen Passwortmanager.** Ohne die zwei Werte ist
> das verschlüsselte Backup unwiederbringlich verloren. Sie stehen zwar auch
> in der `.env`, aber die liegt auf genau dem Rechner, den das Backup
> absichern soll. Ein Backup, dessen Schlüssel nur dort liegt, ist keines.

Beim nächsten Lauf passiert nichts doppelt: besteht das Remote schon, bleibt
es samt Passwort unangetastet.

### Die nächtliche Vollsicherung

Nach der Anmeldung fragt das Skript separat, ob `mediastack-backup.timer`
laufen soll. Der Grund für die Rückfrage: dieser Lauf **hält um 04:30 für ein
paar Minuten alle Container an**, sonst erwischt `tar` die Datenbanken mitten
im Schreiben. Wer nachts zuschaut, merkt das.

### Was danach läuft

```bash
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

## Von Hand, falls du es lieber selbst machst

Der Weg ohne `setup.sh`, und zugleich die Erklärung, was das Skript tut.

```bash
rclone config
```

Als **dein Desktop-Benutzer**, nicht als root: die OAuth-Anmeldung öffnet
einen Browser, und root hat keine Sitzung, in der einer aufgehen könnte.

- `n` für einen neuen Remote, Name `onedrive`, Storage `onedrive`
- `client_id` und `client_secret` leer lassen, Region `global`
- Kontotyp **OneDrive Personal**, danach das Laufwerk bestätigen

Dann das verschlüsselte Remote darüber:

```bash
rclone config create onedrive-crypt crypt \
    remote="onedrive:Paperless/Verschluesselt" \
    filename_encryption=standard \
    directory_name_encryption=true \
    password="<eigenes Passwort>" \
    password2="<eigenes Salt>" \
    --obscure
```

`--obscure` sorgt dafür, dass rclone die beiden Werte verschlüsselt ablegt
statt im Klartext. Beide gehören trotzdem in den Passwortmanager, siehe oben.

Zuletzt die Konfiguration für die Timer bereitstellen, die als root laufen
und deshalb nicht in dein Home schauen:

```bash
sudo install -d -m 700 /etc/rclone
sudo install -m 600 ~/.config/rclone/rclone.conf /etc/rclone/rclone.conf
sudo rclone --config /etc/rclone/rclone.conf lsd onedrive:   # Gegenprobe
sudo systemctl enable --now paperless-inbox.timer paperless-export.timer
sudo systemctl enable --now mediastack-backup.timer
```

**Nach jeder Änderung an den Remotes musst du die Kopie erneuern.** Oder
einfach `sudo ./setup.sh` laufen lassen, das macht genau das.

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

Auf einem frischen Rechner brauchst du zuerst das Remote `onedrive-crypt`
zurück, und dafür Passwort und Salt aus deinem Passwortmanager. Das ist der
Moment, für den du sie dort abgelegt hast.

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
