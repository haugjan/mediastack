#!/usr/bin/env bash
#
# Exportiert Paperless und legt das Ergebnis zweifach in OneDrive ab:
#   1. Klartext-Spiegel, damit du am Handy in der OneDrive-App direkt an
#      ein Dokument kommst, auch wenn der Server aus ist.
#   2. Verschluesseltes Vollbackup ueber rclone crypt als Wiederherstellungs-
#      punkt. Dateinamen inklusive, Microsoft sieht nur Blobs.
#
# Einbahnstrasse nach oben. Das Paperless-Archiv unter media/ wird NIE
# synchronisiert: Paperless besitzt diese Pfade, die Datenbank verweist
# darauf, und eine Konfliktkopie oder ein propagiertes Loeschen aus der
# Cloud wuerde den Bestand beschaedigen.
#
# Ausgeloest von paperless-export.timer, standardmaessig taeglich.
#
set -euo pipefail

REPO_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
export RCLONE_CONFIG="${RCLONE_CONFIG:-/etc/rclone/rclone.conf}"

[[ -f $REPO_DIR/.env ]] || { echo "FEHLER: .env fehlt" >&2; exit 1; }
# shellcheck source=scripts/lib-env.sh
source "$REPO_DIR/scripts/lib-env.sh"
load_env "$REPO_DIR/.env"

: "${RCLONE_REMOTE:?}" ; : "${RCLONE_MIRROR_PATH:?}"
: "${RCLONE_REMOTE_CRYPT:?}" ; : "${RCLONE_BACKUP_PATH:?}"
: "${PAPERLESS_ROOT:?}"

EXPORT_DIR="$PAPERLESS_ROOT/export"
mkdir -p "$EXPORT_DIR"

echo "== Paperless exportieren"
# --split-manifest schreibt ein Manifest pro Dokument statt einer einzigen
# riesigen manifest.json. Ohne das aendert sich bei jedem Export eine
# mehrere Megabyte grosse Datei und rclone laedt sie jedes Mal neu hoch.
# --delete raeumt Dokumente ab, die in Paperless geloescht wurden.
# --compare-checksums exportiert nur, was sich wirklich geaendert hat.
(cd "$REPO_DIR" && docker compose exec -T paperless \
	document_exporter /usr/src/paperless/export \
		--split-manifest \
		--delete \
		--compare-checksums \
		--no-progress-bar)

echo "== Klartext-Spiegel nach ${RCLONE_REMOTE}:${RCLONE_MIRROR_PATH}"
rclone sync "$EXPORT_DIR" "${RCLONE_REMOTE}:${RCLONE_MIRROR_PATH}" \
	--transfers 4 --checkers 8 \
	--log-level INFO --stats-one-line --stats 1m

echo "== Verschluesseltes Backup nach ${RCLONE_REMOTE_CRYPT}:${RCLONE_BACKUP_PATH}"
rclone sync "$EXPORT_DIR" "${RCLONE_REMOTE_CRYPT}:${RCLONE_BACKUP_PATH}" \
	--transfers 4 --checkers 8 \
	--log-level INFO --stats-one-line --stats 1m

echo "== Fertig: $(du -sh "$EXPORT_DIR" | cut -f1) exportiert"
