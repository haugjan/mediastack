# 9. Zugriff vom Handy und von unterwegs

## Die kurze Antwort

**Paperless ist nicht öffentlich, und das war es nie.** Genau dafür ist
Tailscale in diesem Setup da. Vom Handy läuft es so:

1. Tailscale-App installieren, mit demselben Konto anmelden wie auf dem Server
2. `https://paperless.example.com` im Browser öffnen

Das ist alles. Es funktioniert überall, im Mobilfunknetz genauso wie im
fremden WLAN, mit einem echten Let's-Encrypt-Zertifikat und ohne
Browserwarnung. Am Router ist **kein einziger Port** dafür offen.

## Warum das dicht ist

Die Kette hat drei Glieder, und jedes einzelne würde schon genügen:

| Glied | Wirkung |
|---|---|
| **DNS** | `paperless.example.com` löst auf `100.x.y.z` auf, eine Tailnet-Adresse. Die ist aus dem Internet grundsätzlich nicht routbar. |
| **Listener** | Caddy lauscht für diesen Namen per `bind` ausschließlich auf dem `tailscale0`-Interface. Am öffentlichen Interface existiert er nicht. |
| **Router** | Weitergeleitet sind nur 80 und 443, und dort antwortet nur `requests.example.com`. |

Das zweite Glied ist der wichtige. Ein DNS-Eintrag allein wäre zu wenig: wer
deine Heim-Adresse kennt, könnte sich zu Port 443 verbinden und
`Host: paperless.example.com` mitschicken. Weil Caddy für diesen Namen dort
aber gar nicht lauscht, landet so eine Anfrage nirgends.

Gegenprobe von einem Gerät ohne Tailscale, etwa über Mobilfunk mit
abgeschaltetem VPN:

```bash
curl -sS --resolve paperless.example.com:443:<deine-oeffentliche-IP> \
  https://paperless.example.com/
```

Das muss in einen Verbindungsfehler laufen. Kommt eine Paperless-Seite, stimmt
etwas nicht, dann `TAILSCALE_IP` in der `.env` und `network_mode: host` beim
Caddy-Container prüfen.

## Im Heimnetz ohne Tailscale

Nicht auf jedem Gerät läuft Tailscale: Fernseher, Spielkonsole, der Laptop
von Besuch. Für die gibt es dieselben Dienste ein zweites Mal, unter eigenen
Namen und auf einem eigenen Port:

```
https://home.lan.example.com:8443
https://paperless.lan.example.com:8443
https://sonarr.lan.example.com:8443
```

Das Muster ist immer `<name>.lan.<domain>:8443`, mit denselben Namen wie
oben. Echtes Zertifikat, keine Browserwarnung, kein VPN.

**Warum ein anderer Port, und nicht einfach Port 443?** Weil der Router 80
und 443 auf genau diesen Rechner weiterleitet. Läge der Heimnetz-Zugang auf
443, wäre er über die Portweiterleitung auch aus dem Internet erreichbar —
der Listener existiert dann ja. 8443 leitet der Router nicht weiter, damit
bleibt es bei zwei Riegeln. Genau das prüft auch die CI: verschiebt jemand
den Block auf 443, schlägt sie an.

Der zweite Riegel ist hier enger gefasst als bei den Tailscale-Namen:
durchgelassen wird nur dein eigenes Subnetz, nicht jeder private
Adressbereich. Das zählt, sobald der Rechner ein Notebook ist, das auch mal
in einem Café-WLAN steht — dort gilt `10.x` oder `192.168.x` genauso, und
ohne diese Einschränkung käme die halbe Kaffeehaus-Gesellschaft an dein
Dokumentenarchiv.

Aus demselben Grund bindet Caddy diesen Zugang an **keine feste Adresse**.
Täte es das, würde der ganze Proxy nicht mehr starten, sobald das Notebook in
einem anderen Netz hängt: die Adresse gibt es dann nicht. So lauscht er
überall und antwortet außerhalb deines Netzes mit 403.

Einrichten musst du nichts, `setup.sh` legt beides an — den DNS-Eintrag
`*.lan` auf die Adresse des Servers und den passenden Block in Caddy.

