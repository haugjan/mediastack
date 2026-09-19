#!/usr/bin/env bash
#
# Sichert den Laufzeitzustand des gesamten Stacks.
#
# Die Medien sind ersetzbar, config/ ist es nicht: dort stecken die
# Plex-Bibliothek samt Sehfortschritt, alle Quality Profiles, Indexer-
# Zugaenge, die Paperless-Datenbank und die Tautulli-Historie.
#
# Geht ausschliesslich ins verschluesselte Remote, weil das Archiv API-Keys
# und Zugangsdaten enthaelt.
#
set -euo pipefail

REPO_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
export RCLONE_CONFIG="${RCLONE_CONFIG:-/etc/rclone/rclone.conf}"
KEEP_DAYS="${KEEP_DAYS:-30}"

[[ -f $REPO_DIR/.env ]] || { echo "FEHLER: .env fehlt" >&2; exit 1; }
# shellcheck source=scripts/lib-env.sh
source "$REPO_DIR/scripts/lib-env.sh"
load_env "$REPO_DIR/.env"

: "${RCLONE_REMOTE_CRYPT:?}" ; : "${RCLONE_CONFIG_BACKUP_PATH:?}"

STAMP="$(date +%F)"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
ARCHIVE="$WORK/mediastack-config-$STAMP.tar.zst"

cd "$REPO_DIR"

echo "== Container anhalten fuer konsistente Datenbanken"
# Ohne das erwischt man SQLite- und Postgres-Dateien mitten im Schreiben.
# Die paar Minuten Ausfall sind nachts verschmerzbar.
docker compose stop

echo "== Archiv packen"
tar --use-compress-program='zstd -3' \
	-cf "$ARCHIVE" \
	--exclude='config/plex/Library/Application Support/Plex Media Server/Cache' \
	--exclude='config/plex/Library/Application Support/Plex Media Server/Metadata' \
	--exclude='config/*/logs' \
	config .env

echo "== Container wieder starten"
docker compose start

echo "== Hochladen nach ${RCLONE_REMOTE_CRYPT}:${RCLONE_CONFIG_BACKUP_PATH}"
rclone copy "$ARCHIVE" "${RCLONE_REMOTE_CRYPT}:${RCLONE_CONFIG_BACKUP_PATH}" \
	--log-level INFO --stats-one-line

echo "== Alte Sicherungen aelter als ${KEEP_DAYS} Tage entfernen"
rclone delete "${RCLONE_REMOTE_CRYPT}:${RCLONE_CONFIG_BACKUP_PATH}" \
	--min-age "${KEEP_DAYS}d" --log-level INFO

echo "== Fertig: $(du -h "$ARCHIVE" | cut -f1)"
