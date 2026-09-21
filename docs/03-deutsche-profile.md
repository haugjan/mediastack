# 3. Deutsch bevorzugt, Englisch als Fallback

Das ist der Teil, der in Standard-Anleitungen fehlt, weil sie englischsprachig
gedacht sind. Ohne diese Einstellungen greifen Sonarr und Radarr weiter
bevorzugt englische Releases, egal was du dir wünschst.

## Wie das Scoring überhaupt funktioniert

Sonarr v4 und Radarr v5 haben keine Sprachprofile mehr. Sprache ist nur noch
ein **Custom Format** unter vielen, das Punkte vergibt oder abzieht. Jedes
gefundene Release bekommt einen Gesamtscore, und das höchste gewinnt.

Die deutschen TRaSH-Profile, die Recyclarr schreibt, bringen im Kern drei
Gruppen mit:

| Gruppe | Wirkung |
|---|---|
| German Audio / German DL | hohe Pluspunkte für deutsche Tonspur |
| `[Unwanted] Unwanted Formats German` | German LQ, German Microsized, Line/Mic Dubbed werden mit -10000 praktisch verboten |
| Release-Gruppen und Streaming-Dienste | Feinabstufung der Qualität |

Der Punkt `-10000` ist kein Abzug, sondern ein Riegel: alles mit negativem
Gesamtscore wird gar nicht gegriffen. So verhindern die Profile, dass du statt
einer echten Synchronfassung eine schlechte Tonspur oder einen abgefilmten
Mitschnitt bekommst.

Ein englisches Release ohne deutsche Tonspur trifft keinen dieser Riegel. Es
bekommt einfach die deutschen Pluspunkte nicht und landet deshalb unter einer
deutschen Fassung, aber über null. Genau daraus entsteht der Fallback: es ist
erlaubt, aber es verliert, sobald eine deutsche Fassung auftaucht.

## Schritt 1: Recyclarr laufen lassen

```bash
docker compose run --rm recyclarr sync --preview
docker compose run --rm recyclarr sync
```

Danach existiert in Sonarr und Radarr jeweils das Profil
**`[German] HD Bluray + WEB`** samt aller Custom Formats.

## Schritt 2: Das Profil als Standard setzen

In Sonarr und Radarr unter **Settings → Profiles** das deutsche Profil
auswählen und prüfen:

| Einstellung | Wert | Warum |
|---|---|---|
| Upgrades Allowed | **an** | ohne das wird eine später erscheinende deutsche Fassung nie nachgeholt |
| Upgrade Until Quality | `Bluray-1080p` | Obergrenze der Qualität |
| Upgrade Until Custom Format Score | **10000** | damit der Sprung von englisch auf deutsch als Upgrade zählt |
| Minimum Custom Format Score | **0** | lässt englische Releases zu, sperrt die Riegel-Formate aus |

`Upgrade Until Custom Format Score` ist die entscheidende Zeile. Steht der Wert
zu niedrig, hält Sonarr das englische Release für "gut genug" und tauscht es
nie gegen die deutsche Fassung.

Falls Radarr bei dir noch ein Feld **Language** am Profil hat: auf `Any`
stehen lassen. Wer es hart auf `German` setzt, schaltet den Fallback ab und
wundert sich dann über eine halb leere Bibliothek.

## Schritt 3: Delay Profile, der eigentliche Trick

Ohne weitere Einstellung greift Sonarr das erste brauchbare Release, das der
RSS-Feed liefert. Bei englischsprachigen Serien ist das praktisch immer die
englische Fassung, weil die Stunden vor der Synchronfassung da ist. Sonarr
würde also grundsätzlich englisch greifen und die deutsche Fassung erst
Wochen später als Upgrade nachziehen. Das kostet doppelten Traffic.

Der Ausweg heißt **Delay Profile**: Sonarr wartet eine definierte Zeit, sammelt
in der Zwischenzeit alle Kandidaten und greift danach den mit dem höchsten
Score. Releases über einer Punkteschwelle dürfen die Wartezeit überspringen.

**Settings → Profiles → Delay Profiles**, das Standardprofil bearbeiten:

| Feld | Wert | Wirkung |
|---|---|---|
| Preferred Protocol | `Torrent` | bei Punktgleichstand gewinnt der Torrent, passend zur Aufteilung unten |
| Usenet Delay | `1440` | 24 Stunden |
| Torrent Delay | `1440` | 24 Stunden |
| Bypass if Above Custom Format Score | **an** | deutsche Fassungen werden sofort gegriffen |
| Minimum Custom Format Score | siehe unten | Schwelle für das Überspringen |