Zwei Dinge, die dabei schiefgehen können:

**Die Adresse des Servers muss bleiben.** Der Eintrag zeigt auf die
LAN-Adresse, die der Server beim Einrichten hatte. Vergib im Router eine
feste Zuordnung (DHCP-Reservierung), sonst zeigt der Eintrag nach einem
Neustart ins Leere, und ein erneutes `sudo ./setup.sh` muss ihn richtigstellen.

Läufst du `setup.sh` unterwegs, merkt es das: Hat sich die lokale Adresse seit
dem letzten Lauf geändert, fragt es nach, ob das die Heimnetz-Adresse ist.
Sagst du nein, bleiben Adresse, Subnetz und der `*.lan`-Eintrag unangetastet.

**Der DNS-Rebind-Schutz.** Derselbe Filter wie unten beim CGNAT-Bereich, nur
trifft er hier die privaten Adressen `192.168.x` oder `10.x`. Löst
`home.lan.example.com` ins Nichts auf, obwohl der Eintrag stimmt, braucht
deine Domain in der Router-Oberfläche eine Ausnahme.

### Die Alternative: eine statische Route

Wer keinen zweiten Satz Namen will, setzt im Router stattdessen eine statische
Route auf die **Tailscale-Adresse des Servers**. Dann erreichen auch Geräte
ohne Tailscale die ganz normalen Namen, und an der Konfiguration des Stacks
ändert sich nichts.

Bei einer FritzBox unter *Heimnetz → Netzwerk → Netzwerkeinstellungen →
Statische Routingtabelle → IPv4-Routen*:

| Feld | Wert | Beispiel |
|---|---|---|
| Netzwerk | die Tailscale-Adresse des Servers | `100.93.123.100` |
| Subnetzmaske | `255.255.255.255` | |
| Gateway | die LAN-Adresse des Servers | `192.168.178.98` |

**Nur diese eine Adresse, nicht `100.64.0.0/10`.** Eine Route über den ganzen
Tailnet-Bereich schickt allen Tailnet-Verkehr zum Server, und der leitet nicht
weiter — die übrigen Geräte im Tailnet wären vom Heimnetz aus dann gar nicht
mehr erreichbar.

Zwei Dinge lassen die Route ins Leere zeigen: eine neue LAN-Adresse des
Servers (deshalb im Router eine feste Zuordnung vergeben) und eine neue
Tailscale-Adresse nach einer Neuanmeldung. Beides fällt sofort auf, weil dann
im Heimnetz gar nichts mehr geht, unterwegs aber alles.

## Apps, die sich lohnen

| Dienst | Android | iOS |
|---|---|---|
| Paperless | **Paperless Mobile** (F-Droid, Play Store) | Weboberfläche als PWA |
| Musik | **Symfonium** (ca. 5 EUR) | **Amperfy** oder **Plexamp** |
| Hörbücher | **Audiobookshelf** | **Audiobookshelf** |
| Filme und Serien | **Plex** | **Plex** |
| Wünsche | Overseerr als PWA | Overseerr als PWA |

Paperless-ngx und Overseerr sind beide PWAs. Im Browser öffnen, dann
**Zum Startbildschirm hinzufügen**, und sie verhalten sich wie eine App,
inklusive eigenem Symbol und Vollbild.

Für Paperless am Handy ist der praktischste Weg allerdings gar keine App: du
legst Scans einfach in den OneDrive-Ordner `Scans`, und fünf Minuten später
sind sie mit OCR im Archiv. Siehe [docs/06](06-paperless-onedrive.md). Die
Weboberfläche brauchst du dann nur zum Suchen.

## Tailscale auf dem Handy, was du wissen solltest

**Es bleibt an.** Auf Android läuft Tailscale als dauerhafter VPN-Dienst, auf
iOS als Network Extension. Einrichten und vergessen.

**Es kostet praktisch keinen Akku,** solange du nichts überträgst. Tailscale
baut nur bei Bedarf Verbindungen auf und hält sonst eine sehr sparsame
Verbindung zum Koordinationsserver.

