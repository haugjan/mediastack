# 4. Hardware

## Warum kein Raspberry Pi

Die *arr-Seite läuft auf einem Pi problemlos. Sonarr, Radarr, Prowlarr und die
Helfer sind die meiste Zeit Leerlauf-Prozesse, die auf Webhooks warten, und es
gibt für alles arm64-Images.

Das Problem ist Plex, und zwar an einer Stelle, die sich nicht wegkonfigurieren
lässt:

- **Plex kann die GPU des Pi nicht für Transcoding nutzen.** Kein QuickSync,
  kein NVENC, und die VideoCore-Einheit hat Plex nie unterstützt. Transcoding
  läuft also über die CPU, und dafür reicht weder ein Pi 4 noch ein Pi 5.
- Der Pi 5 ist hier sogar schlechter als der Pi 4, weil Broadcom den
  Hardware-H.264-Encoder gestrichen hat. Der Pi 5 dekodiert HEVC, encodiert
  aber nichts mehr in Hardware.
- **SABnzbd ist CPU-hungrig**, weil par2-Prüfung und Entpacken rechnen. Ein
  Pi 5 schafft realistisch 20 bis 40 MB/s. Eine Gigabit-Leitung reizt du damit
  nicht aus.
- Eine SD-Karte überlebt Docker mit zwanzig Containern nicht. Es braucht
  USB-SSD oder NVMe per HAT, und damit ist der Preisvorteil weg.

Für dieses Setup ist der Punkt entscheidend, weil unter den Clients ein
Samsung- oder LG-Fernseher und Browser sind. Beide lösen regelmäßig
Transcoding aus: der TV wegen lückenhafter Format-Unterstützung, der Browser
weil er HEVC oft nicht abspielt und unterwegs die Bandbreite angepasst werden
muss. Mit Apple TV allein wäre ein Pi vertretbar gewesen, mit diesen drei
Clients nicht.

## Was stattdessen

Ein gebrauchter Business-Mini-PC. Das ist für diesen Zweck nicht nur besser
als ein Pi, sondern billiger.

| Modell | CPU | Preis gebraucht | Bemerkung |
|---|---|---|---|
| Lenovo ThinkCentre M720q / M920q | i5-8500T / i5-9500T | 80 bis 180 CHF | beste Verfügbarkeit, 1x NVMe + 1x SATA 2.5" |
| Dell OptiPlex 3060 / 5060 Micro | i5-8500T | 80 bis 170 CHF | gleichwertig, Netzteil extern |
| HP EliteDesk 800 G4 Mini | i5-8500T | 90 bis 180 CHF | leise, gute Kühlung |
| Neuer N100/N150-Mini | N100 | 140 bis 200 CHF | sparsamster Verbrauch, aber meist nur 1x NVMe |

Worauf es ankommt:

- **Intel-CPU ab der 8. Generation.** Damit ist QuickSync mit HEVC-10-Bit und
  HDR-Tonemapping dabei. Ältere Generationen können HEVC nur teilweise.
- **16 GB RAM.** Bei zwanzig Containern plus Plex-Transcoding ist 8 GB knapp,
  und RAM ist bei diesen Geräten billig nachzurüsten (SO-DIMM).
- **NVMe für System und Configs, SATA für Medien.** Die Configs schreiben
  ständig (Datenbanken), die Medien brauchen Platz. Das M720q-Gehäuse nimmt
  beides auf.
- **T-Suffix bei der CPU** (8500T statt 8500) bedeutet 35 Watt TDP. Im
  Leerlauf landet so ein Gerät bei 8 bis 15 Watt, was bei Dauerbetrieb den
  Unterschied macht.

Bei einem i5-8500T mit QuickSync sind vier bis fünf gleichzeitige
1080p-Transcodes kein Problem, und 4K-HDR auf 1080p SDR läuft mit
Tonemapping ebenfalls.

## Verbrauch und Kosten

| Gerät | Leerlauf | Strom pro Jahr bei 0.30 CHF/kWh |
|---|---|---|
| Pi 5 mit NVMe | ca. 5 W | ca. 13 CHF |
| N100-Mini | ca. 8 W | ca. 21 CHF |
| i5-8500T Tiny | ca. 12 W | ca. 32 CHF |

Die Differenz zwischen Pi und Mini-PC liegt bei rund 20 CHF im Jahr. Dafür
bekommst du Transcoding, das überhaupt funktioniert.

## Wenn der Pi schon da ist

Wegwerfen muss man ihn nicht. Zwei sinnvolle Rollen:

- **Zweiter Uptime-Kuma-Knoten**, der von außerhalb prüft, ob der Mediaserver
  erreichbar ist. Ein Monitoring, das auf derselben Maschine läuft wie der
  überwachte Dienst, meldet dessen Ausfall naturgemäß nicht.
- **Off-Site-Backup-Ziel** für `config/`, an einem anderen Ort im Haus oder bei
  Verwandten über Tailscale.

## Plattenplanung

Für den Start reicht eine Platte, das ist die getroffene Entscheidung. Zwei
Dinge trotzdem gleich mitdenken:

- **Kaufe CMR, nicht SMR.** SMR-Platten (bei WD Red häufig, bei Seagate
  Barracuda ebenfalls) brechen bei Schreiblast dramatisch ein und sind für
  Downloads plus Seeding die falsche Wahl. WD Red **Plus** und Seagate IronWolf
  sind CMR, WD Red ohne Plus ist es nicht.
- **Lass einen SATA-Port frei.** Wenn später mergerfs und SnapRAID dazukommen
  sollen, brauchst du mindestens eine zweite Platte für die Parity. Das
  M720q-Gehäuse nimmt nur eine 2.5"-Platte, für 3.5"-Platten braucht es ein
  externes Gehäuse oder ein anderes Modell.

Der Umstieg von einer Platte auf einen mergerfs-Pool ist später ohne
Neusortieren möglich, weil die bestehende Platte einfach der erste Pool-Member
wird. Deshalb ist die Startentscheidung nicht endgültig.