Die Schwelle liest du aus dem echten Profil ab, nicht aus einer Anleitung, weil
die TRaSH-Scores sich ändern. So gehst du vor: in Sonarr unter
**Settings → Custom Formats** den Score des German-Audio-Formats im deutschen
Profil ansehen und die Schwelle knapp darunter setzen.

Das Ergebnis:

- Deutsche Fassung verfügbar → sofort gegriffen, keine Wartezeit.
- Nur englisch verfügbar → 24 Stunden Karenz, dann wird englisch gegriffen.
- Deutsche Fassung erscheint später → wird als Upgrade nachgeholt und ersetzt
  die englische.

## Schritt 4: Quellen aufteilen

Deine Entscheidung war: Torrents primär für deutsche Inhalte, Usenet für
englische. Der Grund ist nicht Geschmack, sondern Verfügbarkeit. Deutsche
Releases werden auf den europäischen Usenet-Backbones überdurchschnittlich
schnell entfernt, während sie auf Trackern lange verfügbar bleiben.

In Prowlarr lässt sich das direkt abbilden, ohne dass du zwei Profile pflegen
musst. Pro Indexer gibt es unter **Indexers → (Indexer bearbeiten)** ein Feld
**Tags** und in Sonarr/Radarr unter **Settings → Indexers** je Indexer ein
Feld **Download Client Priority** sowie **Indexer Priority** (1 ist die
höchste).

Empfehlung:

| Quelle | Indexer Priority | Begründung |
|---|---|---|
| deutschsprachige Torrent-Quellen | `10` | erste Wahl für deutsche Fassungen |
| Usenet-Indexer (NZBGeek und zweiter) | `25` | schnell, aber bei Deutsch lückenhaft |

Zusammen mit `Preferred Protocol = Torrent` im Delay Profile heißt das: bei
gleichwertigen Kandidaten gewinnt die deutsche Torrent-Quelle, bei
englischsprachigem Material liefert Usenet in voller Geschwindigkeit.

## Schritt 5: Bazarr auf zwei Sprachen

> Das legt `setup.sh` inzwischen selbst an, samt Verbindung zu Sonarr und
> Radarr. Die Tabelle unten beschreibt, was dabei herauskommt — und wie du
> es von Hand nachbaust, falls du ein eigenes Profil willst. Ein vorhandenes
> Profil rührt der Installer nicht an.

**Settings → Languages → Add Language Profile:**

| Position | Sprache | Forced | HI |
|---|---|---|---|
| 1 | German | Normal or Forced | aus |
| 2 | English | Normal or Forced | aus |

Cutoff auf `German` setzen, dann hört Bazarr auf zu suchen, sobald deutsche
Untertitel da sind, und fällt sonst auf Englisch zurück.

Unter **Settings → Subtitles** zusätzlich aktivieren:

- **Use embedded subtitles**: an, damit Bazarr nicht sucht, was schon in der
  Datei steckt
- **Automatic Subtitles Synchronization**: an, korrigiert den Versatz per
  ffsubsync, was bei deutschen Untertiteln zu englischem Schnitt oft nötig ist

## Schritt 6: Sonderfall reine Originalfassungen

Bei Serien, die du bewusst im Original schaust, willst du die 24 Stunden
Karenz nicht. Dafür gibt es Tags:

1. In Sonarr ein zweites Quality Profile anlegen, etwa `Original EN`, ohne die
   deutschen Custom Formats.
2. Ein Delay Profile mit `Usenet Delay = 0` und `Torrent Delay = 0` anlegen und
   mit dem Tag `original` versehen.
3. Die betreffenden Serien mit dem Tag `original` versehen und auf das Profil
   `Original EN` setzen.

Delay Profiles werden nach Tag zugeordnet, das Standardprofil greift nur, wo
kein Tag passt. So bekommst du beide Verhaltensweisen in einer Instanz, ohne
eine zweite Bibliothek zu betreiben.

## Kontrolle

Ob das Scoring wirklich so wirkt, wie du denkst, siehst du am besten hier:

- In Sonarr eine Serie auswählen → **Manual Search** auf eine Episode. Die
  Trefferliste zeigt pro Release den Gesamtscore und beim Überfahren die
  einzelnen Custom Formats, die dazu beigetragen haben.
- Ein deutsches Release muss dort deutlich über einem gleichwertigen
  englischen liegen.
- Liegt es gleichauf, hat Recyclarr die Formate nicht geschrieben oder das
  falsche Profil ist der Serie zugewiesen.

Unter **Activity → History** lässt sich später nachvollziehen, warum ein
bestimmtes Release gegriffen wurde. Das ist die Stelle, an der man
Fehlkonfigurationen findet, nicht im Log.
