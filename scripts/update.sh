#!/usr/bin/env bash
#
#   ./scripts/update.sh
#
# Holt die neueste veroeffentlichte Version und legt sie ueber die
# bestehende Installation.
#
# Deine Daten bleiben unberuehrt, und zwar nicht durch Ausnahmeregeln,
# sondern weil .env und config/ im Release-Archiv gar nicht enthalten sind.
# Ein einfaches Ueberkopieren kann sie deshalb nicht treffen.
#
# Bei einem oeffentlichen Repo ist kein Token noetig. Ist das Repo privat,
# traegst du in der .env GITHUB_TOKEN ein (Contents Read-only genuegt), dann
# steht er dort mit Rechten 640 und nicht in der Shell-Historie.
#
# setup.sh ruft das Skript bei jedem Start mit --from-setup auf. Dann ist
# keine .env noetig, es gibt keine Hinweise am Ende, und der Exit-Code sagt
# setup.sh, wie es weitergeht:
#   0 = aktualisiert, 2 = schon aktuell, 1 = nicht geprueft (nichts geaendert),
#   3 = beim Kopieren abgebrochen (Installation womoeglich halb neu)
#
set -uo pipefail

# Alles steht in einem { }-Block. bash liest Skripte stueckweise, und dieses
# Skript ueberschreibt sich unten selbst. Den Block liest bash vollstaendig,
# bevor er laeuft, danach wird nichts mehr aus der Datei gelesen.
{
FROM_SETUP=0
[[ ${1:-} == --from-setup ]] && FROM_SETUP=1

REPO_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_DIR" || exit 1

if [[ -t 1 ]]; then
	G=$'\033[32m'; Y=$'\033[33m'; R=$'\033[31m'; B=$'\033[1m'; N=$'\033[0m'
else
	G=; Y=; R=; B=; N=
fi
ok()   { printf '  %s✓%s %s\n' "$G" "$N" "$*"; }
info() { printf '  · %s\n' "$*"; }
note() { printf '  %s!%s %s\n' "$Y" "$N" "$*"; }
die()  { printf '\n%sAbbruch:%s %s\n\n' "$R" "$N" "$*" >&2; exit 1; }

# Ein Git-Checkout wuerde vom Release-Archiv ueberschrieben, samt
# ungesicherter Aenderungen. Dort gilt git pull.
[[ ! -d .git ]] || die "Das ist ein Git-Checkout. Aktualisieren mit:  git pull"

if [[ -f .env ]]; then
	# shellcheck source=scripts/lib-env.sh
	source ./scripts/lib-env.sh
	load_env ./.env
elif (( ! FROM_SETUP )); then
	die "Keine .env gefunden. Erst installieren: sudo ./setup.sh"
fi

GITHUB_REPO="${GITHUB_REPO:-haugjan/mediastack}"

# Der Token ist optional. Bei einem oeffentlichen Repo braucht es ihn nicht.
# Sinnvoll ist er nur, wenn das Repo privat ist oder du in das
# Abfragelimit von GitHub laeufst (60 Anfragen pro Stunde ohne Anmeldung).
AUTH=()
if [[ -n ${GITHUB_TOKEN:-} ]]; then
	AUTH=(-H "Authorization: Bearer ${GITHUB_TOKEN}")
	info "mit Token aus der .env"
fi

api() {
	curl -fsSL "${AUTH[@]}" \
		-H "Accept: application/vnd.github+json" \
		-H "X-GitHub-Api-Version: 2022-11-28" \
		"https://api.github.com/$1"
}

(( FROM_SETUP )) || printf '\n%sMediastack aktualisieren%s\n' "$B" "$N"
HAVE="$( [[ -f VERSION ]] && cat VERSION || echo "unbekannt" )"
info "installiert: $HAVE"

RELEASE="$(api "repos/${GITHUB_REPO}/releases/latest")" \
	|| die "Konnte das Release nicht abfragen.
     Haeufigste Ursachen: keine Internetverbindung, oder ${GITHUB_REPO}
     ist privat und in der .env fehlt ein GITHUB_TOKEN mit Contents-Read."

read -r TAG ASSET_URL <<<"$(printf '%s' "$RELEASE" | python3 -c "
import json, sys
r = json.load(sys.stdin)
asset = next((a for a in r.get('assets', []) if a['name'] == 'mediastack.tar.gz'), None)
if not asset:
    sys.exit('Im neuesten Release fehlt mediastack.tar.gz')
print(r['tag_name'], asset['url'])
")" || die "Antwort von GitHub nicht verstanden."

info "verfuegbar:  $TAG"

if [[ $HAVE == "$TAG" ]]; then
	ok "Schon aktuell, nichts zu tun."
	exit $(( FROM_SETUP ? 2 : 0 ))
fi

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

info "herunterladen"
curl -fsSL "${AUTH[@]}" \
	-H "Accept: application/octet-stream" \
	-o "$TMP/new.tar.gz" "$ASSET_URL" \
	|| die "Download fehlgeschlagen."

tar --no-same-owner -xzf "$TMP/new.tar.gz" -C "$TMP" || die "Archiv ist beschaedigt."
NEW="$TMP/mediastack"

# Gegenpruefen, bevor irgendetwas ueberschrieben wird.
[[ -x $NEW/setup.sh ]]   || die "Im Archiv fehlt ein ausfuehrbares setup.sh."
[[ -f $NEW/compose.yaml ]] || die "Im Archiv fehlt compose.yaml."
[[ ! -e $NEW/.env ]]     || die "Im Archiv liegt eine .env. Das darf nicht sein, Abbruch."
[[ ! -d $NEW/config ]]   || die "Im Archiv liegt config/. Das darf nicht sein, Abbruch."
bash -n "$NEW/setup.sh"  || die "setup.sh im Archiv ist fehlerhaft."
ok "Archiv geprueft ($(find "$NEW" -type f | wc -l) Dateien)"

# Die alte compose.yaml sichern, damit ein Vergleich moeglich bleibt.
cp -a compose.yaml "compose.yaml.vor-$HAVE" 2>/dev/null \
	&& info "alte compose.yaml als compose.yaml.vor-$HAVE gesichert"

# Ueberkopieren. .env und config/ sind im Archiv nicht enthalten und
# koennen deshalb nicht getroffen werden.
cp -a "$NEW/." . || {
	printf '\n%sAbbruch:%s Kopieren fehlgeschlagen. Rechte pruefen.\n\n' "$R" "$N" >&2
	exit 3; }
ok "Auf $TAG aktualisiert"

# Ab v1.4 hat jeder Dienst sein eigenes Profil, und COMPOSE_PROFILES ist die
# Liste dessen, was laufen soll. Eine aeltere .env nennt dort nur die
# optionalen Bereiche, weil die Kerndienste damals ohne Profil liefen. Ohne
# diese Ergaenzung startet nach dem Update kein Plex, kein Sonarr, nichts.
if [[ -f .env ]] && ! grep -q '^COMPOSE_PROFILES=.*\bplex\b' .env; then
	alt="$(sed -n 's/^COMPOSE_PROFILES=//p' .env | head -1)"
	neu="plex,sonarr,radarr,lidarr,prowlarr,bazarr,navidrome,audiobookshelf"
	neu="$neu,overseerr,tautulli,homepage,uptime,cleanuparr,unpackerr"
	neu="$neu,recyclarr,backfill"
	# Was vorher schon lief, bleibt. "extras" gibt es nicht mehr, es war
	# Kometa und Scrutiny.
	for p in torrent usenet docs proxy radio; do
		[[ ,$alt, == *,$p,* ]] && neu="$neu,$p"
	done
	[[ ,$alt, == *,extras,* ]] && neu="$neu,kometa,scrutiny"
	sed -i "s|^COMPOSE_PROFILES=.*|COMPOSE_PROFILES=$neu|" .env
	ok "Profilliste auf das neue Format gebracht (jeder Dienst einzeln)"
	info "Abwaehlen geht ab jetzt in der Auswahl von sudo ./setup.sh"
fi

(( FROM_SETUP )) && exit 0

if [[ -f homepage/services.yaml ]]; then
	note "homepage/services.yaml wird von setup.sh neu erzeugt"
fi

cat <<EOF

  ${B}Weiter${N}
    1. Aenderungen ansehen:  diff compose.yaml.vor-$HAVE compose.yaml
    2. Uebernehmen:          sudo ./setup.sh
       (ergaenzt nur, fragt nichts erneut, was schon in der .env steht)
    3. Container erneuern:   docker compose up -d

EOF
exit 0
}
