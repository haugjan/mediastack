# 7. Subdomains, Zertifikate und Azure DNS

Liegt deine DNS-Zone bei Azure DNS, erlaubt das eine Lösung, die sonst nicht
geht: **echte, browservertraute Zertifikate für Dienste, die aus dem Internet
überhaupt nicht erreichbar sind.**

> In diesem Dokument steht überall `example.com`. Setze dort deine eigene
> Domain ein. Sie wird **nirgends im Repo hinterlegt**, sondern ausschließlich
> in deiner lokalen `.env` unter `BASE_DOMAIN`, und die ist von Git
> ausgeschlossen. `setup.sh` fragt in Schritt 5 danach.
>
> Prüfen, ob deine Zone überhaupt bei Azure liegt:
> `nslookup -type=NS example.com` muss `*.azure-dns.*` zurückgeben.

## Warum DNS-01 und nicht der übliche Weg

Der normale Weg zu einem Let's-Encrypt-Zertifikat ist die HTTP-01-Challenge:
Let's Encrypt ruft `http://dienst.example.com/.well-known/...` auf und will
eine Antwort. Das setzt voraus, dass der Dienst aus dem Internet erreichbar
ist. Genau das willst du für Paperless nicht.

Die DNS-01-Challenge dreht das um: Caddy setzt einen TXT-Record in deiner
Zone, Let's Encrypt liest ihn dort. Es muss gar nichts erreichbar sein. Als
Nebeneffekt bekommst du ein **Wildcard-Zertifikat für `*.example.com`**, also
ein Zertifikat für alle Subdomains auf einmal.

## Die zwei Riegel

Ein A-Record auf die Tailscale-IP allein reicht **nicht**. Wer deine Heim-IP
kennt, kann sich zu Port 443 verbinden und `Host: paperless.example.com`
schicken. Caddy hätte das passende Zertifikat und würde ausliefern.

Deshalb zwei Ebenen:

1. **`bind` in der Caddyfile.** Die privaten Sites sind an
   `{$TAILSCALE_IP}` gebunden. Am öffentlichen Interface existiert für diese
   Namen schlicht kein Listener. Dafür läuft Caddy im Host-Netz, sonst sähe
   es das `tailscale0`-Interface gar nicht.
2. **`remote_ip`-Prüfung** als zweiter Riegel: nur `100.64.0.0/10` (das
   Tailnet) und die privaten RFC-1918-Bereiche kommen durch.

Ohne Punkt 1 wäre Punkt 2 allein riskant, weil je nach Docker-Konfiguration
die Quell-IP durch den `docker-proxy` verloren geht und dann alles wie
internes LAN aussieht. Das würde im Zweifel offen statt geschlossen
scheitern, und genau das darf bei Dokumenten nicht passieren.

## Schritt 1: Service Principal anlegen

Er darf genau eines: Records in dieser einen Zone ändern. Kein Zugriff auf
die Subscription, keine anderen Ressourcen.

```bash
# Anmelden (interaktiv, mit dem Konto, dem die Zone gehoert)
az login

SUB=$(az account show --query id -o tsv)
RG=$(az network dns zone list --query "[?name=='example.com'].resourceGroup" -o tsv)
echo "Subscription: $SUB / Resource Group: $RG"

az ad sp create-for-rbac \
  --name "caddy-mediastack-dns" \
  --role "DNS Zone Contributor" \
  --scopes "/subscriptions/$SUB/resourceGroups/$RG/providers/Microsoft.Network/dnszones/example.com"
```

Die Ausgabe liefert `appId`, `password` und `tenant`. Diese Zuordnung in die
`.env`:

| Ausgabe | `.env` |
|---|---|
| `appId` | `AZURE_CLIENT_ID` |
| `password` | `AZURE_CLIENT_SECRET` |
| `tenant` | `AZURE_TENANT_ID` |
| `$SUB` von oben | `AZURE_SUBSCRIPTION_ID` |
| `$RG` von oben | `AZURE_RESOURCE_GROUP_NAME` |

Das `password` wird genau einmal angezeigt. Gleich in den Passwortmanager.

## Schritt 2: DNS-Records setzen

Zwei Records, und die Reihenfolge der Spezifität erledigt den Rest: ein
expliziter Record schlägt im DNS immer den Wildcard.

