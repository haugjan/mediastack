#!/usr/bin/env bash
#
#   sudo ./setup.sh
#
# Das ist alles. Das Skript fragt in einfachem Deutsch nach, was es wissen
# muss, installiert was fehlt, und startet nur die Teile, fuer die du die
# Zugaenge schon hast.
#
# Du kannst es jederzeit erneut starten. Es merkt sich alles in der .env,
# fragt nur nach, was noch fehlt, und macht nichts doppelt. Wenn du also
# spaeter ein ProtonVPN-Abo hast: nochmal starten, Schluessel eingeben,
# fertig.
#
# Bricht ein optionaler Teil ab, laeuft der Rest trotzdem durch. Am Ende
# steht eine Liste, was nicht geklappt hat und wie du es nachholst.
# Alles Ausfuehrliche landet in setup.log.
#
# set -e ist ABSICHTLICH nicht gesetzt: ein fehlgeschlagener optionaler
# Schritt soll die Installation nicht abbrechen.
set -uo pipefail

REPO_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
LOG="$REPO_DIR/setup.log"
MEDIA_USER="media"
MEDIA_GROUP="media"
PROBLEMS=()

# Dienst -> Port, fuer das Dashboard und die Abschlussuebersicht.
declare -A PORT=(
	[plex]=32400 [navidrome]=4533 [audiobookshelf]=13378 [overseerr]=5055
	[paperless]=8000 [sonarr]=8989 [radarr]=7878 [lidarr]=8686
	[prowlarr]=9696 [bazarr]=6767 [qbittorrent]=8080 [sabnzbd]=8081
	[homepage]=3000 [uptime]=3001 [scrutiny]=8082 [cleanuparr]=11011
	[tautulli]=8181
)
# Dienst -> Subdomain, falls eine Domain eingerichtet ist.
declare -A SUB=(
	[navidrome]=music [audiobookshelf]=books [overseerr]=requests
	[paperless]=paperless [sonarr]=sonarr [radarr]=radarr [lidarr]=lidarr
	[prowlarr]=prowlarr [bazarr]=bazarr [qbittorrent]=qbit [sabnzbd]=sab
	[homepage]=home [uptime]=status [scrutiny]=disks [cleanuparr]=clean
	[tautulli]=stats
)

# --------------------------------------------------------------- Darstellung
if [[ -t 1 ]]; then
	G=$'\033[32m'; Y=$'\033[33m'; R=$'\033[31m'; B=$'\033[1m'; D=$'\033[2m'; N=$'\033[0m'
else
	G=; Y=; R=; B=; D=; N=
fi

step()  { printf '\n%s%s%s\n' "$B" "$*" "$N"; }
ok()    { printf '  %s✓%s %s\n' "$G" "$N" "$*"; }
info()  { printf '  %s·%s %s\n' "$D" "$N" "$*"; }
note()  { printf '  %s!%s %s\n' "$Y" "$N" "$*"; }
bad()   { printf '  %s✗%s %s\n' "$R" "$N" "$*"; }
problem() { PROBLEMS+=("$*"); bad "$*"; }
stop()  { printf '\n%sAbbruch:%s %s\n\n' "$R" "$N" "$*" >&2; exit 1; }

# Fuehrt einen Befehl aus, schreibt alles ins Log und bricht NICHT ab.
try() {
	local what="$1"; shift
	if "$@" >>"$LOG" 2>&1; then ok "$what"; return 0; fi
	problem "$what (Details in setup.log)"; return 1
}

# ------------------------------------------------------------------ Eingabe
# Immer von /dev/tty lesen, damit das auch in einer Pipe funktioniert.
ask() {
	local q="$1" def="${2:-}" a
	if [[ -n $def ]]; then printf '  %s [%s]: ' "$q" "$def" >&2
	else printf '  %s: ' "$q" >&2; fi
	read -r a </dev/tty || a=""
	echo "${a:-$def}"
}

ask_yn() {
	local q="$1" def="${2:-j}" a
	while :; do
		printf '  %s (j/n) [%s]: ' "$q" "$def" >&2
		read -r a </dev/tty || a=""
		a="${a:-$def}"
		case "${a,,}" in j|ja|y|yes) return 0 ;; n|nein|no) return 1 ;; esac
		printf '    Bitte j oder n.\n' >&2
	done
}

ask_secret() {
	local q="$1" a
	printf '  %s (wird nicht angezeigt): ' "$q" >&2
	read -rs a </dev/tty || a=""
	printf '\n' >&2
	echo "$a"
}

