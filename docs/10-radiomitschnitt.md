# 10. Radiomitschnitt mit Aircheckarr

Die Lücke, die dieses Kapitel schließt: Lidarr findet nicht alles. Ältere
Titel, Regionales, Eigenproduktionen kleiner Labels — vieles steht bei
keinem Indexer. Im Radio läuft es trotzdem, und zwar ständig.

`docs/08-musik.md` verwirft Webradio-Mitschnitte pauschal, und für
`streamripper` stimmt das auch. Aircheckarr geht das Problem anders an; was
genau anders ist, steht in [`aircheckarr/README.md`](../aircheckarr/README.md).

## Einschalten

```bash
sudo ./setup.sh
```

In Schritt 6 fragt der Installer danach. Der Bau des Containers dauert einige
Minuten, deshalb steht der Dienst hinter dem Profil `radio` und läuft nicht
ungefragt mit.

Danach erreichbar unter `https://radio.example.com` oder
`http://<ip>:8099`.

Die Oberfläche ist aufgebaut wie Sonarr, Radarr und Lidarr: links die
Seiten, oben die Knöpfe, darunter die Tabelle.

## Wie du es benutzt

1. **Sender auswählen.** Seite *Sender*, dann *Sender suchen*: nach Name
   („Swiss Jazz"), Stilrichtung (`funk`, `oldies` …) oder Land suchen,
   beliebig viele Treffer ankreuzen und übernehmen. Der Dienst misst sie
   sofort nach. In der Liste schaltet der Regler einen Sender an oder aus;
   mehrere auf einmal gehen über die Kästchen links und die Leiste unten.
   *Nach Stilrichtung* holt gleich einige hundert Sender einer Richtung.
2. **Wünschen, am besten in Lidarr.** Beobachte das Album in Lidarr wie
   gewohnt. Was dort fehlt, steht nach spätestens 15 Minuten unter
   *Wünsche*, und Aircheckarr sucht parallel zu Torrent und Usenet danach.
   Was Lidarr nicht kennt, trägst du unter *Wünsche → Hinzufügen* von Hand
   ein.
3. **Warten.** Ohne offene Wünsche hört der Dienst gar nicht erst zu, das
   spart Bandbreite. Mit Wünschen hört er bis zu zwölf Sender gleichzeitig.
   Was gerade läuft und was aufgenommen wird, zeigt *Aktivität*.

Ein Mitschnitt zu einem Lidarr-Wunsch geht an Lidarr zurück: Lidarr
übernimmt die Datei, benennt sie wie alles andere und hakt den Titel ab.
In *Mitschnitte* steht dann „In Lidarr". Wünsche von Hand landen als
`<Interpret>/Radio-Mitschnitte/<Interpret> - <Titel>` direkt in der
Musikbibliothek, getaggt. Navidrome und Plex finden beides beim nächsten
Scan.

## Wie lange das dauert

Ehrlich: unterschiedlich. Ein aktueller Charttitel läuft auf einem
Formatradio mehrmals am Tag, den hast du in Stunden. Ein Albumtrack, der nie
Single war, läuft vielleicht nie. Der Dienst hat unbegrenzt Geduld, du
brauchst sie auch.

Sender mit enger Rotation und passendem Format liefern am schnellsten. Ein
Spartensender für Funk der Siebziger bringt für Funk der Siebziger mehr als
zehn Popsender.

## Die Qualitätsfrage

Die Spalte *Katalog* in der Senderliste ist Selbstauskunft und oft falsch.
Maßgeblich ist *gemessen* — das kommt von `ffprobe` aus dem Datenstrom
selbst. Unter 128 kbit/s wird nichts mitgehört, einstellbar über
`AIRCHECKARR_MIN_BITRATE`.

Realistisch liegen gute Sender bei 192 bis 320 kbit/s MP3 oder AAC. Das ist
besser als YouTube (Opus, 128 bis 160) und schlechter als eine CD. Für Autos,
Küche und Handy reicht es; für Alben, die dir wirklich wichtig sind, bleibt
der Kauf bei Bandcamp der bessere Weg, siehe [docs/08](08-musik.md).

## Wenn nichts ankommt

| Symptom | Ursache |
|---|---|
| Kein Sender „tauglich" | Filter zu streng, oder der Katalog enthält für diese Stilrichtung nur schwache Sender |
| Sender tauglich, aber nichts passiert | kein offener Wunsch — ohne Wünsche wird nicht zugehört |
| Mitschnitte werden als „zu kurz" verworfen | Sender meldet Jingles als Titel, das ist normal und richtig so |
| Alles wird verworfen als „zu lang" | Sender meldet Sendungsnamen statt Titeln, taugt nicht |
| Titel läuft, wird aber nicht erkannt | Schreibweise weicht stark ab; `AIRCHECKARR_MATCH_THRESHOLD` senken, aber nicht unter 0.8 |
| Keine Wünsche aus Lidarr | `LIDARR_API_KEY` fehlt in der `.env`, oder das Album wird in Lidarr nicht beobachtet; Stand unter *System* |
| Mitschnitt „Abgelegt" statt „In Lidarr" | Lidarr hat die Übergabe abgelehnt, der Grund steht beim Darüberfahren; die Datei liegt trotzdem in der Bibliothek |