```bash
TS_IP=$(tailscale ip -4 | head -1)
PUBLIC_IP=$(curl -s https://ipinfo.io/ip)

# Alles Private: Wildcard auf die Tailscale-Adresse
az network dns record-set a add-record \
  -g "$RG" -z example.com -n "*" -a "$TS_IP"

# Die eine oeffentliche Ausnahme
az network dns record-set a add-record \
  -g "$RG" -z example.com -n "requests" -a "$PUBLIC_IP"
```

Prüfen:

```bash
dig +short paperless.example.com    # -> 100.x.y.z
dig +short requests.example.com     # -> deine oeffentliche IP
```

## Schritt 3: Wechselnde Heim-IP

Die meisten Schweizer Anschlüsse haben keine feste IPv4. Dann zeigt
`requests.example.com` irgendwann ins Leere. Zwei Wege:

**A: CNAME auf einen DynDNS-Namen.** Dein Router kann vermutlich DynDNS bei
einem Anbieter aktualisieren. Dann einmalig:

```bash
az network dns record-set cname set-record \
  -g "$RG" -z example.com -n "requests" -c "deinname.ddns.net"
```

Danach ist es nicht mehr dein Problem. Das ist die pflegeleichteste Variante.

**B: Ein kleiner Timer, der den A-Record nachführt.** Mehr Kontrolle, aber
noch ein Dienst, der laufen muss. Nur sinnvoll, wenn dein Router kein DynDNS
kann.

Falls du IPv6 hast und dein Anschluss ein stabiles Präfix liefert, ist ein
AAAA-Record oft die stabilere Antwort als beides.

## Schritt 4: Portweiterleitung

Am Router **ausschließlich**:

| Extern | Intern | Wofür |
|---|---|---|
| 80/tcp | Server:80 | Weiterleitung auf HTTPS |
| 443/tcp | Server:443 | Overseerr |

Nichts sonst. Kein 32400 für Plex, das löst Plex über seine eigenen Server.
Kein 8989, kein 8000. Jeder zusätzlich geöffnete Port ist eine Zeile, die du
später erklären musst.

## Schritt 5: Testen

Beim ersten Start solltest du gegen das Let's-Encrypt-Staging gehen, sonst
brennst du bei einem Konfigurationsfehler schnell das Rate-Limit ab. Dafür
die Zeile `acme_ca` in der `Caddyfile` einkommentieren, testen, wieder
auskommentieren und die Zertifikate wegwerfen:

```bash
docker compose restart caddy
docker compose logs -f caddy      # muss "certificate obtained successfully" zeigen

# Nach erfolgreichem Test auf Produktion umstellen:
docker compose down caddy
sudo rm -rf config/caddy/data/caddy/certificates
docker compose up -d caddy
```

Gegenprobe, dass die Trennung wirklich sitzt. Von einem Gerät **außerhalb**
des Tailnets, zum Beispiel über Mobilfunk mit ausgeschaltetem Tailscale:

```bash
curl -sS --resolve paperless.example.com:443:<deine-oeffentliche-IP> \
  https://paperless.example.com/
```

Das muss in einen Verbindungsfehler laufen. Kommt stattdessen eine
Paperless-Seite, ist `bind` nicht aktiv und du solltest `TAILSCALE_IP` in der
`.env` sowie `network_mode: host` beim Caddy-Container prüfen.

## Übersicht der Namen

| Name | Ziel | Erreichbar |
|---|---|---|
| `requests.example.com` | Overseerr | **öffentlich** |
| `paperless.example.com` | Paperless-ngx | Tailnet |
| `music.example.com` | Navidrome | Tailnet |
| `books.example.com` | Audiobookshelf | Tailnet |
| `home.example.com` | Homepage | Tailnet |
| `sonarr` `radarr` `lidarr` `prowlarr` `bazarr` | *arr-Apps | Tailnet |
| `qbit` `sab` | Download-Clients | Tailnet |
| `status` `disks` `clean` `stats` | Betrieb | Tailnet |

Plex fehlt hier bewusst. Ein Reverse Proxy davor bringt nichts, weil Plex
seinen Fernzugriff selbst löst, und er kann Direct Play stören.
