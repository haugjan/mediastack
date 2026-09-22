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

## Wie du es benutzt

1. **Sender holen.** Reiter *Sender*, Stilrichtung eintragen (`rock`,
   `funk`, `jazz`, `oldies` …), optional ein Land, dann *Katalog holen*.
   Der Dienst misst die neuen Sender im Hintergrund nach und schaltet die
   tauglichen von selbst scharf.
2. **Wünschen.** Reiter *Wünsche*, Interpret und Titel eintragen. Oder
   *Aus Lidarr übernehmen*: das holt die fehlenden Alben und löst sie in
   einzelne Titel auf.
3. **Warten.** Ohne offene Wünsche hört der Dienst gar nicht erst zu, das
   spart Bandbreite. Mit Wünschen hört er bis zu zwölf Sender gleichzeitig
   und schneidet mit, sobald einer davon etwas von der Liste spielt.

Fertige Mitschnitte landen als `<Interpret>/Radio-Mitschnitte/<Interpret> -
<Titel>` in der Musikbibliothek, getaggt. Navidrome und Plex finden sie beim
nächsten Scan, Lidarr stößt der Dienst selbst an.

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
