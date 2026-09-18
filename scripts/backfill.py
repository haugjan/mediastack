#!/usr/bin/env python3
"""Gedrosselte Nachsuche fuer Sonarr, Radarr und Lidarr.

Ersatz fuer Huntarr. Statt einem fremden Container, der die API-Keys aller
*arr-Instanzen zu sehen bekommt, macht dieses Skript genau das Notwendige:
es holt pro Durchlauf eine kleine Zahl fehlender Titel aus der Wanted-Liste
und stoesst gezielt eine Suche dafuer an.

Die Drosselung ist der Punkt. Ein pauschales MissingEpisodeSearch ueber die
ganze Bibliothek feuert hunderte Abfragen auf einmal und bringt dir eine
Indexer-Sperre ein. Deshalb ITEMS_PER_CYCLE klein halten.

Damit nicht jeden Durchlauf dieselben ersten Titel gesucht werden, wird eine
zufaellige Seite der Wanted-Liste gezogen. Ueber viele Durchlaeufe deckt das
die ganze Liste ab, ohne Zustand speichern zu muessen.

Nur Standardbibliothek, keine Abhaengigkeiten.
"""

from __future__ import annotations

import json
import os
import random
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
from datetime import datetime, timezone

TIMEOUT = 60
PAUSE_BETWEEN_APPS = 10  # Sekunden, damit nicht alles gleichzeitig losgeht


def log(msg: str) -> None:
    stamp = datetime.now(timezone.utc).astimezone().strftime("%Y-%m-%d %H:%M:%S")
    print(f"[{stamp}] {msg}", flush=True)


def env_int(name: str, default: int) -> int:
    raw = os.environ.get(name, "").strip()
    try:
        return int(raw) if raw else default
    except ValueError:
        log(f"WARN {name}={raw!r} ist keine Zahl, nutze {default}")
        return default


def env_bool(name: str, default: bool = False) -> bool:
    raw = os.environ.get(name, "").strip().lower()
    if not raw:
        return default
    return raw in {"1", "true", "yes", "on", "ja"}


class Arr:
    """Minimaler Client fuer die *arr-API."""

    def __init__(
        self,
        name: str,
        base_url: str,
        api_key: str,
        api_version: str,
        id_field: str,
        search_command: str,
        sort_key: str | None = None,
    ) -> None:
        self.name = name
        self.base_url = base_url.rstrip("/")
        self.api_key = api_key
        self.api_version = api_version
        self.id_field = id_field          # z.B. "episodeIds"
        self.search_command = search_command  # z.B. "EpisodeSearch"
        self.sort_key = sort_key

    def _call(self, method: str, path: str, params: dict | None = None,
              body: dict | None = None):
        url = f"{self.base_url}/api/{self.api_version}{path}"
        if params:
            url = f"{url}?{urllib.parse.urlencode(params)}"
        data = json.dumps(body).encode("utf-8") if body is not None else None
        req = urllib.request.Request(
            url,
            data=data,
            method=method,
            headers={
                "X-Api-Key": self.api_key,
                "Content-Type": "application/json",
                "Accept": "application/json",
            },
        )
        with urllib.request.urlopen(req, timeout=TIMEOUT) as resp:
            raw = resp.read()
        return json.loads(raw) if raw else None

    def wanted(self, kind: str, page: int, page_size: int) -> dict:
        """kind ist 'missing' (fehlt ganz) oder 'cutoff' (Upgrade moeglich)."""
        params = {"page": page, "pageSize": page_size, "monitored": "true"}
        if self.sort_key:
            params["sortKey"] = self.sort_key
            params["sortDirection"] = "descending"
        return self._call("GET", f"/wanted/{kind}", params=params) or {}

    def search(self, ids: list[int]) -> None:
        self._call("POST", "/command",
                   body={"name": self.search_command, self.id_field: ids})

    def run(self, kind: str, items: int) -> None:
        # Erste Abfrage nur, um totalRecords zu erfahren.
        head = self.wanted(kind, page=1, page_size=items)
        total = int(head.get("totalRecords") or 0)
        if total == 0:
            log(f"{self.name}/{kind}: nichts offen")
            return

        pages = max(1, -(-total // items))  # ceil
        page = random.randint(1, pages)
        payload = head if page == 1 else self.wanted(kind, page=page, page_size=items)

        ids = [rec["id"] for rec in payload.get("records", []) if "id" in rec]
        if not ids:
            log(f"{self.name}/{kind}: Seite {page} von {pages} war leer")
            return

        self.search(ids)
        log(f"{self.name}/{kind}: Suche fuer {len(ids)} Eintraege angestossen "
            f"(Seite {page}/{pages}, {total} offen)")

    def safe_run(self, kind: str, items: int) -> None:
        try:
            self.run(kind, items)
        except urllib.error.HTTPError as err:
            detail = ""
            try:
                detail = err.read().decode("utf-8", "replace")[:300]
            except Exception:
                pass
            log(f"ERROR {self.name}/{kind}: HTTP {err.code} {err.reason} {detail}")
        except urllib.error.URLError as err:
            log(f"ERROR {self.name}/{kind}: nicht erreichbar ({err.reason})")
        except Exception as err:  # noqa: BLE001 - der Loop darf nie sterben
            log(f"ERROR {self.name}/{kind}: {type(err).__name__}: {err}")


def build_clients() -> list[Arr]:
    specs = [
        # name,     env-Prefix, api, id-Feld,     Suchbefehl,      sortKey
        ("Sonarr", "SONARR", "v3", "episodeIds", "EpisodeSearch", "airDateUtc"),
        ("Radarr", "RADARR", "v3", "movieIds", "MoviesSearch", None),
        ("Lidarr", "LIDARR", "v1", "albumIds", "AlbumSearch", None),
    ]
    clients = []
    for name, prefix, api, id_field, command, sort_key in specs:
        url = os.environ.get(f"{prefix}_URL", "").strip()
        key = os.environ.get(f"{prefix}_API_KEY", "").strip()
        if not url or not key:
            log(f"{name}: uebersprungen (URL oder API-Key fehlt in .env)")
            continue
        clients.append(Arr(name, url, key, api, id_field, command, sort_key))
    return clients


def main() -> int:
    items = env_int("ITEMS_PER_CYCLE", 5)
    minutes = env_int("CYCLE_MINUTES", 60)
    upgrades = env_bool("SEARCH_UPGRADES", False)

    clients = build_clients()
    if not clients:
        log("Keine *arr-Instanz konfiguriert. Nichts zu tun, beende.")
        return 1

    kinds = ["missing"] + (["cutoff"] if upgrades else [])
    log(f"Start: {len(clients)} Instanzen, {items} Titel pro Durchlauf, "
        f"alle {minutes} min, Modi {kinds}")

    while True:
        for client in clients:
            for kind in kinds:
                client.safe_run(kind, items)
                time.sleep(PAUSE_BETWEEN_APPS)
        log(f"Durchlauf fertig, schlafe {minutes} min")
        time.sleep(minutes * 60)


if __name__ == "__main__":
    try:
        sys.exit(main())
    except KeyboardInterrupt:
        log("Abbruch durch Signal")
        sys.exit(0)
