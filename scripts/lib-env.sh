# shellcheck shell=bash
#
# Liest die .env so, wie Docker Compose sie versteht: pro Zeile KEY=Wert,
# der Wert woertlich bis zum Zeilenende, umschliessende Anfuehrungszeichen
# werden entfernt.
#
# Bewusst NICHT per source: die .env ist kein Shell-Skript. Ein Wert mit
# Leerzeichen, Klammern oder $ wuerde sonst als Befehl ausgewertet, und
# im schlimmsten Fall ausgefuehrt.
#
# Zeilenkommentare fallen nach derselben Regel weg wie bei Docker Compose:
# ein # zaehlt nur als Kommentar, wenn Leerraum davor steht. Ohne das wird
# aus
#     RCLONE_REMOTE=onedrive   # dein privates OneDrive
# ein Remote namens "onedrive   # dein privates OneDrive", und rclone sucht
# ein Ziel, das es nicht gibt. Ein # mitten in einem Passwort bleibt stehen.
load_env() {
	local line key val
	while IFS= read -r line || [[ -n $line ]]; do
		[[ $line =~ ^[[:space:]]*([A-Za-z_][A-Za-z0-9_]*)=(.*)$ ]] || continue
		key="${BASH_REMATCH[1]}"
		val="${BASH_REMATCH[2]%$'\r'}"
		if [[ $val =~ ^\"(.*)\"$ || $val =~ ^\'(.*)\'$ ]]; then
			val="${BASH_REMATCH[1]}"
		else
			val="${val%%[[:space:]]#*}"
			val="${val%"${val##*[![:space:]]}"}"
		fi
		export "$key=$val"
	done <"$1"
}
