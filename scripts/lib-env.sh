# shellcheck shell=bash
#
# Liest die .env so, wie Docker Compose sie versteht: pro Zeile KEY=Wert,
# der Wert woertlich bis zum Zeilenende, umschliessende Anfuehrungszeichen
# werden entfernt.
#
# Bewusst NICHT per source: die .env ist kein Shell-Skript. Ein Wert mit
# Leerzeichen, Klammern oder $ wuerde sonst als Befehl ausgewertet, und
# im schlimmsten Fall ausgefuehrt.
load_env() {
	local line key val
	while IFS= read -r line || [[ -n $line ]]; do
		[[ $line =~ ^[[:space:]]*([A-Za-z_][A-Za-z0-9_]*)=(.*)$ ]] || continue
		key="${BASH_REMATCH[1]}"
		val="${BASH_REMATCH[2]%$'\r'}"
		if [[ $val =~ ^\"(.*)\"$ || $val =~ ^\'(.*)\'$ ]]; then
			val="${BASH_REMATCH[1]}"
		fi
		export "$key=$val"
	done <"$1"
}