**Es leitet nicht deinen ganzen Verkehr um.** Standardmäßig gehen nur Adressen
im Tailnet durch den Tunnel, alles andere läuft normal. Dein übriges
Surfverhalten ändert sich nicht, und die Geschwindigkeit auch nicht.

**Android Auto und CarPlay funktionieren trotzdem,** weil dort die App auf dem
Handy läuft und nicht das Auto selbst eine Verbindung aufbaut. Im Auto ist
aber ohnehin Offline-Caching die richtige Antwort, siehe
[docs/08](08-musik.md).

## Der eine Fallstrick: DNS-Rebinding-Schutz

Es gibt eine Stelle, an der es klemmen kann, und sie ist gut versteckt.

Tailnet-Adressen liegen in `100.64.0.0/10`, dem Bereich für Carrier-Grade
NAT. **Manche Router und öffentliche Resolver filtern DNS-Antworten mit
privaten oder CGNAT-Adressen heraus**, als Schutz vor DNS-Rebinding-Angriffen.
Dann löst `paperless.example.com` ins Nichts auf, obwohl der Eintrag korrekt
ist.

Typisch dafür: es funktioniert im Mobilfunknetz einwandfrei und scheitert
genau im eigenen WLAN zu Hause. Das ist ein verwirrendes Fehlerbild, weil man
zu Hause zuerst testet.

Zwei Lösungen:

**A: Tailscale die DNS-Auflösung übernehmen lassen.** In der
[Tailscale-Admin-Konsole](https://login.tailscale.com/admin/dns) unter
**Nameservers** einen Resolver hinterlegen, etwa `1.1.1.1`, und
**Override local DNS** aktivieren. Danach fragt jedes Gerät im Tailnet an
deinem Router vorbei und der Filter greift nicht mehr. Das ist die
pflegeleichte Variante.

**B: Split DNS.** In derselben Ansicht unter **Split DNS** nur `example.com`
einem bestimmten Resolver zuweisen. Feiner dosiert, weil der übrige
DNS-Verkehr unangetastet bleibt.

Prüfen, ob es dich betrifft:

```bash
nslookup paperless.example.com          # muss 100.x.y.z zurueckgeben
nslookup paperless.example.com 1.1.1.1  # Gegenprobe an einem Resolver ohne Filter
```

Liefert die erste Zeile nichts und die zweite die Tailnet-Adresse, ist es
genau dieser Filter.

## Alternative ganz ohne eigene Domain

Wenn du keine Domain bei Azure hast oder den Schritt überspringen willst,
bietet Tailscale selbst Namen und Zertifikate an. In der Admin-Konsole
**HTTPS Certificates** aktivieren, dann auf dem Server:

```bash
sudo tailscale cert "$(tailscale status --json | python3 -c 'import json,sys; print(json.load(sys.stdin)["Self"]["DNSName"].rstrip("."))')"
```

Danach ist der Server unter `<rechnername>.<tailnet>.ts.net` mit gültigem
Zertifikat erreichbar. Du brauchst dafür kein DNS, keine Azure-Zone und keinen
Service Principal, bekommst aber auch keine hübschen Namen pro Dienst, sondern
arbeitest weiter mit Portnummern.

**Nicht verwenden: Tailscale Funnel.** Das stellt einen Dienst absichtlich ins
öffentliche Internet und ist genau das Gegenteil von dem, was hier gebaut ist.
Für Paperless wäre es die falsche Entscheidung.

## Wer außer dir Zugriff braucht

Tailscale ist für dich und deine Geräte. Für andere Menschen gibt es zwei
Wege, und der zweite ist meistens der bessere:

- **Ins Tailnet einladen.** In der Admin-Konsole unter **Users** teilen, oder
  einen einzelnen Rechner per **Share** freigeben. Sinnvoll für den Haushalt
  oder jemanden, der ohnehin Technik mag.
- **Gar keinen Zugriff geben.** Familie und Freunde brauchen in der Praxis nur
  zwei Dinge: Wünsche eintragen und Filme schauen. Das erste löst Overseerr
  unter `requests.example.com`, das zweite Plex über seine eigenen Server.
  Beides ohne Tailscale, ohne Konto bei dir und ohne dass sie irgendetwas
  installieren.

Paperless gehört in keinem Fall dazu.