# Numerierte Auswahl. Liefert den gewaehlten Index (ab 1) auf stdout.
# Bei nur einem Eintrag wird er ohne Rueckfrage genommen.
ask_choice() {
	local prompt="$1"; shift
	local -a items=("$@")
	local i sel
	if (( ${#items[@]} == 1 )); then
		printf '    nur eine Moeglichkeit: %s\n' "${items[0]}" >&2
		echo 1; return 0
	fi
	for ((i = 0; i < ${#items[@]}; i++)); do
		printf '    %2d) %s\n' "$((i + 1))" "${items[i]}" >&2
	done
	while :; do
		printf '  %s [1-%d]: ' "$prompt" "${#items[@]}" >&2
		read -r sel </dev/tty || sel=""
		if [[ $sel =~ ^[0-9]+$ ]] && (( sel >= 1 && sel <= ${#items[@]} )); then
			echo "$sel"; return 0
		fi
		printf '    Bitte eine Zahl zwischen 1 und %d.\n' "${#items[@]}" >&2
	done
}

# ------------------------------------------------------------- .env-Zugriff
env_get() {
	[[ -f $REPO_DIR/.env ]] || return 1
	awk -F= -v k="$1" '$1==k {sub(/^[^=]*=/,""); print; exit}' "$REPO_DIR/.env"
}

env_set() {
	python3 - "$REPO_DIR/.env" "$1" "$2" <<'PY'
import sys, pathlib
path, key, val = pathlib.Path(sys.argv[1]), sys.argv[2], sys.argv[3]
lines = path.read_text(encoding="utf-8").splitlines(keepends=True) if path.exists() else []
out, done = [], False
for line in lines:
    if line.split("=", 1)[0].strip() == key and not line.lstrip().startswith("#"):
        out.append(f"{key}={val}\n"); done = True
    else:
        out.append(line)
if not done:
    if out and not out[-1].endswith("\n"):
        out.append("\n")
    out.append(f"{key}={val}\n")
path.write_text("".join(out), encoding="utf-8")
PY
}

# Fragt nur, wenn der Wert noch leer ist. Das macht Neustarts schmerzlos.
env_ask_if_empty() {
	local key="$1" prompt="$2" secret="${3:-0}" cur val
	cur="$(env_get "$key" 2>/dev/null || true)"
	if [[ -n $cur ]]; then
		if [[ $secret == 1 ]]; then info "$key ist gesetzt (${cur:0:4}...), bleibt unveraendert"
		else info "$key = $cur (bleibt)"; fi
		return 0
	fi
	if [[ $secret == 1 ]]; then val="$(ask_secret "$prompt")"; else val="$(ask "$prompt")"; fi
	[[ -n $val ]] && env_set "$key" "$val"
}

gen_secret() { LC_ALL=C tr -dc 'A-Za-z0-9' </dev/urandom 2>/dev/null | head -c "${1:-40}" || true; }

# =========================================================================
# 0. Start
# =========================================================================
: >"$LOG"
VERSION_STR=""
[[ -f $REPO_DIR/VERSION ]] && VERSION_STR=" $(cat "$REPO_DIR/VERSION")"
cat <<EOF

${B}Mediastack einrichten${VERSION_STR}${N}
${D}Plex, Serien, Filme, Musik, Hoerbuecher und Dokumente.
Log: $LOG${N}
EOF

[[ $EUID -eq 0 ]] || stop "Bitte mit sudo starten:  sudo ./setup.sh"
ADMIN_USER="${SUDO_USER:-root}"
command -v python3 >/dev/null || { apt-get update -qq; apt-get install -y -qq python3; }

# Vor allem anderen: gibt es ein neueres Release? Dann erst aktualisieren
# und gleich das neue setup.sh starten. Der { }-Block ist Absicht: bash
# liest ein Skript stueckweise, und diese Datei wird dabei ueberschrieben.
# Den Block liest bash vollstaendig, bevor er laeuft, und danach wird
# nichts mehr aus der alten Datei gelesen, weil exec sie ersetzt.
# Ein Git-Checkout bleibt unberuehrt, dort gilt git pull.
{
if [[ -n ${MEDIASTACK_UPDATED:-} ]]; then
	:
elif [[ -d $REPO_DIR/.git ]]; then
	info "Git-Checkout, keine Release-Pruefung (neuester Stand: git pull)"
else
	info "Suche nach einer neueren Version..."
	bash "$REPO_DIR/scripts/update.sh" --from-setup
	case $? in
		0) info "Starte die neue Version"
		   MEDIASTACK_UPDATED=1 exec bash "$REPO_DIR/setup.sh" "$@" ;;
		2) ;;
		3) stop "Aktualisierung unvollstaendig. Bitte sudo ./setup.sh erneut starten." ;;
		*) note "Keine Pruefung moeglich, es geht mit dieser Version weiter." ;;
	esac
fi
}

FIRST_RUN=1
[[ -f $REPO_DIR/.env ]] && FIRST_RUN=0
if (( FIRST_RUN )); then
	cp "$REPO_DIR/.env.example" "$REPO_DIR/.env" 2>/dev/null || : >"$REPO_DIR/.env"
	chmod 600 "$REPO_DIR/.env"
	info "Neue Installation"
else
	info "Bestehende Installation gefunden, es wird nur ergaenzt"
fi

# =========================================================================
# 1. System pruefen und Fehlendes nachinstallieren
# =========================================================================
step "1/9  System pruefen"

if [[ -r /etc/os-release ]]; then
	# shellcheck source=/dev/null
	. /etc/os-release
	ok "${PRETTY_NAME:-Linux} ($(uname -m))"
	# Linux Mint meldet einen eigenen Codenamen, den Docker nicht kennt.
	# Der passende Ubuntu-Name steht in UBUNTU_CODENAME. Genau hier
	# scheitern die meisten Docker-Anleitungen auf Mint.
	if [[ -n ${UBUNTU_CODENAME:-} ]]; then
		DOCKER_DISTRO=ubuntu; DOCKER_CODENAME="$UBUNTU_CODENAME"
	elif [[ ${ID_LIKE:-} == *debian* || ${ID:-} == debian ]]; then
		DOCKER_DISTRO=debian; DOCKER_CODENAME="${VERSION_CODENAME:-}"
	else
		DOCKER_DISTRO=; DOCKER_CODENAME=
	fi
else
	stop "/etc/os-release fehlt. Dieses Skript ist fuer Debian, Ubuntu und Mint."
fi

RAM_GB=$(awk '/MemTotal/ {printf "%.0f", $2/1024/1024}' /proc/meminfo)
if   (( RAM_GB >= 16 )); then ok "${RAM_GB} GB RAM, $(nproc) Kerne"
elif (( RAM_GB >= 8 ));  then note "${RAM_GB} GB RAM. Reicht, wird bei OCR und Transcoding gleichzeitig knapp."
else note "${RAM_GB} GB RAM ist wenig. Es laeuft, aber nicht alles gleichzeitig."
fi

info "Pakete nachinstallieren, das kann einen Moment dauern"
apt-get update -qq >>"$LOG" 2>&1
try "Grundpakete" apt-get install -y -qq ca-certificates curl gnupg git jq \
	smartmontools vainfo iproute2 rclone

if command -v docker >/dev/null 2>&1 && docker compose version >/dev/null 2>&1; then
	ok "Docker ist da ($(docker --version | cut -d, -f1))"
else
	if [[ -z $DOCKER_CODENAME ]]; then
		problem "Docker fehlt und die Distribution ist unbekannt. Bitte Docker manuell installieren."
	else
		info "Docker installieren ($DOCKER_DISTRO $DOCKER_CODENAME)"
		install -m 0755 -d /etc/apt/keyrings
		curl -fsSL "https://download.docker.com/linux/$DOCKER_DISTRO/gpg" \
			-o /etc/apt/keyrings/docker.asc >>"$LOG" 2>&1
		chmod a+r /etc/apt/keyrings/docker.asc
		echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.asc] https://download.docker.com/linux/$DOCKER_DISTRO $DOCKER_CODENAME stable" \
			>/etc/apt/sources.list.d/docker.list
		apt-get update -qq >>"$LOG" 2>&1
		try "Docker CE" apt-get install -y -qq docker-ce docker-ce-cli containerd.io \
			docker-buildx-plugin docker-compose-plugin
		systemctl enable --now docker >>"$LOG" 2>&1
	fi
fi
command -v docker >/dev/null 2>&1 || stop "Ohne Docker geht es nicht weiter. Siehe setup.log."

# Transcoding-Hardware
HAVE_GPU=0
if [[ -e /dev/dri/renderD128 ]]; then
	HAVE_GPU=1
	RENDER_GID=$(getent group render | cut -d: -f3 || echo 0)
	if vainfo 2>/dev/null | grep -q VAProfileHEVC; then
		ok "Grafikbeschleunigung nutzbar, Plex kann 4K umrechnen"
	else
		note "Grafikeinheit da, aber HEVC fehlt. Plex rechnet dann teils per CPU."
	fi
else
	RENDER_GID=0
	note "Keine nutzbare Grafikeinheit gefunden."
	info "Plex rechnet dann per CPU. Das reicht fuer Geraete, die alles direkt"
	info "abspielen (Apple TV, Shield), nicht aber fuer Smart-TV-Apps und Browser."
fi

# Desktop-Systeme schlafen ein, und ein schlafender Server ist keiner.
try "Ruhezustand abschalten" systemctl mask sleep.target suspend.target \
	hibernate.target hybrid-sleep.target

# Redis (fuer Paperless) forkt sich zum Sichern und braucht dafuer kurz die
# doppelte Speichermenge auf dem Papier. Ohne overcommit lehnt der Kernel das
# ab, und Redis meldet bei jedem Versuch "Background saving error".
if [[ $(sysctl -n vm.overcommit_memory 2>/dev/null) != 1 ]]; then
	echo 'vm.overcommit_memory = 1' >/etc/sysctl.d/60-mediastack-redis.conf
	sysctl -q -w vm.overcommit_memory=1 >>"$LOG" 2>&1
	ok "Speicherreservierung fuer Redis erlaubt"
fi
if [[ -d /proc/acpi/button/lid ]]; then
	mkdir -p /etc/systemd/logind.conf.d
	printf '[Login]\nHandleLidSwitch=ignore\nHandleLidSwitchExternalPower=ignore\nHandleLidSwitchDocked=ignore\n' \
		>/etc/systemd/logind.conf.d/99-mediastack.conf
	ok "Deckel schliessen schaltet nicht mehr ab"
fi

# =========================================================================
# 2. Wohin die Daten
# =========================================================================
step "2/9  Speicherort"

suggest_path() {
	local existing best="" bestfree=0 free mp
	existing="$(env_get DATA_ROOT 2>/dev/null || true)"
	if [[ -n $existing && -d $existing ]]; then echo "$existing"; return; fi
	[[ -d /mnt/data ]] && { echo /mnt/data; return; }
	# Groesste beschreibbare Partition nehmen.
	while read -r mp free; do
		[[ -w $mp ]] || continue
		(( free > bestfree )) && { bestfree=$free; best=$mp; }
	done < <(df -BG --output=target,avail 2>/dev/null | tail -n +2 \
		| grep -E '^/($|mnt|media|srv|home|var)' | tr -d 'G')
	if [[ -n $best ]]; then
		[[ $best == / ]] && echo /srv/mediastack || echo "$best/mediastack"
	else
		echo /srv/mediastack
	fi
}

echo "  Hier landen Filme, Serien, Musik und Dokumente. Es muss VIEL Platz sein"
echo "  und alles muss auf derselben Festplatte liegen."
df -h --output=target,size,avail 2>/dev/null | grep -E '^/($|mnt|media|srv|home)' \
	| sed 's/^/    /' | head -8
DATA_ROOT="$(ask "Speicherort" "$(suggest_path)")"
mkdir -p "$DATA_ROOT" 2>/dev/null || stop "Kann $DATA_ROOT nicht anlegen."

FREE_GB=$(df -BG --output=avail "$DATA_ROOT" 2>/dev/null | tail -1 | tr -dc '0-9')
if   (( ${FREE_GB:-0} >= 200 )); then ok "$DATA_ROOT, ${FREE_GB} GB frei"
elif (( ${FREE_GB:-0} >= 30 ));  then note "$DATA_ROOT, nur ${FREE_GB} GB frei. Fuer den Anfang ok."
else problem "$DATA_ROOT hat nur ${FREE_GB} GB frei. Das wird nichts."
fi

# Der wichtigste Test im ganzen Skript.
_a="$DATA_ROOT/.t1-$$"; _b="$DATA_ROOT/.t2-$$"
if echo x >"$_a" 2>/dev/null && ln "$_a" "$_b" 2>/dev/null; then
	ok "Speicherplatz ist geeignet (Hardlink-Test bestanden)"
	rm -f "$_a" "$_b"
else
	rm -f "$_a" "$_b"
	stop "Auf $DATA_ROOT funktionieren keine Hardlinks.
     Damit wuerde jeder fertige Download KOPIERT statt verlinkt: doppelter
     Platzverbrauch und das Weitergeben von Torrents endet sofort.
     Waehle einen Pfad auf einer normalen Linux-Festplatte (ext4, xfs, btrfs).
     NTFS, exFAT, FAT32, SMB- und NFS-Netzlaufwerke koennen das nicht."
fi

# =========================================================================
# 3. Benutzer und Ordner
# =========================================================================
step "3/9  Benutzer und Ordner"

getent group "$MEDIA_GROUP" >/dev/null || groupadd --system "$MEDIA_GROUP"
id "$MEDIA_USER" >/dev/null 2>&1 || useradd --system --gid "$MEDIA_GROUP" \
	--shell /usr/sbin/nologin --no-create-home "$MEDIA_USER"
PUID=$(id -u "$MEDIA_USER"); PGID=$(id -g "$MEDIA_USER")
ok "Dienstbenutzer $MEDIA_USER ($PUID:$PGID)"

if [[ $ADMIN_USER != root ]]; then
	usermod -aG docker,"$MEDIA_GROUP" "$ADMIN_USER" 2>>"$LOG"
	ok "$ADMIN_USER darf Docker und die Mediendateien nutzen"
fi

mkdir -p \
	"$DATA_ROOT"/torrents/{movies,tv,music,books} \
	"$DATA_ROOT"/usenet/incomplete \
	"$DATA_ROOT"/usenet/complete/{movies,tv,music,books} \
	"$DATA_ROOT"/media/{movies,tv,music,audiobooks,books} \
	"$DATA_ROOT"/paperless/{data,media,consume,export}
chown -R "$MEDIA_USER":"$MEDIA_GROUP" "$DATA_ROOT" 2>>"$LOG"
find "$DATA_ROOT" -type d -exec chmod 2775 {} + 2>>"$LOG"
ok "Ordnerstruktur angelegt"

# Muss VOR dem ersten Start existieren, sonst legt Docker die Ordner als
# root an und die Container duerfen nicht hineinschreiben.
mkdir -p "$REPO_DIR"/config/{gluetun,qbittorrent,sabnzbd,prowlarr,sonarr,radarr,lidarr,bazarr}
mkdir -p "$REPO_DIR"/config/{plex,navidrome,overseerr,tautulli,kometa,cleanuparr,uptime-kuma}
mkdir -p "$REPO_DIR"/config/{paperless-db,paperless-redis}
mkdir -p "$REPO_DIR"/config/audiobookshelf/{config,metadata}
mkdir -p "$REPO_DIR"/config/scrutiny/{config,influxdb}
mkdir -p "$REPO_DIR"/config/caddy/{data,config}
# Postgres und Redis NICHT mitnehmen. Beide bringen ihren eigenen Benutzer
# mit (uid 70 und 999) und richten ihren Ordner beim Start selbst ein. Ein
# chown auf media entzieht dem laufenden Dienst die Rechte an seinen eigenen
# Dateien: Postgres beantwortet danach JEDE Anfrage mit "could not open file
# ... Permission denied", Redis verweigert jeden Schreibbefehl. Der
# Healthcheck (pg_isready) prueft nur den Socket und meldet weiter "healthy",
# der Ausfall faellt also erst in Paperless auf.
find "$REPO_DIR/config" -mindepth 1 -maxdepth 1 \
	! -name paperless-db ! -name paperless-redis \
	-exec chown -R "$MEDIA_USER":"$MEDIA_GROUP" {} + 2>>"$LOG"
find "$REPO_DIR/config" -mindepth 1 -maxdepth 1 \
	! -name paperless-db ! -name paperless-redis \
	-exec chmod -R 775 {} + 2>>"$LOG"
chown "$MEDIA_USER":"$MEDIA_GROUP" "$REPO_DIR/config" 2>>"$LOG"
chmod 775 "$REPO_DIR/config" 2>>"$LOG"

# Recyclarr laeuft als media und legt unter recyclarr/ seinen Zustand ab
# (state/, cache/, repositories/). Gehoert der Ordner root, bricht jeder
# Lauf mit "Access to the path '/config/state' is denied" ab.
mkdir -p "$REPO_DIR/recyclarr"
chown -R "$MEDIA_USER":"$MEDIA_GROUP" "$REPO_DIR/recyclarr" 2>>"$LOG"
ok "Konfigurationsordner vorbereitet"

# =========================================================================
# 4. Fernzugriff
# =========================================================================
step "4/9  Zugriff von unterwegs"

echo "  Tailscale ist ein kostenloses privates Netz zwischen deinen Geraeten."
echo "  Damit erreichst du alles sicher von unterwegs, ohne am Router etwas"
echo "  zu oeffnen. Sehr empfohlen."
TS_IP="$(env_get TAILSCALE_IP 2>/dev/null || true)"
if command -v tailscale >/dev/null 2>&1 && [[ -n $(tailscale ip -4 2>/dev/null) ]]; then
	TS_IP="$(tailscale ip -4 | head -1)"
	ok "Tailscale ist schon verbunden ($TS_IP)"
elif ask_yn "Tailscale jetzt einrichten?" j; then
	command -v tailscale >/dev/null 2>&1 || \
		try "Tailscale installieren" bash -c 'curl -fsSL https://tailscale.com/install.sh | sh'
	if command -v tailscale >/dev/null 2>&1; then
		echo
		echo "  Jetzt oeffnet sich ein Link. Im Browser anmelden, dann geht es weiter."
		tailscale up --ssh </dev/tty || problem "Tailscale-Anmeldung abgebrochen"
		TS_IP="$(tailscale ip -4 2>/dev/null | head -1 || true)"
		[[ -n $TS_IP ]] && ok "Verbunden als $TS_IP"
	fi
else
	info "Uebersprungen. Spaeter: sudo tailscale up --ssh && sudo ./setup.sh"
fi

LAN_IP="$(ip -o -4 addr show scope global | awk '{print $4}' | cut -d/ -f1 | head -1)"
LAN_SUBNET="$(ip -o -4 route show to default | awk '{print $3}' | head -1 | sed 's/\.[0-9]*$/.0\/24/')"
ok "Lokale Adresse: ${LAN_IP:-unbekannt}"

# =========================================================================
# 5. Eigene Web-Adressen, vollautomatisch ueber Azure DNS
# =========================================================================
step "5/9  Eigene Web-Adressen (optional)"

USE_PROXY=0
BASE_DOMAIN="$(env_get BASE_DOMAIN 2>/dev/null || true)"

# Setzt einen A-Eintrag idempotent: erst loeschen, dann neu anlegen. Ein
# blosses add-record wuerde bei einem bestehenden Eintrag eine zweite
# Adresse daneben schreiben, und dann antwortet die Zone abwechselnd.
dns_set_a() {
	local name="$1" ip="$2"
	az network dns record-set a delete -g "$AZ_RG" -z "$BASE_DOMAIN" -n "$name" \
		--subscription "$AZ_SUB" -y --only-show-errors >>"$LOG" 2>&1 || true
	az network dns record-set a add-record -g "$AZ_RG" -z "$BASE_DOMAIN" -n "$name" \
		-a "$ip" --ttl 300 --subscription "$AZ_SUB" --only-show-errors >>"$LOG" 2>&1
}

# Anmeldung per Geraetecode, damit es auch per SSH ohne Browser geht.
# Bewusst NICHT ins Log umgeleitet: du musst Link und Code sehen.
# Die neuere az-Anmeldung fragt danach selbst nach Tenant und Subscription.
# Das schalten wir ab, gesucht wird ohnehin in allen Subscriptions.
azure_login() {
	echo
	echo "  Es erscheinen jetzt ein Link und ein Code. Link im Browser oeffnen,"
	echo "  Code eingeben, mit dem Konto anmelden, dem die DNS-Zone gehoert."
	echo
	AZURE_CORE_LOGIN_EXPERIENCE_V2=off az login --use-device-code --only-show-errors \
		>>"$LOG" || { problem "Azure-Anmeldung abgebrochen"; return 1; }
}

# Sucht in ALLEN Subscriptions aller Tenants, die das Konto sieht, nach
# DNS-Zonen. So muss niemand wissen, wo die Zone liegt.
azure_find_zones() {
	Z_NAMES=(); Z_RGS=(); Z_SUBS=(); Z_TENANTS=(); Z_LABELS=()
	local sub_id tenant sub_name zname zrg
	while IFS=$'\t' read -r sub_id tenant sub_name; do
		[[ -n $sub_id ]] || continue
		while IFS=$'\t' read -r zname zrg; do
			[[ -n $zname ]] || continue
			Z_NAMES+=("$zname"); Z_RGS+=("$zrg")
			Z_SUBS+=("$sub_id"); Z_TENANTS+=("$tenant")
			Z_LABELS+=("$(printf '%-24s (%s, Resource Group %s)' "$zname" "$sub_name" "$zrg")")
		done < <(az network dns zone list --subscription "$sub_id" \
			--query "[].[name, resourceGroup]" -o tsv --only-show-errors 2>>"$LOG")
	done < <(az account list --query "[?state=='Enabled'].[id, tenantId, name]" \
		-o tsv --only-show-errors 2>>"$LOG")
}

azure_dns_setup() {
	# ---------------------------------------------------------- Azure CLI
	if ! command -v az >/dev/null 2>&1; then
		info "Azure CLI fehlt, wird installiert (Download ca. 100 MB)"
		curl -sSL https://aka.ms/InstallAzureCLIDeb | bash >>"$LOG" 2>&1 \
			|| { problem "Azure CLI liess sich nicht installieren, siehe setup.log"; return 1; }
		ok "Azure CLI installiert"
	fi

	# ---------------------------------------------------------- Anmeldung
	if ! az account show >/dev/null 2>&1; then
		azure_login || return 1
	fi
	local who
	who="$(az account show --query user.name -o tsv 2>/dev/null)"
	ok "angemeldet als ${who:-unbekannt}"

	# ---------------------------------------------------------- DNS-Zonen
	info "Suche DNS-Zonen in allen Subscriptions..."
	azure_find_zones
	while ! (( ${#Z_NAMES[@]} )); do
		note "Das Konto ${who:-unbekannt} sieht keine DNS-Zone."
		if ask_yn "Mit einem anderen Azure-Konto anmelden?" j; then
			az logout --only-show-errors >>"$LOG" 2>&1 || true
			azure_login || return 1
			who="$(az account show --query user.name -o tsv 2>/dev/null)"
			ok "angemeldet als ${who:-unbekannt}"
			azure_find_zones
			continue
		fi
		info "Eine Zone anzulegen heisst auch, beim Registrar die Nameserver"
		info "umzustellen und auf die Uebernahme zu warten. Das automatisiere"
		info "ich bewusst nicht, dabei kann man sich die Domain abschiessen."
		return 1
	done
	echo "  Gefundene DNS-Zonen:"
	local zidx; zidx="$(ask_choice "Welche Domain willst du nutzen?" "${Z_LABELS[@]}")"
	zidx=$((zidx - 1))
	BASE_DOMAIN="${Z_NAMES[zidx]}"
	AZ_RG="${Z_RGS[zidx]}"
	AZ_SUB="${Z_SUBS[zidx]}"
	# Der Zugang unten muss im Tenant der Zone entstehen, nicht in dem,
	# der nach der Anmeldung zufaellig aktiv ist.
	az account set --subscription "$AZ_SUB" --only-show-errors >>"$LOG" 2>&1
	ok "Domain: $BASE_DOMAIN"

	# ------------------------------------------------- Service Principal
	# Caddy braucht Schreibrecht in der Zone, um fuer die DNS-01-Challenge
	# einen TXT-Eintrag zu setzen. Der Umfang ist genau diese eine Zone.
	local scope sp_name appid secret out tenant="${Z_TENANTS[zidx]}"
	scope="/subscriptions/${AZ_SUB}/resourceGroups/${AZ_RG}/providers/Microsoft.Network/dnszones/${BASE_DOMAIN}"
	sp_name="caddy-${BASE_DOMAIN//./-}-dns"
	appid="$(az ad sp list --display-name "$sp_name" --query "[0].appId" -o tsv \
		--only-show-errors 2>/dev/null)"

	if [[ -n $appid ]]; then
		info "Zugang '$sp_name' existiert bereits"
		secret="$(env_get AZURE_CLIENT_SECRET 2>/dev/null || true)"
		if [[ -z $secret ]] || ask_yn "Neues Passwort erzeugen? (das alte wird ungueltig)" n; then
			secret="$(az ad app credential reset --id "$appid" --query password -o tsv \
				--only-show-errors 2>>"$LOG")" \
				|| { problem "Neues Passwort konnte nicht erzeugt werden"; return 1; }
			ok "neues Passwort erzeugt"
		else
			info "bestehendes Passwort aus der .env wird weiterverwendet"
		fi
	else
		info "Lege einen Zugang an, der NUR in dieser Zone schreiben darf"
		out="$(az ad sp create-for-rbac --name "$sp_name" \
			--role "DNS Zone Contributor" --scopes "$scope" \
			-o json --only-show-errors 2>>"$LOG")" || {
			problem "Zugang konnte nicht angelegt werden."
			info "In Firmen-Tenants ist das Anlegen von App-Registrierungen oft"
			info "gesperrt, und die Rollenzuweisung braucht Owner-Recht auf der"
			info "Zone. Details in setup.log. Von Hand geht es nach"
			info "docs/07-azure-dns.md, dann die AZURE_*-Werte in die .env."
			return 1; }
		appid="$(printf '%s' "$out" | python3 -c "import json,sys; print(json.load(sys.stdin)['appId'])")"
		secret="$(printf '%s' "$out" | python3 -c "import json,sys; print(json.load(sys.stdin)['password'])")"
	fi
	[[ -n $appid && -n $secret && -n $tenant ]] \
		|| { problem "Azure-Zugangsdaten unvollstaendig"; return 1; }
	ok "Zugang bereit (Client-ID ${appid:0:8}...)"

	# --------------------------------------------------------- Adressen
	[[ -n $TS_IP ]] || { problem "Ohne Tailscale-Adresse kann ich die privaten Namen nicht setzen"; return 1; }
	local pub
	pub="$(curl -fsS --max-time 10 https://ipinfo.io/ip 2>/dev/null | tr -d '[:space:]')"
	echo
	echo "  Ich setze zwei Arten von Eintraegen:"
	printf '    *.%-28s -> %s   (nur ueber Tailscale)\n' "$BASE_DOMAIN" "$TS_IP"
	printf '    requests.%-21s -> %s   (oeffentlich)\n' "$BASE_DOMAIN" "${pub:-unbekannt}"
	echo
	echo "  Ein genauer Eintrag schlaegt im DNS immer den Platzhalter. Deshalb"
	echo "  ist 'requests' die einzige Ausnahme, alles andere bleibt privat."
	echo

	dns_set_a "*" "$TS_IP" \
		&& ok "*.${BASE_DOMAIN} zeigt auf $TS_IP" \
		|| { problem "Platzhalter-Eintrag fehlgeschlagen, siehe setup.log"; return 1; }

	if [[ -n $pub ]]; then
		if ask_yn "requests.${BASE_DOMAIN} oeffentlich anlegen? (damit Familie und Freunde Wuensche eintragen koennen)" j; then
			if dns_set_a "requests" "$pub"; then
				ok "requests.${BASE_DOMAIN} zeigt auf $pub"
				note "Am Router muessen 80 und 443 auf diesen Rechner zeigen, sonst nichts."
				note "Wechselt deine Internet-Adresse, zeigt der Eintrag ins Leere."
				note "Dauerhafte Loesung: CNAME auf einen DynDNS-Namen, docs/07-azure-dns.md."
			else
				problem "requests-Eintrag fehlgeschlagen"
			fi
		else
			info "Kein oeffentlicher Eintrag. Dann ist auch Overseerr nur im Tailnet."
		fi
	else
		note "Oeffentliche Adresse nicht ermittelbar, 'requests' wurde nicht gesetzt."
	fi

	# -------------------------------------------------------- Speichern
	env_set BASE_DOMAIN "$BASE_DOMAIN"
	env_set PAPERLESS_DOMAIN "paperless.${BASE_DOMAIN}"
	env_set AZURE_SUBSCRIPTION_ID "$AZ_SUB"
	env_set AZURE_RESOURCE_GROUP_NAME "$AZ_RG"
	env_set AZURE_TENANT_ID "$tenant"
	env_set AZURE_CLIENT_ID "$appid"
	env_set AZURE_CLIENT_SECRET "$secret"
	# Die Azure-Anmeldung ist fast immer eine brauchbare Mailadresse.
	if [[ -z $(env_get ACME_EMAIL 2>/dev/null || true) && $who == *@*.* ]]; then
		env_set ACME_EMAIL "$who"
		info "E-Mail fuer die Zertifikate: $who"
	else
		env_ask_if_empty ACME_EMAIL "E-Mail fuer die Zertifikate (Let's Encrypt)"
	fi
	ok "Zugangsdaten in der .env gespeichert"

	# ------------------------------------------------------ Gegenprobe
	local got
	got="$(az network dns record-set a show -g "$AZ_RG" -z "$BASE_DOMAIN" -n "*" \
		--subscription "$AZ_SUB" --query "(ARecords || aRecords)[0].ipv4Address" -o tsv \
		--only-show-errors 2>/dev/null)"
	if [[ $got == "$TS_IP" ]]; then
		ok "Azure bestaetigt: *.${BASE_DOMAIN} -> $got"
	else
		note "Azure meldet '${got:-nichts}', erwartet war $TS_IP. Bitte pruefen."
	fi
	info "Die Azure-Anmeldung wird ab jetzt nicht mehr gebraucht."
	info "Abmelden kannst du mit:  az logout"
	return 0
}

# Taugen die Azure-Werte in der .env? Subscription, Tenant und Client
# sind immer GUIDs. Ein Anzeigename wie "Default Directory (...)" an
# ihrer Stelle heisst: von Hand falsch eingetragen, also neu einrichten.
azure_env_valid() {
	local k v guid='^[0-9a-fA-F]{8}(-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}$'
	for k in AZURE_SUBSCRIPTION_ID AZURE_TENANT_ID AZURE_CLIENT_ID; do
		v="$(env_get "$k" 2>/dev/null || true)"
		[[ $v =~ $guid ]] || return 1
	done
	[[ -n $(env_get AZURE_RESOURCE_GROUP_NAME 2>/dev/null || true) ]] \
		&& [[ -n $(env_get AZURE_CLIENT_SECRET 2>/dev/null || true) ]]
}

if [[ -n $BASE_DOMAIN ]] && ! azure_env_valid \
	&& [[ -n $(env_get AZURE_TENANT_ID 2>/dev/null)$(env_get AZURE_CLIENT_ID 2>/dev/null) ]]; then
	note "Die Azure-Werte in der .env sind unvollstaendig oder ungueltig."
	info "Subscription, Tenant und Client muessen IDs sein, keine Namen."
	info "Am einfachsten: gleich die automatische Einrichtung waehlen."
fi

if azure_env_valid && [[ -n $BASE_DOMAIN ]]; then
	USE_PROXY=1
	ok "Schon eingerichtet fuer $BASE_DOMAIN"
	if ask_yn "DNS-Eintraege neu setzen? (etwa weil sich deine Internet-Adresse geaendert hat)" n; then
		azure_dns_setup || note "Nicht geaendert, die bisherigen Werte bleiben."
	fi
else
	echo "  Ohne diesen Schritt erreichst du alles unter http://$LAN_IP:8989 und"
	echo "  aehnlich. Das funktioniert einwandfrei, sieht nur nicht schoen aus."
	echo
	echo "  Mit eigener Domain bekommst du https://paperless.deine-domain.ch mit"
	echo "  echtem, browservertrautem Zertifikat, und trotzdem ist nichts davon"
	echo "  aus dem Internet erreichbar: die Namen zeigen auf deine private"
	echo "  Tailscale-Adresse. Ich richte Zone, Zugang und Eintraege selbst ein."
	echo "  Voraussetzung ist eine Domain, deren DNS-Zone bei Azure liegt."
	echo
	if [[ -z $TS_IP ]]; then
		note "Tailscale ist nicht verbunden, und das ist hierfuer Voraussetzung."
		info "Spaeter: sudo tailscale up --ssh, dann sudo ./setup.sh erneut."
	elif ask_yn "Domain jetzt automatisch einrichten?" n; then
		if azure_dns_setup; then
			USE_PROXY=1
			ok "Adressen eingerichtet"
		else
			note "Uebersprungen. Der Stack laeuft trotzdem, ueber IP und Portnummer."
			BASE_DOMAIN=""
		fi
	else
		info "Uebersprungen. Du nutzt Adressen mit IP und Portnummer."
	fi
fi

# =========================================================================
# 6. Downloads
# =========================================================================
step "6/9  Downloads"

USE_TORRENT=0; USE_USENET=0
if [[ -n $(env_get PROTON_WG_PRIVATE_KEY 2>/dev/null || true) ]]; then
	USE_TORRENT=1; ok "Torrents sind schon eingerichtet"
else
	echo "  Fuer Torrents braucht es ein VPN, sonst ist deine Adresse oeffentlich"
	echo "  sichtbar. Eingerichtet ist ProtonVPN (ca. 4 bis 5 EUR pro Monat)."
	echo "  Den Schluessel findest du dort unter Downloads, WireGuard-Konfiguration."
	echo "  Wichtig: beim Erzeugen NAT-PMP und P2P aktivieren."
	if ask_yn "ProtonVPN-Schluessel jetzt eingeben?" n; then
		k="$(ask_secret "WireGuard PrivateKey")"
		if [[ -n $k ]]; then
			env_set PROTON_WG_PRIVATE_KEY "$k"
			env_set VPN_COUNTRIES "$(ask 'Land fuer den VPN-Server' "$(env_get VPN_COUNTRIES || echo Switzerland)")"
			USE_TORRENT=1; ok "Torrents werden eingerichtet"
		fi
	else
		info "Uebersprungen. Ohne VPN werden keine Torrents gestartet, das ist Absicht."
	fi
fi

if [[ -n $(env_get SABNZBD_API_KEY 2>/dev/null || true) ]] \
	|| [[ ${COMPOSE_PROFILES_OLD:-} == *usenet* ]]; then
	USE_USENET=1
fi
if (( ! USE_USENET )); then
	echo
	echo "  Usenet ist schneller als Torrents und braucht kein VPN, kostet aber"
	echo "  ein Abo bei einem Anbieter plus einen Suchdienst."
	if ask_yn "Usenet mitstarten? (Zugangsdaten traegst du danach im Browser ein)" n; then
		USE_USENET=1; ok "Usenet wird mitgestartet"
	else
		info "Uebersprungen."
	fi
fi

# =========================================================================
# 7. Dokumente
# =========================================================================
step "7/9  Dokumentenarchiv (optional)"

USE_DOCS=0
if [[ -n $(env_get PAPERLESS_SECRET_KEY 2>/dev/null || true) ]]; then
	USE_DOCS=1; ok "Paperless ist schon eingerichtet"
else
	echo "  Paperless durchsucht eingescannte Briefe und Rechnungen im Volltext,"
	echo "  auf Deutsch. Braucht etwa 2 GB RAM und keine externen Zugaenge."
	if ask_yn "Paperless einrichten?" j; then
		env_set PAPERLESS_SECRET_KEY "$(gen_secret 64)"
		env_set PAPERLESS_DB_PASSWORD "$(gen_secret 32)"
		env_set PAPERLESS_ADMIN_USER "$(ask 'Benutzername fuer Paperless' "${ADMIN_USER}")"
		PW="$(gen_secret 20)"; env_set PAPERLESS_ADMIN_PASSWORD "$PW"
		USE_DOCS=1
		ok "Eingerichtet, Passwort steht am Ende in der Uebersicht"
	else
		info "Uebersprungen."
	fi
fi

# =========================================================================
# 8. Plex
# =========================================================================
step "8/9  Plex"

if [[ -z $(env_get PLEX_CLAIM 2>/dev/null || true) ]] \
	&& [[ ! -f $REPO_DIR/config/plex/Library/Application\ Support/Plex\ Media\ Server/Preferences.xml ]]; then
	echo "  Damit Plex sofort mit deinem Konto verbunden ist, brauchst du einen"
	echo "  Code von https://www.plex.tv/claim/ . Er gilt nur 4 Minuten."
	echo "  Ohne Code geht es auch, du verbindest Plex dann im Browser selbst."
	c="$(ask "Code von plex.tv/claim (leer = spaeter)")"
	[[ -n $c ]] && { env_set PLEX_CLAIM "$c"; ok "Code uebernommen"; } || info "Ohne Code, du verbindest Plex danach selbst."
else
	ok "Plex ist bereits verbunden"
fi

# =========================================================================
# 9. Schreiben und starten
# =========================================================================
step "9/9  Einstellungen schreiben und starten"

env_set PUID "$PUID"
env_set PGID "$PGID"
env_set RENDER_GID "${RENDER_GID:-0}"
env_set TZ "$(timedatectl show -p Timezone --value 2>/dev/null || echo Europe/Zurich)"
env_set DATA_ROOT "$DATA_ROOT"
env_set PAPERLESS_ROOT "$DATA_ROOT/paperless"
env_set LAN_SUBNET "${LAN_SUBNET:-192.168.1.0/24}"
env_set TAILSCALE_IP "${TS_IP:-127.0.0.1}"
[[ -z $(env_get DOCKER_SUBNET) ]] && env_set DOCKER_SUBNET "172.28.0.0/16"
[[ -z $(env_get VPN_COUNTRIES) ]] && env_set VPN_COUNTRIES "Switzerland"
[[ -z $(env_get QBIT_USER) ]] && env_set QBIT_USER "admin"
[[ -z $(env_get BACKFILL_ITEMS_PER_CYCLE) ]] && env_set BACKFILL_ITEMS_PER_CYCLE 5
[[ -z $(env_get BACKFILL_CYCLE_MINUTES) ]] && env_set BACKFILL_CYCLE_MINUTES 60
[[ -z $(env_get BACKFILL_SEARCH_UPGRADES) ]] && env_set BACKFILL_SEARCH_UPGRADES false

# Profile bestimmen, welche Container ueberhaupt starten. Fehlt ein Zugang,
# laeuft der Dienst gar nicht, statt endlos neu zu starten.
PROFILES=()
(( USE_TORRENT )) && PROFILES+=(torrent)
(( USE_USENET ))  && PROFILES+=(usenet)
(( USE_DOCS ))    && PROFILES+=(docs)
(( USE_PROXY ))   && PROFILES+=(proxy)
PROF_STR="$(IFS=,; echo "${PROFILES[*]}")"
env_set COMPOSE_PROFILES "$PROF_STR"
ok "Aktive Bereiche: ${PROF_STR:-nur Grundausstattung}"

if [[ -n $BASE_DOMAIN ]]; then
	env_set HOMEPAGE_ALLOWED_HOSTS "localhost:3000,homepage:3000,$LAN_IP:3000,home.$BASE_DOMAIN"
else
	env_set HOMEPAGE_ALLOWED_HOSTS "localhost:3000,homepage:3000,$LAN_IP:3000,${TS_IP:-127.0.0.1}:3000"
fi

chown "$ADMIN_USER":"$MEDIA_GROUP" "$REPO_DIR/.env" 2>>"$LOG"
chmod 640 "$REPO_DIR/.env"

# --- Dashboard-Links passend zum gewaehlten Zugriffsweg erzeugen
url_for() {
	local svc="$1"
	if (( USE_PROXY )) && [[ -n ${SUB[$svc]:-} ]]; then
		echo "https://${SUB[$svc]}.$BASE_DOMAIN"
	else
		echo "http://${LAN_IP:-localhost}:${PORT[$svc]}"
	fi
}
# Dienste, die nicht laufen, bekommen keine Kachel. Sonst zeigt die
# Startseite fuer sie dauerhaft "API Error".
INACTIVE=()
(( USE_TORRENT )) || INACTIVE+=(qbittorrent)
(( USE_USENET ))  || INACTIVE+=(sabnzbd)
(( USE_DOCS ))    || INACTIVE+=(paperless)
if [[ -f $REPO_DIR/homepage/services.yaml.tmpl ]]; then
	# Die Daten kommen ueber eine eigene Datei, denn stdin von python3 -
	# ist schon mit dem Skript selbst belegt.
	hp_data="$(mktemp)"
	{
		for svc in "${!PORT[@]}"; do
			u="$(url_for "$svc")"
			[[ $svc == plex ]] && u="http://${LAN_IP:-localhost}:32400/web"
			printf 'url\t%s\t%s\n' "${svc^^}" "$u"
		done
		for svc in "${INACTIVE[@]}"; do printf 'off\t%s\n' "${svc^^}"; done
	} >"$hp_data"
	python3 - "$REPO_DIR/homepage/services.yaml.tmpl" "$REPO_DIR/homepage/services.yaml" "$hp_data" <<'PY2'
import re, sys, pathlib
urls, off = {}, set()
for line in open(sys.argv[3], encoding="utf-8"):
    f = line.rstrip("\n").split("\t")
    if f[0] == "url": urls[f[1]] = f[2]
    elif f[0] == "off": off.add(f[1])

# Die Vorlage in Gruppen ("- Name:") und Kacheln ("    - Name:") zerlegen.
# Eine Kachel faellt weg, wenn ihr href auf einen inaktiven Dienst zeigt,
# eine Gruppe, wenn danach keine Kachel mehr in ihr steht.
head, groups = [], []
for line in pathlib.Path(sys.argv[1]).read_text(encoding="utf-8").splitlines(keepends=True):
    if re.match(r"- \S", line):
        groups.append([line, []])
    elif re.match(r"    - \S", line) and groups:
        groups[-1][1].append([line])
    elif groups and groups[-1][1]:
        groups[-1][1][-1].append(line)
    elif groups:
        groups[-1][0] += line
    else:
        head.append(line)

def keep(tile):
    m = re.search(r"__URL_([A-Z]+)__", "".join(tile))
    return not (m and m.group(1) in off)

out = "".join(head)
for title, tiles in groups:
    tiles = [t for t in tiles if keep(t)]
    if tiles:
        out += title + "".join("".join(t) for t in tiles)
out = re.sub(r"__URL_([A-Z]+)__", lambda m: urls.get(m.group(1), m.group(0)), out)
pathlib.Path(sys.argv[2]).write_text(out, encoding="utf-8")
PY2
	rm -f "$hp_data"
	chown "$MEDIA_USER":"$MEDIA_GROUP" "$REPO_DIR/homepage/services.yaml" 2>>"$LOG"
	ok "Dashboard-Links gesetzt"
fi

# --- systemd-Timer ablegen UND einschalten
# Frueher wurden die Units nur hierher kopiert. Eingeschaltet hat sie
# niemand, also lief weder der OneDrive-Abgleich noch die Sicherung. Das
# faellt erst auf, wenn man die Sicherung braucht.
if [[ -d $REPO_DIR/systemd ]]; then
	for f in "$REPO_DIR"/systemd/*.service "$REPO_DIR"/systemd/*.timer; do
		[[ -e $f ]] || continue
		sed "s|@REPO_DIR@|$REPO_DIR|g" "$f" >"/etc/systemd/system/$(basename "$f")"
	done
	systemctl daemon-reload >>"$LOG" 2>&1

	# Jeder Timer wird nur eingeschaltet, wenn sein Ziel wirklich
	# eingerichtet ist. Sonst scheitert er im Minutentakt und fuellt das
	# Journal, statt sichtbar zu fehlen.
	RCLONE_CONF="${RCLONE_CONFIG:-/etc/rclone/rclone.conf}"
	TIMERS=0
	if command -v rclone >/dev/null 2>&1 && [[ -f $RCLONE_CONF ]]; then
		if [[ -n $(env_get RCLONE_REMOTE) && -n $(env_get RCLONE_INBOX_PATH) ]]; then
			systemctl enable --now paperless-inbox.timer >>"$LOG" 2>&1 \
				&& TIMERS=$((TIMERS+1))
		fi
		if [[ -n $(env_get RCLONE_REMOTE) && -n $(env_get RCLONE_MIRROR_PATH) ]]; then
			systemctl enable --now paperless-export.timer >>"$LOG" 2>&1 \
				&& TIMERS=$((TIMERS+1))
		fi
		if [[ -n $(env_get RCLONE_REMOTE_CRYPT) && -n $(env_get RCLONE_CONFIG_BACKUP_PATH) ]]; then
			systemctl enable --now mediastack-backup.timer >>"$LOG" 2>&1 \
				&& TIMERS=$((TIMERS+1))
		fi
	fi
	if (( TIMERS )); then
		ok "$TIMERS Timer laufen (OneDrive und Sicherung)"
	else
		note "Sicherung und OneDrive laufen noch NICHT. Es fehlt rclone:"
		info "  sudo rclone config   (danach nach /etc/rclone/rclone.conf kopieren)"
		info "  Anleitung: docs/06-paperless-onedrive.md, dann setup.sh erneut"
	fi
fi

cd "$REPO_DIR" || stop "Kann nicht nach $REPO_DIR wechseln."

if (( USE_PROXY )); then
	info "Caddy bauen, das dauert einige Minuten"
	try "Caddy gebaut" docker compose build caddy
fi

info "Images holen, das dauert je nach Leitung 5 bis 20 Minuten"
try "Images geholt" docker compose pull --ignore-buildable -q

info "Container starten"
if ! docker compose up -d >>"$LOG" 2>&1; then
	problem "Nicht alle Container sind gestartet"
	docker compose ps --format '{{.Name}} {{.State}}' 2>/dev/null | grep -v running | sed 's/^/     /'
else
	ok "Container laufen"
fi

# --- Warten und API-Schluessel selbst einsammeln
info "Auf die Dienste warten und Schluessel einsammeln"
for i in $(seq 1 40); do
	[[ -f config/sonarr/config.xml && -f config/radarr/config.xml \
		&& -f config/overseerr/settings.json && -f config/tautulli/config.ini ]] && break
	sleep 5
done

harvest() {
	local app="$1" key=""
	case "$app" in
		sonarr|radarr|lidarr|prowlarr)
			[[ -f config/$app/config.xml ]] &&
				key=$(sed -n 's:.*<ApiKey>\(.*\)</ApiKey>.*:\1:p' "config/$app/config.xml" | head -1) ;;
		sabnzbd)
			[[ -f config/sabnzbd/sabnzbd.ini ]] &&
				key=$(awk -F' *= *' '/^api_key/ {print $2; exit}' config/sabnzbd/sabnzbd.ini) ;;
		bazarr)
			[[ -f config/bazarr/config/config.yaml ]] &&
				key=$(awk '/^ *apikey:/ {print $2; exit}' config/bazarr/config/config.yaml | tr -d "'\"") ;;
		tautulli)
			[[ -f config/tautulli/config.ini ]] &&
				key=$(awk -F' *= *' '/^api_key/ {print $2; exit}' config/tautulli/config.ini) ;;
		overseerr)
			# Den Schluessel legt Overseerr schon beim ersten Start an, noch
			# bevor es im Browser eingerichtet ist.
			[[ -f config/overseerr/settings.json ]] &&
				key=$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["main"]["apiKey"])' \
					config/overseerr/settings.json 2>/dev/null) ;;
	esac
	[[ -n $key ]] || return 1
	env_set "${app^^}_API_KEY" "$key"
}
FOUND=0
for app in sonarr radarr lidarr prowlarr bazarr sabnzbd tautulli overseerr; do
	harvest "$app" && { FOUND=$((FOUND+1)); }
done
if (( FOUND )); then
	ok "$FOUND Schluessel automatisch uebernommen"
else
	note "Noch keine Schluessel gefunden. Einfach spaeter nochmal: sudo ./setup.sh"
fi

# --- Stammordner eintragen
# Ohne Stammordner lehnen Sonarr, Radarr und Lidarr jede Anfrage ab
# ("'Root Folder Path' must not be empty"), und auch Overseerr kann dann
# nichts weiterreichen. Die Pfade sind die IM Container, dort haengt
# DATA_ROOT unter /data.
arr_api() {
	local port="$1" api="$2" key="$3" path="$4"; shift 4
	curl -fsS --max-time 15 -H "X-Api-Key: $key" "$@" \
		"http://localhost:$port/api/$api/$path" 2>>"$LOG"
}
add_root() {
	local app="$1" port="$2" api="$3" dir="$4" key body q m
	key="$(env_get "${app^^}_API_KEY" 2>/dev/null || true)"
	[[ -n $key ]] || return 1
	# Schon eingetragen? Dann nichts aendern.
	arr_api "$port" "$api" "$key" rootfolder | grep -q '"path"' && return 0
	body="{\"path\":\"$dir\"}"
	if [[ $app == lidarr ]]; then
		# Lidarr will zusaetzlich einen Namen und zwei Profile.
		q="$(arr_api "$port" "$api" "$key" qualityprofile | python3 -c \
			'import json,sys; d=json.load(sys.stdin); print(d[0]["id"] if d else "")' 2>>"$LOG")"
		m="$(arr_api "$port" "$api" "$key" metadataprofile | python3 -c \
			'import json,sys; d=json.load(sys.stdin); print(d[0]["id"] if d else "")' 2>>"$LOG")"
		[[ -n $q && -n $m ]] || return 1
		body="{\"name\":\"Musik\",\"path\":\"$dir\",\"defaultQualityProfileId\":$q,\"defaultMetadataProfileId\":$m,\"defaultMonitorOption\":\"all\",\"defaultNewItemMonitorOption\":\"all\",\"defaultTags\":[]}"
	fi
	arr_api "$port" "$api" "$key" rootfolder -X POST \
		-H 'Content-Type: application/json' -d "$body" >>"$LOG" 2>&1
}
ROOTS=0
add_root sonarr 8989 v3 /data/media/tv     && ROOTS=$((ROOTS+1))
add_root radarr 7878 v3 /data/media/movies && ROOTS=$((ROOTS+1))
add_root lidarr 8686 v1 /data/media/music  && ROOTS=$((ROOTS+1))
if (( ROOTS == 3 )); then
	ok "Stammordner in Sonarr, Radarr und Lidarr stehen"
else
	note "Stammordner nicht ueberall gesetzt. In der App unter Einstellungen,"
	info "Medienverwaltung nachtragen: /data/media/tv, /movies, /music."
fi

# --- Tautulli: API freigeben
# Der Einrichtungsassistent von Tautulli schaltet die API aus. Ohne sie
# bleibt die Plex-Kachel auf der Startseite bei "API not enabled". Die
# Datei nur im gestoppten Zustand aendern, sonst schreibt Tautulli beim
# Beenden seinen alten Stand zurueck.
if [[ -f config/tautulli/config.ini ]] && grep -q '^api_enabled = 0' config/tautulli/config.ini; then
	docker compose stop tautulli >>"$LOG" 2>&1
	sed -i 's/^api_enabled = 0/api_enabled = 1/' config/tautulli/config.ini
	docker compose start tautulli >>"$LOG" 2>&1
	ok "Tautulli: API freigegeben"
fi

# --- qBittorrent: festes Passwort statt des temporaeren
# Ohne eigenes Passwort vergibt qBittorrent bei jedem Start ein neues,
# temporaeres und schreibt es ins Log. Damit melden wir uns einmal an,
# setzen ein festes Passwort und schalten gleich die Anmeldung fuer
# localhost ab. Die braucht gluetun, um qBittorrent den VPN-Port zu melden.
qbit_login() {
	# Liefert den HTTP-Status: 200/204 = angemeldet, 000 = nicht erreichbar.
	curl -sS -o /dev/null -w '%{http_code}' --max-time 5 ${2:+-c "$2"} \
		--data-urlencode "username=$(env_get QBIT_USER)" --data-urlencode "password=$1" \
		http://localhost:8080/api/v2/auth/login 2>>"$LOG"
}
qbit_setup() {
	local tmp pw jar code i
	[[ -n $(env_get QBIT_USER) ]] || return 1
	# Direkt nach dem Start steht das temporaere Passwort noch nicht im Log.
	for i in $(seq 1 24); do
		tmp="$(docker logs qbittorrent 2>&1 \
			| sed -n 's/.*temporary password is provided for this session: *//p' \
			| tail -1 | tr -d '[:space:]')"
		[[ -n $tmp ]] && break
		sleep 5
	done
	[[ -n $tmp ]] || return 1
	jar="$(mktemp)"
	# Nur bei "nicht erreichbar" erneut versuchen. Falsche Anmeldungen
	# zaehlt qBittorrent und sperrt nach fuenf die Adresse fuer eine Stunde.
	for i in $(seq 1 24); do
		code="$(qbit_login "$tmp" "$jar")"
		[[ $code != 000 ]] && break
		sleep 5
	done
	[[ $code == 20[04] ]] || { rm -f "$jar"; return 1; }
	pw="$(gen_secret 24)"
	curl -fsS -b "$jar" --max-time 10 \
		--data-urlencode "json={\"web_ui_password\":\"$pw\",\"bypass_local_auth\":true}" \
		http://localhost:8080/api/v2/app/setPreferences >>"$LOG" 2>&1
	rm -f "$jar"
	# Gegenprobe mit dem neuen Passwort, erst dann speichern.
	[[ $(qbit_login "$pw") == 20[04] ]] || return 1
	env_set QBIT_PASS "$pw"
}
if (( USE_TORRENT )) && [[ -z $(env_get QBIT_PASS 2>/dev/null || true) ]]; then
	if qbit_setup; then
		ok "qBittorrent: festes Passwort gesetzt (steht in der .env als QBIT_PASS)"
		FOUND=$((FOUND+1))
	else
		note "qBittorrent-Passwort nicht gesetzt. Von Hand: docs/02-inbetriebnahme.md"
	fi
fi
(( FOUND )) && docker compose up -d --force-recreate recyclarr unpackerr backfill homepage >>"$LOG" 2>&1

# =========================================================================
# Apps untereinander verkabeln
#
# Alles ab hier stand frueher in docs/02-inbetriebnahme.md als Handarbeit.
# Jeder Schritt prueft erst, ob es schon eingerichtet ist, und macht dann
# nichts. Deshalb darf setup.sh beliebig oft laufen.
#
# Die Adressen sind zweierlei Art, das ist die haeufigste Fehlerquelle:
#   - setup.sh selbst spricht die Apps ueber localhost:<veroeffentlichter
#     Port> an, es laeuft ja auf dem Host.
#   - Was die Apps EINANDER eintragen, sind Container-Namen und die
#     INTERNEN Ports: http://sonarr:8989, und fuer qBittorrent gluetun:8080,
#     weil der Container kein eigenes Netz hat.
# =========================================================================
API_TMP="$(mktemp -d)"
trap 'rm -rf "$API_TMP"' EXIT

# Legt eine Ressource in einer *arr-App an (Download-Client, Prowlarr-App).
# Holt dafuer das Schema der App und aendert nur die genannten Felder,
# statt JSON von Hand zu bauen: so ueberlebt es Feldwechsel bei Updates.
cat >"$API_TMP/add.py" <<'PY'
import json, sys, urllib.error, urllib.request

base, key, endpoint, impl, extra, fields = sys.argv[1:7]
want = dict(p.split("=", 1) for p in fields.split("\x1f") if p)

def call(path, data=None):
    req = urllib.request.Request(base + path)
    req.add_header("X-Api-Key", key)
    if data is not None:
        req.add_header("Content-Type", "application/json")
        data = json.dumps(data).encode()
    with urllib.request.urlopen(req, data, timeout=60) as r:
        return json.loads(r.read() or "null")

try:
    for c in call("/" + endpoint):
        if c.get("implementation") == impl:
            print("schon vorhanden"); sys.exit(3)
    schema = next(s for s in call("/" + endpoint + "/schema")
                  if s.get("implementation") == impl)
except (urllib.error.URLError, StopIteration) as e:
    sys.exit("nicht erreichbar: %s" % e)

for f in schema["fields"]:
    if f["name"] in want:
        v, old = want[f["name"]], f.get("value")
        # Den Typ aus dem Schema uebernehmen, sonst lehnt die App den Wert ab.
        if isinstance(old, bool):  v = v.lower() in ("1", "true", "ja")
        elif isinstance(old, int): v = int(v)
        f["value"] = v
schema.update(json.loads(extra))
schema.pop("id", None)
try:
    call("/" + endpoint, schema)
except urllib.error.HTTPError as e:
    # Die App prueft die Verbindung beim Anlegen. Ein Fehler hier heisst
    # fast immer: Adresse, Port oder Zugangsdaten stimmen nicht.
    sys.exit("abgelehnt (%s): %s" % (e.code, e.read().decode()[:300]))
PY

# Aendert einen Konfigurationsabschnitt (Medienverwaltung, Umbenennen).
cat >"$API_TMP/cfg.py" <<'PY'
import json, sys, urllib.error, urllib.request

base, key, section, changes = sys.argv[1], sys.argv[2], sys.argv[3], json.loads(sys.argv[4])

def call(path, data=None, method="GET"):
    req = urllib.request.Request(base + path, method=method)
    req.add_header("X-Api-Key", key)
    if data is not None:
        req.add_header("Content-Type", "application/json")
        data = json.dumps(data).encode()
    with urllib.request.urlopen(req, data, timeout=30) as r:
        return json.loads(r.read() or "null")

try:
    cur = call("/config/" + section)
except urllib.error.URLError as e:
    sys.exit("nicht erreichbar: %s" % e)
todo = {k: v for k, v in changes.items() if k in cur and cur[k] != v}
if not todo:
    print("schon gesetzt"); sys.exit(3)
cur.update(todo)
try:
    call("/config/%s/%s" % (section, cur["id"]), cur, "PUT")
except urllib.error.HTTPError as e:
    sys.exit("abgelehnt (%s): %s" % (e.code, e.read().decode()[:200]))
PY

US=$'\x1f'   # Trennzeichen fuer die Feldliste, kommt in Werten nicht vor

arr_add() { python3 "$API_TMP/add.py" "$@" >>"$LOG" 2>&1; }
arr_cfg() { python3 "$API_TMP/cfg.py" "$@" >>"$LOG" 2>&1; }

# Wartet, bis eine App ihre API beantwortet. Ohne das laufen die ersten
# Aufrufe ins Leere, wenn die Container gerade erst gestartet sind.
api_ready() {
	local port="$1" api="$2" key="$3" i
	[[ -n $key ]] || return 1
	for i in $(seq 1 30); do
		curl -fsS -o /dev/null --max-time 5 -H "X-Api-Key: $key" \
			"http://localhost:$port/api/$api/system/status" 2>/dev/null && return 0
		sleep 2
	done
	return 1
}

SONARR_KEY="$(env_get SONARR_API_KEY 2>/dev/null || true)"
RADARR_KEY="$(env_get RADARR_API_KEY 2>/dev/null || true)"
LIDARR_KEY="$(env_get LIDARR_API_KEY 2>/dev/null || true)"

# --- qBittorrent: Kategorien und Zielpfade
# Die vier Kategorien muessen genau so heissen, die *arr-Apps tragen sie
# beim Download-Client als Ziel ein. Legt eine App sie selbst an, fehlt
# ihnen der Pfad, deshalb bei 409 zusaetzlich editCategory.
if (( USE_TORRENT )) && [[ -n $(env_get QBIT_PASS 2>/dev/null || true) ]]; then
	qjar="$(mktemp)"
	if [[ $(curl -sS -o /dev/null -w '%{http_code}' -c "$qjar" --max-time 10 \
		--data-urlencode "username=$(env_get QBIT_USER)" \
		--data-urlencode "password=$(env_get QBIT_PASS)" \
		http://localhost:8080/api/v2/auth/login 2>>"$LOG") == 20[04] ]]; then
		for cat in tv movies music books; do
			c=$(curl -sS -o /dev/null -w '%{http_code}' -b "$qjar" --max-time 10 \
				--data-urlencode "category=$cat" \
				--data-urlencode "savePath=/data/torrents/$cat" \
				http://localhost:8080/api/v2/torrents/createCategory 2>>"$LOG")
			[[ $c == 409 ]] && curl -sS -o /dev/null -b "$qjar" --max-time 10 \
				--data-urlencode "category=$cat" \
				--data-urlencode "savePath=/data/torrents/$cat" \
				http://localhost:8080/api/v2/torrents/editCategory >>"$LOG" 2>&1
		done
		# Vorbelegen kostet bei Hardlinks nur Zeit, Queueing verhindert,
		# dass zwanzig Downloads gleichzeitig die Leitung teilen.
		curl -sS -o /dev/null -b "$qjar" --max-time 10 \
			--data-urlencode 'json={"save_path":"/data/torrents","preallocate_all":false,"queueing_enabled":true,"max_active_downloads":5,"max_active_uploads":5,"max_active_torrents":10}' \
			http://localhost:8080/api/v2/app/setPreferences >>"$LOG" 2>&1
		ok "qBittorrent: Kategorien und Zielpfade gesetzt"
	else
		note "qBittorrent antwortet nicht, Kategorien nicht gesetzt"
	fi
	rm -f "$qjar"
fi

# --- Download-Clients in Sonarr, Radarr und Lidarr
# Host ist gluetun, NICHT qbittorrent: der Container teilt sich den
# Netzwerk-Namespace mit gluetun, unter eigenem Namen existiert er nicht.
CLIENTS=0; CLIENTS_DA=0
if (( USE_TORRENT )) && [[ -n $(env_get QBIT_PASS 2>/dev/null || true) ]]; then
	qu="$(env_get QBIT_USER)"; qp="$(env_get QBIT_PASS)"
	for spec in "8989${US}v3${US}$SONARR_KEY${US}tvCategory${US}tv" \
	            "7878${US}v3${US}$RADARR_KEY${US}movieCategory${US}movies" \
	            "8686${US}v1${US}$LIDARR_KEY${US}musicCategory${US}music"; do
		IFS="$US" read -r port api key catfield cat <<<"$spec"
		[[ -n $key ]] || continue
		if arr_add "http://localhost:$port/api/$api" "$key" downloadclient QBittorrent \
			'{"name":"qBittorrent","enable":true,"priority":1,"removeCompletedDownloads":true,"removeFailedDownloads":true}' \
			"host=gluetun${US}port=8080${US}username=${qu}${US}password=${qp}${US}${catfield}=${cat}"
		then CLIENTS=$((CLIENTS+1))
		elif [[ $? == 3 ]]; then CLIENTS_DA=$((CLIENTS_DA+1))
		fi
	done
fi
if (( USE_USENET )) && [[ -n $(env_get SABNZBD_API_KEY 2>/dev/null || true) ]]; then
	sk="$(env_get SABNZBD_API_KEY)"
	for spec in "8989${US}v3${US}$SONARR_KEY${US}tvCategory${US}tv" \
	            "7878${US}v3${US}$RADARR_KEY${US}movieCategory${US}movies" \
	            "8686${US}v1${US}$LIDARR_KEY${US}musicCategory${US}music"; do
		IFS="$US" read -r port api key catfield cat <<<"$spec"
		[[ -n $key ]] || continue
		# Port 8080 ist der INTERNE Port von SABnzbd, nicht die 8081,
		# unter der es auf dem Host veroeffentlicht ist.
		if arr_add "http://localhost:$port/api/$api" "$key" downloadclient Sabnzbd \
			'{"name":"SABnzbd","enable":true,"priority":1,"removeCompletedDownloads":true,"removeFailedDownloads":true}' \
			"host=sabnzbd${US}port=8080${US}apiKey=${sk}${US}${catfield}=${cat}"
		then CLIENTS=$((CLIENTS+1))
		elif [[ $? == 3 ]]; then CLIENTS_DA=$((CLIENTS_DA+1))
		fi
	done
fi
if (( CLIENTS )); then
	ok "$CLIENTS Download-Clients eingetragen"
elif (( CLIENTS_DA )); then
	ok "Download-Clients stehen bereits"
fi

# --- Medienverwaltung: hardlinken statt kopieren, umbenennen, Untertitel
# Ohne importExtraFiles bleiben fertig heruntergeladene .srt-Dateien im
# Download-Ordner liegen, statt neben dem Film zu landen.
MM='{"copyUsingHardlinks":true,"importExtraFiles":true,"extraFileExtensions":"srt,sub,idx"}'
MMOK=0; MMDA=0
for spec in "8989${US}v3${US}$SONARR_KEY${US}renameEpisodes" \
	            "7878${US}v3${US}$RADARR_KEY${US}renameMovies" \
	            "8686${US}v1${US}$LIDARR_KEY${US}renameTracks"; do
	IFS="$US" read -r port api key renamekey <<<"$spec"
	[[ -n $key ]] || continue
	arr_cfg "http://localhost:$port/api/$api" "$key" mediamanagement "$MM"; c1=$?
	arr_cfg "http://localhost:$port/api/$api" "$key" naming "{\"$renamekey\":true}"; c2=$?
	# 0 = geaendert, 3 = war schon so. Alles andere ist ein echter Fehler.
	if [[ $c1 == 0 || $c2 == 0 ]]; then MMOK=$((MMOK+1))
	elif [[ $c1 == 3 && $c2 == 3 ]]; then MMDA=$((MMDA+1))
	fi
done
if (( MMOK )); then
	ok "Hardlinks und Umbenennen in $MMOK Apps gesetzt"
elif (( MMDA )); then
	ok "Hardlinks und Umbenennen stehen bereits"
fi

# --- Prowlarr mit den drei Apps verbinden
# Danach schiebt Prowlarr jeden neu angelegten Indexer automatisch in
# Sonarr, Radarr und Lidarr. Ohne das traegt man ihn dreimal von Hand ein.
PKEY="$(env_get PROWLARR_API_KEY 2>/dev/null || true)"
if [[ -n $PKEY ]] && api_ready 9696 v1 "$PKEY"; then
	APPS=0; APPS_DA=0
	for spec in "Sonarr${US}sonarr${US}8989${US}$SONARR_KEY" \
	            "Radarr${US}radarr${US}7878${US}$RADARR_KEY" \
	            "Lidarr${US}lidarr${US}8686${US}$LIDARR_KEY"; do
		IFS="$US" read -r impl host port key <<<"$spec"
		[[ -n $key ]] || continue
		if arr_add "http://localhost:9696/api/v1" "$PKEY" applications "$impl" \
			"{\"name\":\"$impl\",\"syncLevel\":\"fullSync\"}" \
			"prowlarrUrl=http://prowlarr:9696${US}baseUrl=http://${host}:${port}${US}apiKey=${key}"
		then APPS=$((APPS+1))
		elif [[ $? == 3 ]]; then APPS_DA=$((APPS_DA+1))
		fi
	done
	if (( APPS )); then
		ok "Prowlarr an $APPS Apps angebunden"
	elif (( APPS_DA )); then
		ok "Prowlarr ist bereits angebunden"
	fi
fi

# --- Qualitaetsprofile schreiben
# Muss VOR Overseerr laufen, sonst gibt es das deutsche Profil noch nicht,
# auf das Overseerr zeigen soll.
if [[ -n $SONARR_KEY && -n $RADARR_KEY ]]; then
	info "Deutsche Qualitaetsprofile schreiben, das dauert ein bis zwei Minuten"
	if docker compose run --rm recyclarr sync >>"$LOG" 2>&1; then
		ok "Qualitaetsprofile geschrieben (Recyclarr)"
	else
		note "Recyclarr lief nicht durch. Von Hand: docker compose run --rm recyclarr sync"
	fi
fi

# --- Plex: Token einsammeln, Transcode-Ziel und Bibliotheken
# Den Token musstest du frueher im Browser aus einer XML-Ansicht abschreiben.
# Er steht in Plex' eigener Konfigurationsdatei, sobald der Server einmal
# mit deinem Konto verbunden war.
PLEX_TOKEN_VAL="$(env_get PLEX_TOKEN 2>/dev/null || true)"
if [[ -z $PLEX_TOKEN_VAL ]]; then
	PLEX_TOKEN_VAL="$(docker exec plex cat \
		'/config/Library/Application Support/Plex Media Server/Preferences.xml' 2>/dev/null \
		| sed -n 's/.*PlexOnlineToken="\([^"]*\)".*/\1/p')"
	if [[ -n $PLEX_TOKEN_VAL ]]; then
		env_set PLEX_TOKEN "$PLEX_TOKEN_VAL"
		env_set PLEX_URL "http://localhost:32400"
		ok "Plex-Token uebernommen (Tautulli und Kometa brauchen ihn)"
	fi
fi
if [[ -n $PLEX_TOKEN_VAL ]]; then
	# /transcode ist die RAM-Disk aus compose.yaml. Ohne diese Einstellung
	# schreibt Plex jeden Umrechenvorgang auf die SSD.
	curl -sS -o /dev/null --max-time 10 -X PUT -H "X-Plex-Token: $PLEX_TOKEN_VAL" \
		--get --data-urlencode 'TranscoderTempDirectory=/transcode' \
		http://localhost:32400/:/prefs >>"$LOG" 2>&1 \
		&& ok "Plex: Umrechnen laeuft ueber den Arbeitsspeicher"

	# Bibliotheken anlegen, die es noch nicht gibt. Agent und Scanner
	# muessen zusammenpassen, sonst antwortet Plex mit
	# "new scanner needs to be paired with new agent".
	have="$(curl -fsS --max-time 10 -H "X-Plex-Token: $PLEX_TOKEN_VAL" \
		http://localhost:32400/library/sections 2>>"$LOG" \
		| sed -n 's/.*<Location[^>]*path="\([^"]*\)".*/\1/p')"
	LIBS=0
	for spec in "Filme${US}movie${US}tv.plex.agents.movie${US}Plex Movie${US}/data/media/movies" \
	            "Serien${US}show${US}tv.plex.agents.series${US}Plex TV Series${US}/data/media/tv" \
	            "Musik${US}artist${US}tv.plex.agents.music${US}Plex Music${US}/data/media/music"; do
		IFS="$US" read -r name type agent scanner path <<<"$spec"
		grep -qxF "$path" <<<"$have" && continue
		curl -sS -o /dev/null --max-time 30 -X POST -H "X-Plex-Token: $PLEX_TOKEN_VAL" \
			--get --data-urlencode "name=$name" --data-urlencode "type=$type" \
			--data-urlencode "agent=$agent" --data-urlencode "scanner=$scanner" \
			--data-urlencode 'language=de-DE' --data-urlencode "location=$path" \
			http://localhost:32400/library/sections >>"$LOG" 2>&1 && LIBS=$((LIBS+1))
	done
	(( LIBS )) && ok "$LIBS Plex-Bibliotheken angelegt"
fi

# --- Bazarr: Verbindungen und das Sprachprofil
# Ohne Sprachprofil laedt Bazarr NICHTS und meldet dabei keinen Fehler.
# Die API nimmt Formularfelder, kein JSON, und die Profile liegen unter
# einem eigenen Endpunkt, nicht in den Einstellungen.
BKEY="$(env_get BAZARR_API_KEY 2>/dev/null || true)"
if [[ -n $BKEY && -n $SONARR_KEY && -n $RADARR_KEY ]]; then
	bz() { curl -sS -o /dev/null -w '%{http_code}' --max-time 25 -X POST \
		-H "X-API-KEY: $BKEY" "$@" http://localhost:6767/api/system/settings 2>>"$LOG"; }
	if [[ $(bz --data-urlencode 'settings-general-use_sonarr=true' \
		--data-urlencode 'settings-sonarr-ip=sonarr' \
		--data-urlencode 'settings-sonarr-port=8989' \
		--data-urlencode "settings-sonarr-apikey=$SONARR_KEY" \
		--data-urlencode 'settings-general-use_radarr=true' \
		--data-urlencode 'settings-radarr-ip=radarr' \
		--data-urlencode 'settings-radarr-port=7878' \
		--data-urlencode "settings-radarr-apikey=$RADARR_KEY") == 2?? ]]; then

		# Nur anlegen, wenn noch kein Profil existiert: sonst wuerden
		# eigene Aenderungen bei jedem Lauf ueberschrieben.
		if ! curl -fsS --max-time 10 -H "X-API-KEY: $BKEY" \
			http://localhost:6767/api/system/languages/profiles 2>>"$LOG" | grep -q '"profileId"'; then
			# cutoff zeigt auf Position 1 (Deutsch): sind deutsche
			# Untertitel da, hoert Bazarr auf zu suchen.
			bz --data-urlencode 'languages-profiles=[{"profileId":1,"name":"Deutsch, Englisch","items":[{"id":1,"language":"de","audio_exclude":"False","hi":"False","forced":"False"},{"id":2,"language":"en","audio_exclude":"False","hi":"False","forced":"False"}],"cutoff":1,"mustContain":[],"mustNotContain":[],"originalFormat":false,"tag":null}]' \
				--data-urlencode 'settings-general-serie_default_enabled=true' \
				--data-urlencode 'settings-general-serie_default_profile=1' \
				--data-urlencode 'settings-general-movie_default_enabled=true' \
				--data-urlencode 'settings-general-movie_default_profile=1' \
				--data-urlencode 'settings-general-use_embedded_subs=true' \
				--data-urlencode 'settings-subsync-use_subsync=true' >>"$LOG" 2>&1
			# Die Sprachen selbst schaltet Bazarr ueber ein eigenes Feld
			# frei. Fehlt das, steht das Profil da, wird aber nicht benutzt.
			bz -d 'languages-enabled=de' -d 'languages-enabled=en' >>"$LOG" 2>&1
			ok "Bazarr: Deutsch vor Englisch, Synchronisierung an"
		else
			ok "Bazarr: Verbindungen gesetzt, Sprachprofil war schon da"
		fi
	else
		note "Bazarr antwortet nicht. Von Hand: docs/03-deutsche-profile.md"
	fi
fi

# --- Overseerr an Sonarr und Radarr
# Die Anmeldung mit dem Plex-Konto bleibt Handarbeit, die Verbindungen
# dahinter nicht. Overseerr nimmt seine API schon vor dem Assistenten an.
OKEY="$(env_get OVERSEERR_API_KEY 2>/dev/null || true)"
if [[ -n $OKEY && -n $SONARR_KEY && -n $RADARR_KEY ]]; then
	# Das deutsche Profil von Recyclarr suchen, sonst das erste beste.
	prof_id() {
		curl -fsS --max-time 10 -H "X-Api-Key: $2" "http://localhost:$1/api/$3/qualityprofile" 2>>"$LOG" \
			| python3 -c 'import json,sys
d=json.load(sys.stdin)
g=[p for p in d if "German" in p["name"]]
print((g or d or [{"id":""}])[0]["id"])' 2>>"$LOG"
	}
	prof_name() {
		curl -fsS --max-time 10 -H "X-Api-Key: $2" "http://localhost:$1/api/$3/qualityprofile" 2>>"$LOG" \
			| python3 -c 'import json,sys
d=json.load(sys.stdin)
g=[p for p in d if "German" in p["name"]]
print((g or d or [{"name":""}])[0]["name"])' 2>>"$LOG"
	}
	ov_post() {
		curl -sS -o /dev/null -w '%{http_code}' --max-time 20 -X POST \
			-H "X-Api-Key: $OKEY" -H 'Content-Type: application/json' -d "$2" \
			"http://localhost:5055/api/v1/settings/$1" 2>>"$LOG"
	}
	OVOK=0
	if ! curl -fsS --max-time 10 -H "X-Api-Key: $OKEY" \
		http://localhost:5055/api/v1/settings/sonarr 2>>"$LOG" | grep -q '"hostname"'; then
		sid="$(prof_id 8989 "$SONARR_KEY" v3)"; sname="$(prof_name 8989 "$SONARR_KEY" v3)"
		[[ -n $sid ]] && [[ $(ov_post sonarr "{\"name\":\"Sonarr\",\"hostname\":\"sonarr\",\"port\":8989,\"apiKey\":\"$SONARR_KEY\",\"useSsl\":false,\"baseUrl\":\"\",\"activeProfileId\":$sid,\"activeProfileName\":\"$sname\",\"activeDirectory\":\"/data/media/tv\",\"activeLanguageProfileId\":1,\"is4k\":false,\"isDefault\":true,\"enableSeasonFolders\":true,\"syncEnabled\":true,\"preventSearch\":false,\"tagRequests\":false}") == 2?? ]] && OVOK=$((OVOK+1))
	fi
	if ! curl -fsS --max-time 10 -H "X-Api-Key: $OKEY" \
		http://localhost:5055/api/v1/settings/radarr 2>>"$LOG" | grep -q '"hostname"'; then
		rid="$(prof_id 7878 "$RADARR_KEY" v3)"; rname="$(prof_name 7878 "$RADARR_KEY" v3)"
		[[ -n $rid ]] && [[ $(ov_post radarr "{\"name\":\"Radarr\",\"hostname\":\"radarr\",\"port\":7878,\"apiKey\":\"$RADARR_KEY\",\"useSsl\":false,\"baseUrl\":\"\",\"activeProfileId\":$rid,\"activeProfileName\":\"$rname\",\"activeDirectory\":\"/data/media/movies\",\"is4k\":false,\"isDefault\":true,\"minimumAvailability\":\"released\",\"syncEnabled\":true,\"preventSearch\":false,\"tagRequests\":false}") == 2?? ]] && OVOK=$((OVOK+1))
	fi
	if (( OVOK )); then
		ok "Overseerr an $OVOK Apps angebunden"
	else
		ok "Overseerr ist bereits angebunden"
	fi
fi

# --- Uptime Kuma: Monitore anlegen
# Kuma 1.x hat keine Schnittstelle zum Anlegen von Monitoren, alles laeuft
# ueber socket.io aus dem Browser heraus. Deshalb schreiben wir direkt in
# seine Datenbank - und nur im gestoppten Zustand, weil Kuma die Monitore im
# Speicher haelt und sie beim Beenden zurueckschreiben wuerde. Dasselbe
# Vorgehen wie bei Tautulli weiter oben.
#
# Das Admin-Konto legst du beim ersten Aufruf im Browser an. Vorher gibt es
# keinen Benutzer, dem die Monitore gehoeren koennten. Dann passiert hier
# nichts, und ein spaeteres  sudo ./setup.sh  holt es nach.
KUMA_DB="$REPO_DIR/config/uptime-kuma/kuma.db"
if [[ -f $KUMA_DB ]]; then
	docker compose stop uptime-kuma >>"$LOG" 2>&1
	KUMA_OUT="$(python3 - "$KUMA_DB" "${LAN_IP:-127.0.0.1}" \
		"$USE_TORRENT" "$USE_USENET" "$USE_DOCS" 2>>"$LOG" <<'PY'
import json, sqlite3, sys

db, lan, torrent, usenet, docs = sys.argv[1:6]

# Name, Adresse. Die Adressen sind containerintern, Kuma haengt im selben
# Netz. Plex laeuft im Host-Netz und ist nur ueber die LAN-Adresse zu
# erreichen. /ping bzw. /identity antworten ohne Anmeldung.
mon = [
    ("Plex",        "http://%s:32400/identity" % lan),
    ("Serien",      "http://sonarr:8989/ping"),
    ("Filme",       "http://radarr:7878/ping"),
    ("Musik-Suche", "http://lidarr:8686/ping"),
    ("Suchquellen", "http://prowlarr:9696/ping"),
    ("Untertitel",  "http://bazarr:6767/"),
    ("Wuensche",    "http://overseerr:5055/api/v1/status"),
    ("Musik",       "http://navidrome:4533/ping"),
    ("Hoerbuecher", "http://audiobookshelf:80/healthcheck"),
    ("Statistiken", "http://tautulli:8181/status"),
    ("Aufraeumen",  "http://cleanuparr:11011/health"),
    ("Startseite",  "http://homepage:3000/"),
]
# qBittorrent haengt im Netz von gluetun, deshalb gluetun:8080.
if torrent == "1":
    mon.append(("Torrents", "http://gluetun:8080/"))
if usenet == "1":
    mon.append(("Usenet", "http://sabnzbd:8080/"))
if docs == "1":
    mon.append(("Dokumente", "http://paperless:8000/"))

c = sqlite3.connect(db)
row = c.execute("select id from user order by id limit 1").fetchone()
if not row:
    print("kein-konto"); raise SystemExit(0)

have = {r[0] for r in c.execute("select name from monitor")}
added = 0
for name, url in mon:
    if name in have:
        continue
    c.execute(
        "insert into monitor (name,user_id,active,interval,retry_interval,"
        "timeout,maxretries,url,type,accepted_statuscodes_json) "
        "values (?,?,1,60,60,48,2,?,'http',?)",
        (name, row[0], url, json.dumps(["200-299"])))
    added += 1
c.commit()
print(added)
PY
)"
	docker compose start uptime-kuma >>"$LOG" 2>&1
	case "$KUMA_OUT" in
		kein-konto)
			note "Uptime Kuma: lege im Browser das Konto an, dann setup.sh erneut" ;;
		0)  ok "Uptime Kuma: Monitore stehen bereits" ;;
		[0-9]*) ok "Uptime Kuma: $KUMA_OUT Monitore angelegt" ;;
		*)  note "Uptime Kuma: Monitore nicht angelegt, Details in setup.log" ;;
	esac
fi

# --- VPN-Gegenprobe
if (( USE_TORRENT )); then
	v=$(docker compose exec -T gluetun sh -c 'wget -qO- --timeout=10 https://ipinfo.io/ip' 2>/dev/null | tr -d '[:space:]')
	r=$(curl -fsS --max-time 10 https://ipinfo.io/ip 2>/dev/null | tr -d '[:space:]')
	if [[ -n $v && -n $r && $v == "$r" ]]; then
		problem "WARNUNG: Torrent-Verkehr laeuft NICHT durch das VPN. Bitte pruefen, bevor du Torrents nutzt."
	elif [[ -n $v ]]; then
		ok "VPN aktiv (Torrents zeigen nach aussen $v statt $r)"
	fi

	# Eingehenden Port gegenpruefen. Gluetun legt den von Proton
	# zugewiesenen Port in /tmp/gluetun/forwarded_port ab. Fehlt die Datei,
	# hat Proton NAT-PMP abgelehnt, und das heisst praktisch immer: der
	# WireGuard-Schluessel wurde ohne das Haekchen "NAT-PMP (Port
	# Forwarding)" erzeugt. Ohne eingehenden Port findet qBittorrent kaum
	# Peers und gibt nichts weiter. Der Tunnel selbst steht trotzdem, der
	# Fehler faellt sonst monatelang nicht auf.
	fp=$(docker compose exec -T gluetun sh -c \
		'cat /tmp/gluetun/forwarded_port 2>/dev/null' 2>/dev/null | tr -d '[:space:]')
	if [[ -n $fp && $fp != 0 ]]; then
		ok "Eingehender Port vom VPN: $fp"
	else
		problem "Keine Portweiterleitung vom VPN. Schluessel bei Proton neu erzeugen und dabei NAT-PMP und P2P aktivieren, sonst seedet qBittorrent nicht."
	fi
fi

# =========================================================================
# Uebersicht
# =========================================================================
printf '\n%s%s%s\n' "$B" "════════════════ Fertig ════════════════" "$N"
printf '\n%sDas kannst du jetzt aufrufen:%s\n\n' "$B" "$N"
printf '  %-22s %s\n' "Startseite"  "$(url_for homepage)"
printf '  %-22s %s\n' "Plex"        "http://${LAN_IP:-localhost}:32400/web"
printf '  %-22s %s\n' "Serien"      "$(url_for sonarr)"
printf '  %-22s %s\n' "Filme"       "$(url_for radarr)"
printf '  %-22s %s\n' "Musik"       "$(url_for navidrome)"
printf '  %-22s %s\n' "Hoerbuecher" "$(url_for audiobookshelf)"
printf '  %-22s %s\n' "Wuensche"    "$(url_for overseerr)"
(( USE_DOCS ))    && printf '  %-22s %s\n' "Dokumente" "$(url_for paperless)"
(( USE_TORRENT )) && printf '  %-22s %s\n' "Torrents"  "$(url_for qbittorrent)"
(( USE_USENET ))  && printf '  %-22s %s\n' "Usenet"    "$(url_for sabnzbd)"

if (( USE_DOCS )) && [[ -n ${PW:-} ]]; then
	printf '\n%sPaperless-Zugang%s (steht auch in .env)\n' "$B" "$N"
	printf '  Benutzer: %s\n  Passwort: %s\n' "$(env_get PAPERLESS_ADMIN_USER)" "$PW"
fi

printf '\n%sNaechste Schritte%s\n' "$B" "$N"
n=1
printf '  %d. Ab- und wieder anmelden, dann brauchst du kein sudo mehr fuer docker.\n' $((n++))
if (( USE_TORRENT )) && ! grep -qs 'LocalHostAuth=false' config/qbittorrent/qBittorrent/qBittorrent.conf; then
	printf '  %d. In qBittorrent unter Werkzeuge, Einstellungen, Web-UI die Option\n' $((n++))
	printf '     "Bypass authentication for clients on localhost" EINSCHALTEN.\n'
	printf '     Ohne die bekommst du keinen Port und kannst nichts weitergeben.\n'
fi
if (( HAVE_GPU )); then
	printf '  %d. In Plex unter Einstellungen, Transcoder die hardwarebeschleunigte\n' $((n++))
	printf '     Kodierung anhaken. Das Haekchen ist NICHT automatisch gesetzt,\n'
	printf '     obwohl die Grafikeinheit durchgereicht ist. Ohne es rechnet Plex\n'
	printf '     unnoetig per CPU, und das ist die haeufigste Ursache fuer 100%%\n'
	printf '     Auslastung bei einem einzigen Zuschauer.\n'
else
	printf '  %d. Dieser Rechner hat keine nutzbare Grafikeinheit fuers Umrechnen.\n' $((n++))
	printf '     Plex rechnet dann per CPU, was fuer Apple TV oder Shield reicht,\n'
	printf '     weil die alles direkt abspielen. Smart-TV-Apps und Browser lösen\n'
	printf '     aber Umrechnen aus, und dafuer wird es zu langsam.\n'
	printf '     Siehe docs/04-hardware.md.\n'
fi
# Was hier steht, ist genau das, was sich NICHT skripten laesst: eine
# Kontoanmeldung, eine Auswahl, die von deinem Abo abhaengt. Alles andere
# hat das Skript oben schon eingetragen.
printf '  %d. In Prowlarr Suchquellen hinzufuegen (Indexers, Add Indexer).\n' $((n++))
printf '     Das ist der einzige Schritt, ohne den nichts gefunden wird.\n'
printf '     Welche und mit welcher Prioritaet: docs/03-deutsche-profile.md\n'
printf '  %d. Overseerr im Browser oeffnen und mit dem Plex-Konto anmelden.\n' $((n++))
printf '     Sonarr und Radarr sind darin schon eingetragen.\n'
printf '  %d. Navidrome im Browser oeffnen und das Admin-Konto anlegen.\n' $((n++))
(( USE_USENET ))    && printf '  %d. In SABnzbd die Zugangsdaten deines Usenet-Anbieters eintragen.\n' $((n++))
(( ! USE_TORRENT )) && printf '  %d. Torrents spaeter dazu: sudo ./setup.sh nochmal starten.\n' $((n++))
(( ! USE_DOCS ))    && printf '  %d. Paperless spaeter dazu: sudo ./setup.sh nochmal starten.\n' $((n++))
(( USE_DOCS ))      && printf '  %d. OneDrive anbinden: docs/06-paperless-onedrive.md\n' $((n++))

if ((${#PROBLEMS[@]})); then
	printf '\n%sDas hat nicht geklappt%s\n' "$Y" "$N"
	for p in "${PROBLEMS[@]}"; do printf '  - %s\n' "$p"; done
	printf '\n  Nichts davon ist endgueltig. Ursache in %s nachlesen,\n' "${LOG##*/}"
	printf '  beheben, dann einfach  sudo ./setup.sh  erneut starten.\n'
fi

printf '\n  Status ansehen:  docker compose ps\n'
printf '  Protokoll:       docker compose logs -f\n\n'
exit 0
