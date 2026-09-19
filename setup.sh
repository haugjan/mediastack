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
	smartmontools vainfo iproute2

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
chown -R "$MEDIA_USER":"$MEDIA_GROUP" "$REPO_DIR/config" 2>>"$LOG"
chmod -R 775 "$REPO_DIR/config" 2>>"$LOG"
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
		--subscription "$AZ_SUB" --query "aRecords[0].ipv4Address" -o tsv \
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

if [[ -n $(env_get AZURE_CLIENT_SECRET 2>/dev/null || true) && -n $BASE_DOMAIN ]]; then
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
	env_set HOMEPAGE_ALLOWED_HOSTS "localhost:3000,$LAN_IP:3000,home.$BASE_DOMAIN"
else
	env_set HOMEPAGE_ALLOWED_HOSTS "localhost:3000,$LAN_IP:3000,${TS_IP:-127.0.0.1}:3000"
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
if [[ -f $REPO_DIR/homepage/services.yaml.tmpl ]]; then
	cp "$REPO_DIR/homepage/services.yaml.tmpl" "$REPO_DIR/homepage/services.yaml"
	for svc in "${!PORT[@]}"; do
		u="$(url_for "$svc")"
		[[ $svc == plex ]] && u="http://${LAN_IP:-localhost}:32400/web"
		python3 - "$REPO_DIR/homepage/services.yaml" "__URL_${svc^^}__" "$u" <<'PY'
import sys, pathlib
p = pathlib.Path(sys.argv[1])
p.write_text(p.read_text(encoding="utf-8").replace(sys.argv[2], sys.argv[3]), encoding="utf-8")
PY
	done
	chown "$MEDIA_USER":"$MEDIA_GROUP" "$REPO_DIR/homepage/services.yaml" 2>>"$LOG"
	ok "Dashboard-Links gesetzt"
fi

# --- systemd-Timer bereitlegen (aktiviert werden sie erst mit OneDrive)
if [[ -d $REPO_DIR/systemd ]]; then
	for f in "$REPO_DIR"/systemd/*.service "$REPO_DIR"/systemd/*.timer; do
		[[ -e $f ]] || continue
		sed "s|@REPO_DIR@|$REPO_DIR|g" "$f" >"/etc/systemd/system/$(basename "$f")"
	done
	systemctl daemon-reload >>"$LOG" 2>&1
	ok "Sicherungs-Timer bereitgelegt (noch nicht aktiv)"
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
	[[ -f config/sonarr/config.xml && -f config/radarr/config.xml ]] && break
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
	esac
	[[ -n $key ]] || return 1
	env_set "${app^^}_API_KEY" "$key"
}
FOUND=0
for app in sonarr radarr lidarr prowlarr bazarr sabnzbd; do
	harvest "$app" && { FOUND=$((FOUND+1)); }
done
if (( FOUND )); then
	ok "$FOUND Schluessel automatisch uebernommen"
	docker compose up -d --force-recreate recyclarr unpackerr backfill homepage >>"$LOG" 2>&1
else
	note "Noch keine Schluessel gefunden. Einfach spaeter nochmal: sudo ./setup.sh"
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
if (( USE_TORRENT )); then
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
printf '  %d. Qualitaetsprofile schreiben:  docker compose run --rm recyclarr sync\n' $((n++))
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
