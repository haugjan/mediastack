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
