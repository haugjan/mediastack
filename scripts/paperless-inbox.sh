#!/usr/bin/env bash
#
# Holt Scans aus OneDrive in den Paperless-Eingang.
#
# Einbahnstrasse nach unten: rclone move raeumt die Quelle hinterher ab,
# Paperless verarbeitet und loescht die Datei aus consume/. Damit gibt es
# keinen Zustand, der zwischen zwei Systemen abgeglichen werden muesste,
# und entsprechend auch keine Konfliktkopien.
#
# Ausgeloest von paperless-inbox.timer, standardmaessig alle 5 Minuten.
#
set -euo pipefail

REPO_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
export RCLONE_CONFIG="${RCLONE_CONFIG:-/etc/rclone/rclone.conf}"

[[ -f $REPO_DIR/.env ]] || { echo "FEHLER: .env fehlt" >&2; exit 1; }
# shellcheck source=/dev/null
set -a; source "$REPO_DIR/.env"; set +a

: "${RCLONE_REMOTE:?RCLONE_REMOTE fehlt in .env}"
: "${RCLONE_INBOX_PATH:?RCLONE_INBOX_PATH fehlt in .env}"
: "${PAPERLESS_ROOT:?PAPERLESS_ROOT fehlt in .env}"

CONSUME="$PAPERLESS_ROOT/consume"
mkdir -p "$CONSUME"

# --min-age 1m ist wichtig: sonst greift rclone eine Datei ab, die vom Handy
# gerade erst halb hochgeladen ist, und Paperless sieht ein kaputtes PDF.
rclone move \
	"${RCLONE_REMOTE}:${RCLONE_INBOX_PATH}" "$CONSUME" \
	--min-age 1m \
	--transfers 4 \
	--checkers 8 \
	--delete-empty-src-dirs \
	--exclude ".*" \
	--exclude "*.tmp" \
	--exclude "desktop.ini" \
	--log-level INFO \
	--stats-one-line \
	--stats 1m

# Paperless laeuft als PUID/PGID und muss die Dateien loeschen koennen,
# nachdem es sie verarbeitet hat.
chown -R "${PUID:-1000}:${PGID:-1000}" "$CONSUME" 2>/dev/null || true
