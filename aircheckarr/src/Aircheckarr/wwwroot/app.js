"use strict";
// Oberflaeche von aircheckarr, aufgebaut wie Sonarr und Co.: Seiten ueber
// die Adresse (#/sender), Werkzeugleiste oben, Tabellen mit Markieren und
// Sortieren, Dialoge statt Browser-Popups.
//
// Kein Framework und kein Build-Schritt. Die Datei wird so ausgeliefert,
// wie sie hier steht; wer etwas aendert, laedt einfach die Seite neu.
//
// Die eine Falle: alles, was vom Server oder aus dem Senderkatalog kommt,
// ist fremder Text und geht nur ueber esc() ins HTML. Sendernamen im
// Katalog sind frei eingetragen.

// ------------------------------------------------------------ Werkzeuge
const $ = (sel, wurzel = document) => wurzel.querySelector(sel);

const esc = (s) => String(s ?? "").replace(/[&<>"']/g,
  (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));

const SYMBOLE = {
  menue: '<path d="M3 6h18M3 12h18M3 18h18"/>',
  wunsch: '<path d="M20.8 4.6a5.5 5.5 0 0 0-7.8 0L12 5.7l-1-1.1a5.5 5.5 0 0 0-7.8 7.8L12 21l8.8-8.6a5.5 5.5 0 0 0 0-7.8z"/>',
  sender: '<circle cx="12" cy="12" r="2"/><path d="M16.2 7.8a6 6 0 0 1 0 8.4M7.8 16.2a6 6 0 0 1 0-8.4M19.1 4.9a10 10 0 0 1 0 14.2M4.9 19.1a10 10 0 0 1 0-14.2"/>',
  aktivitaet: '<path d="M22 12h-4l-3 9L9 3l-3 9H2"/>',
  mitschnitt: '<path d="M9 18V5l12-2v13"/><circle cx="6" cy="18" r="3"/><circle cx="18" cy="16" r="3"/>',
  system: '<circle cx="12" cy="12" r="10"/><path d="M12 16v-4M12 8h.01"/>',
  plus: '<path d="M12 5v14M5 12h14"/>',
  suche: '<circle cx="11" cy="11" r="7"/><path d="M21 21l-4.3-4.3"/>',
  aktualisieren: '<path d="M23 4v6h-6M1 20v-6h6"/><path d="M3.5 9a9 9 0 0 1 14.9-3.4L23 10M1 14l4.6 4.4A9 9 0 0 0 20.5 15"/>',
  abgleich: '<path d="M21 12a9 9 0 0 1-15.5 6.2L3 16M3 12a9 9 0 0 1 15.5-6.2L21 8"/><path d="M21 3v5h-5M3 21v-5h5"/>',
  etikett: '<path d="M20.6 13.4l-7.2 7.2a2 2 0 0 1-2.8 0L2 12V2h10l8.6 8.6a2 2 0 0 1 0 2.8z"/><path d="M7 7h.01"/>',
  loeschen: '<path d="M3 6h18M19 6l-1 14a2 2 0 0 1-2 2H8a2 2 0 0 1-2-2L5 6M10 11v6M14 11v6M9 6V4h6v2"/>',
  an: '<path d="M5 3l14 9-14 9V3z"/>',
  aus: '<rect x="6" y="6" width="12" height="12" rx="1"/>',
  zu: '<path d="M18 6L6 18M6 6l12 12"/>',
  filter: '<path d="M22 3H2l8 9.5V19l4 2v-8.5L22 3z"/>',
  ab: '<path d="M6 9l6 6 6-6"/>',
  auf: '<path d="M18 15l-6-6-6 6"/>',
  info: '<circle cx="12" cy="12" r="10"/><path d="M12 16v-4M12 8h.01"/>',
  markieren: '<path d="M9 11l3 3L22 4"/><path d="M21 12v7a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h11"/>',
};
const sym = (name) =>
  `<svg class="sym" viewBox="0 0 24 24" aria-hidden="true">${SYMBOLE[name] ?? ""}</svg>`;

const etikett = (text, art = "", titel = "") =>
  `<span class="etikett ${art}"${titel ? ` title="${esc(titel)}"` : ""}>${esc(text)}</span>`;

async function api(pfad, methode = "GET", koerper) {
  const antwort = await fetch(pfad, {
    method: methode,
    headers: koerper ? { "Content-Type": "application/json" } : {},
    body: koerper ? JSON.stringify(koerper) : undefined,
  });
  let daten = null;
  try { daten = await antwort.json(); } catch { /* leere Antwort */ }
  if (!antwort.ok) throw new Error(daten?.fehler ?? `Der Dienst antwortet mit ${antwort.status}`);
  return daten;
}

const datum = (s) => s
  ? new Date(s).toLocaleString("de-CH", { dateStyle: "short", timeStyle: "short" })
  : "–";

function vor(s) {
  if (!s) return "–";
  const min = Math.round((Date.now() - new Date(s)) / 60000);
  if (min < 1) return "gerade eben";
  if (min < 60) return `vor ${min} Min.`;
  const std = Math.floor(min / 60);
  if (std < 24) return `vor ${std} Std.`;
  return `vor ${Math.floor(std / 24)} Tg.`;
}

const dauer = (sek) => {
  const s = Math.round(sek || 0);
  return `${Math.floor(s / 60)}:${String(s % 60).padStart(2, "0")}`;
};

function meldung(text, art = "info") {
  const el = document.createElement("div");
  el.className = `meldung ${art}`;
  el.textContent = text;
  $("#meldungen").append(el);
  setTimeout(() => el.classList.add("weg"), 5000);
  setTimeout(() => el.remove(), 5500);
}

// ------------------------------------------------------------- Zustand
const zustand = {
  seite: null,
  status: null,
  daten: [],
  markiert: new Set(),
  letzteMarke: null,
  sichtbar: [],
  sortierung: {},
  filter: {},
  suche: "",
};

// ---------------------------------------------------------------- Seiten
// Jede Seite beschreibt nur, was sie zeigt. Laden, Filtern, Sortieren,
// Markieren und Zeichnen erledigt der gemeinsame Teil weiter unten.
const SEITEN = {
  wuensche: {
    titel: "Wünsche",
    symbol: "wunsch",
    laden: () => api("/api/wishes"),
    werkzeug: [
      ["plus", "Hinzufügen", () => wunschDialog()],
      ["abgleich", "Mit Lidarr abgleichen", (knopf) => lidarrAbgleich(knopf)],
      "|",
      ["aktualisieren", "Aktualisieren", (knopf) => neuLaden(knopf)],
    ],
    filter: [["alle", "Alle"], ["offen", "Offen"], ["erfuellt", "Erfüllt"],
             ["lidarr", "Aus Lidarr"], ["hand", "Von Hand"]],
    filtern: (w, f) =>
      f === "offen" ? !w.fulfilledAt
      : f === "erfuellt" ? !!w.fulfilledAt
      : f === "lidarr" ? w.inLidarr
      : f === "hand" ? !w.inLidarr
      : true,
    suchtext: (w) => `${w.artist} ${w.title} ${w.album ?? ""}`,
    spalten: [
      { titel: "Interpret", wert: (w) => w.artist, zelle: (w) => esc(w.artist) },
      { titel: "Titel", wert: (w) => w.title, zelle: (w) => esc(w.title) },
      { titel: "Album", klasse: "weg-klein", wert: (w) => w.album ?? "",
        zelle: (w) => esc(w.album ?? "") },
      { titel: "Quelle", klasse: "schmal", wert: (w) => (w.inLidarr ? 0 : 1),
        zelle: (w) => w.inLidarr
          ? etikett("Lidarr", "info", "Kommt aus Lidarrs Fehlliste und geht nach dem Mitschnitt an Lidarr zurück")
          : etikett("Von Hand", "rahmen") },
      { titel: "Eingetragen", klasse: "schmal weg-klein", wert: (w) => w.createdAt,
        zelle: (w) => `<span title="${esc(datum(w.createdAt))}">${esc(vor(w.createdAt))}</span>` },
      { titel: "Stand", klasse: "schmal", wert: (w) => w.fulfilledAt ?? "",
        zelle: (w) => w.fulfilledAt
          ? etikett("Erfüllt", "erfolg", datum(w.fulfilledAt))
          : etikett("Wartet") },
    ],
    aktionen: [["loeschen", "Löschen", wuenscheLoeschen, "gefahr"]],
    hinweise: (s) => {
      const l = s.lidarr;
      if (!l.verbunden) {
        return [["info", "Lidarr ist nicht verbunden. Wünsche gibt es dann nur von Hand. " +
                         "Mit LIDARR_API_KEY in der .env kommen sie von selbst aus Lidarrs Fehlliste."]];
      }
      const a = l.letzterAbgleich;
      const wann = !a ? "Der erste Abgleich läuft kurz nach dem Start."
        : a.ok ? `Letzter Abgleich ${vor(a.at)}: ${a.wanted} fehlende Titel in Lidarr.`
        : `Beim letzten Abgleich ${vor(a.at)} hat Lidarr nicht geantwortet.`;
      const takt = l.abgleichMinuten > 0
        ? `alle ${l.abgleichMinuten} Minuten` : "nur auf Knopfdruck";
      return [[a && !a.ok ? "warnung" : "info",
        `Alles, was Lidarr bei beobachteten Alben fehlt, steht hier von selbst (${takt}). ` +
        `Bekommt Lidarr einen Titel anderswo her, verschwindet er wieder. ${wann}`]];
    },
    leer: () => "Noch nichts gewünscht. Trag einen Titel von Hand ein oder beobachte ein Album in Lidarr.",
  },

  sender: {
    titel: "Sender",
    symbol: "sender",
    laden: () => api("/api/stations"),
    werkzeug: [
      ["suche", "Sender suchen", () => senderSuchen()],
      ["etikett", "Nach Stilrichtung", () => katalogDialog()],
      "|",
      ["aktualisieren", "Aktualisieren", (knopf) => neuLaden(knopf)],
    ],
    filter: [["alle", "Alle"], ["mithoeren", "Mithören"], ["laeuft", "Läuft gerade"],
             ["tauglich", "Tauglich"], ["untauglich", "Untauglich"], ["ungemessen", "Ungemessen"]],
    filtern: (s, f) =>
      f === "mithoeren" ? s.enabled
      : f === "laeuft" ? s.laeuft
      : f === "tauglich" ? s.tauglich
      : f === "untauglich" ? !!s.gemessen && !s.tauglich
      : f === "ungemessen" ? !s.gemessen
      : true,
    suchtext: (s) => `${s.name} ${s.country ?? ""} ${s.tags ?? ""}`,
    spalten: [
      { titel: "Mithören", klasse: "schmal", wert: (s) => (s.enabled ? 0 : 1),
        zelle: (s) => `<button class="schalter${s.enabled ? " an" : ""}" data-aktion="umschalten"
            data-id="${esc(s.id)}" title="${s.enabled ? "Mithören aus" : "Mithören an"}"
            aria-pressed="${s.enabled}"></button>` },
      { titel: "Sender", wert: (s) => s.name.toLowerCase(),
        zelle: (s) => `${esc(s.name)}${s.tags
          ? `<div class="klein">${esc(s.tags.split(",").slice(0, 4).join(", "))}</div>` : ""}` },
      { titel: "Land", klasse: "weg-klein", wert: (s) => s.country ?? "", zelle: (s) => esc(s.country ?? "") },
      { titel: "Katalog", klasse: "zahl schmal weg-klein", wert: (s) => s.katalogBitrate,
        zelle: (s) => `<span class="klein">${s.katalogBitrate || "–"}</span>` },
      { titel: "Gemessen", klasse: "zahl schmal", wert: (s) => s.gemessenBitrate,
        zelle: (s) => s.gemessenBitrate ? `${s.gemessenBitrate} kbit/s` : "–" },
      { titel: "Codec", klasse: "schmal weg-klein", wert: (s) => s.gemessenCodec ?? "",
        zelle: (s) => esc(s.gemessenCodec ?? "–") },
      { titel: "Zustand", klasse: "schmal", wert: (s) => senderRang(s), zelle: (s) => senderZustand(s) },
    ],
    aktionen: [
      ["an", "Mithören an", (ids) => senderSchalten(ids, true), "primaer"],
      ["aus", "Mithören aus", (ids) => senderSchalten(ids, false)],
      ["loeschen", "Entfernen", senderEntfernen, "gefahr"],
    ],
    hinweise: (s) => {
      const h = [];
      if (s.sender.ausgewaehlt === 0) {
        h.push(["warnung", "Kein Sender zum Mithören ausgewählt. Über „Sender suchen“ " +
                           "mehrere ankreuzen und übernehmen."]);
      } else if (s.sender.bereit > s.filter.maxSender) {
        h.push(["info", `${s.sender.bereit} ausgewählte Sender taugen, gleichzeitig gehört ` +
                        `werden höchstens ${s.filter.maxSender}. Vorrang haben die mit der ` +
                        `höchsten gemessenen Bitrate.`]);
      }
      if (s.wuensche.offen === 0) {
        h.push(["info", "Keine offenen Wünsche. Solange wird gar nicht mitgehört, das spart Bandbreite."]);
      }
      return h;
    },
    leer: () => "Noch kein Sender da. Über „Sender suchen“ welche aus dem Katalog holen.",
  },

  aktivitaet: {
    titel: "Aktivität",
    symbol: "aktivitaet",
    laden: () => api("/api/activity"),
    werkzeug: [["aktualisieren", "Aktualisieren", (knopf) => neuLaden(knopf)]],
    suchtext: (a) => `${a.name} ${a.interpret ?? ""} ${a.titel ?? ""}`,
    spalten: [
      { titel: "Sender", wert: (a) => a.name.toLowerCase(), zelle: (a) => esc(a.name) },
      { titel: "Läuft gerade", wert: (a) => `${a.interpret ?? ""} ${a.titel ?? ""}`,
        zelle: (a) => a.titel
          ? `${esc(a.interpret)} – ${esc(a.titel)}`
          : '<span class="klein">wartet auf die erste Titelmeldung</span>' },
      { titel: "Seit", klasse: "schmal weg-klein", wert: (a) => a.titelSeit ?? "",
        zelle: (a) => esc(vor(a.titelSeit)) },
      { titel: "Qualität", klasse: "schmal weg-klein", wert: (a) => a.bitrate,
        zelle: (a) => `${a.bitrate} kbit/s ${esc(a.codec ?? "")}` },
      { titel: "Zustand", klasse: "schmal", wert: (a) => (a.nimmtAuf ? 0 : 1),
        zelle: (a) => a.nimmtAuf
          ? `<span class="etikett gefahr"><span class="punkt"></span>Nimmt auf</span>`
          : etikett("Hört zu", "marke", `verbunden ${vor(a.seit)}`) },
    ],
    aktionen: [["aus", "Mithören aus", (ids) => senderSchalten(ids, false)]],
    leer: (s) =>
      s.wuensche.offen === 0 ? "Keine offenen Wünsche, deshalb wird gerade nicht mitgehört."
      : s.sender.bereit === 0 ? "Kein ausgewählter Sender taugt bisher. Unter „Sender“ welche auswählen."
      : "Die Verbindungen werden gerade aufgebaut.",
  },

  mitschnitte: {
    titel: "Mitschnitte",
    symbol: "mitschnitt",
    laden: () => api("/api/captures"),
    werkzeug: [["aktualisieren", "Aktualisieren", (knopf) => neuLaden(knopf)]],
    filter: [["alle", "Alle"], ["abgelegt", "Abgelegt"], ["lidarr", "In Lidarr"], ["verworfen", "Verworfen"]],
    filtern: (c, f) =>
      f === "abgelegt" ? c.state === "Done"
      : f === "lidarr" ? c.importedByLidarr
      : f === "verworfen" ? c.state === "Rejected"
      : true,
    suchtext: (c) => `${c.artist} ${c.title} ${c.stationName}`,
    spalten: [
      { titel: "Wann", klasse: "schmal", wert: (c) => c.startedAt, zelle: (c) => esc(datum(c.startedAt)) },
      { titel: "Interpret", wert: (c) => c.artist, zelle: (c) => esc(c.artist) },
      { titel: "Titel", wert: (c) => c.title, zelle: (c) => esc(c.title) },
      { titel: "Sender", klasse: "weg-klein", wert: (c) => c.stationName, zelle: (c) => esc(c.stationName) },
      { titel: "Länge", klasse: "zahl schmal", wert: (c) => c.seconds, zelle: (c) => dauer(c.seconds) },
      { titel: "Qualität", klasse: "schmal weg-klein", wert: (c) => c.bitrate,
        zelle: (c) => `${c.bitrate} kbit/s ${esc(c.codec ?? "")}` },
      { titel: "Ergebnis", klasse: "schmal", wert: (c) => c.state, zelle: (c) => mitschnittErgebnis(c) },
    ],
    leer: () => "Noch nichts mitgeschnitten.",
  },

  system: {
    titel: "System",
    symbol: "system",
    werkzeug: [["aktualisieren", "Aktualisieren", (knopf) => neuLaden(knopf)]],
    zeichnen: (s) => systemSeite(s),
  },
};
for (const [name, seite] of Object.entries(SEITEN)) seite.name = name;

// ------------------------------------------------ Seitenbezogene Helfer
function senderRang(s) {
  if (s.laeuft) return 0;
  if (s.enabled && s.tauglich) return 1;
  if (s.tauglich) return 2;
  if (!s.gemessen) return 3;
  return 4;
}

function senderZustand(s) {
  const f = zustand.status?.filter;
  if (s.laeuft) return etikett("Hört zu", "marke");
  if (!s.gemessen) return etikett("Wird gemessen", "", "Die Messung läuft im Hintergrund");
  if (s.fehler) return etikett("Nicht erreichbar", "gefahr", s.fehler);
  if (!s.tauglich) {
    if (f && s.gemessenBitrate < f.mindestbitrate) {
      return etikett(`Unter ${f.mindestbitrate} kbit/s`, "warnung");
    }
    return etikett(`Codec ${s.gemessenCodec ?? "?"}`, "warnung", "Codec ist nicht zugelassen");
  }
  if (s.enabled) return etikett("Bereit", "erfolg", "Wird gehört, sobald ein Platz frei ist");
  return etikett("Tauglich", "info");
}

function mitschnittErgebnis(c) {
  if (c.state === "Recording") return etikett("Läuft", "marke");
  if (c.state === "Rejected") return etikett(c.reason ?? "Verworfen", "gefahr", c.reason ?? "");
  if (c.importedByLidarr) return etikett("In Lidarr", "erfolg", c.path ?? "");
  if (c.reason) {
    return etikett("Abgelegt", "warnung", `${c.path ?? ""}\nLidarr hat nicht übernommen: ${c.reason}`);
  }
  return etikett("Abgelegt", "erfolg", c.path ?? "");
}

function systemSeite(s) {
  const zeile = (k, v) => `<dt>${esc(k)}</dt><dd>${v}</dd>`;
  const l = s.lidarr;
  const a = l.letzterAbgleich;
  return `
    <section class="kasten">
      <h2>Über</h2>
      <dl class="eigenschaften">
        ${zeile("Version", esc(s.version ?? "–"))}
        ${zeile("Datenbank", esc(s.pfade.datenbank))}
        ${zeile("Bibliothek", esc(s.pfade.bibliothek))}
        ${zeile("Arbeitsordner", esc(s.pfade.arbeit))}
        ${zeile("Senderkatalog", `<a href="${esc(s.katalog)}" target="_blank" rel="noopener">${esc(s.katalog)}</a>`)}
      </dl>
    </section>
    <section class="kasten">
      <h2>Lidarr</h2>
      <dl class="eigenschaften">
        ${zeile("Verbunden", l.verbunden ? etikett("Ja", "erfolg") : etikett("Nein", "gefahr"))}
        ${l.verbunden ? zeile("Abgleich", l.abgleichMinuten > 0
          ? `alle ${l.abgleichMinuten} Minuten, höchstens ${l.maxAlben} Alben` : "nur auf Knopfdruck") : ""}
        ${l.verbunden ? zeile("Letzter Abgleich", !a ? "noch keiner"
          : a.ok ? `${esc(vor(a.at))}: ${a.wanted} fehlende Titel, ${a.added} neu, ${a.removed} erledigt`
          : `${esc(vor(a.at))}: ${etikett("Lidarr antwortete nicht", "gefahr")}`) : ""}
      </dl>
    </section>
    <section class="kasten">
      <h2>Filter</h2>
      <dl class="eigenschaften">
        ${zeile("Mindestbitrate", `${s.filter.mindestbitrate} kbit/s, gemessen`)}
        ${zeile("Codecs", esc(s.filter.codecs.join(", ")))}
        ${zeile("Gleichzeitig", `${s.filter.maxSender} Sender`)}
        ${zeile("Ähnlichkeit", `ab ${s.filter.schwelle}`)}
        ${zeile("Titellänge", `${s.filter.minSekunden} bis ${s.filter.maxSekunden} Sekunden`)}
      </dl>
    </section>
    <section class="kasten">
      <h2>Zahlen</h2>
      <dl class="eigenschaften">
        ${zeile("Sender", `${s.sender.gesamt} bekannt, ${s.sender.gemessen} gemessen, ` +
                          `${s.sender.tauglich} tauglich, ${s.sender.ausgewaehlt} ausgewählt`)}
        ${zeile("Hört gerade", `${s.sender.aktiv} Sender`)}
        ${zeile("Wünsche", `${s.wuensche.offen} offen, ${s.wuensche.erfuellt} erfüllt`)}
      </dl>
    </section>
    <p class="hilfe">Die Einstellungen stehen in der .env und werden beim Start gelesen,
      siehe docs/10-radiomitschnitt.md.</p>`;
}

// ------------------------------------------------------------- Aktionen
async function mitFehler(arbeit) {
  try { return await arbeit(); }
  catch (e) { meldung(e.message, "gefahr"); return undefined; }
}

async function wuenscheLoeschen(ids) {
  const ok = await bestaetigen("Wünsche löschen",
    `${ids.length} ${ids.length === 1 ? "Wunsch" : "Wünsche"} löschen? Wünsche aus Lidarr ` +
    "kommen beim nächsten Abgleich zurück, solange das Album dort beobachtet wird.", "Löschen");
  if (!ok) return;
  await mitFehler(async () => {
    await api("/api/wishes/delete", "POST", { ids: ids.map(Number) });
    zustand.markiert.clear();
    meldung("Gelöscht.", "erfolg");
  });
  neuLaden();
}

async function lidarrAbgleich(knopf) {
  knopf.disabled = true;
  knopf.classList.add("dreht");
  const d = await mitFehler(() => api("/api/wishes/sync-lidarr", "POST"));
  knopf.disabled = false;
  knopf.classList.remove("dreht");
  if (d) {
    meldung(`Lidarr vermisst ${d.fehlend} Titel. ${d.neu} neu übernommen, ${d.erledigt} erledigt.`, "erfolg");
  }
  neuLaden();
}

async function senderSchalten(ids, an) {
  await mitFehler(async () => {
    await api("/api/stations/enabled", "POST", { ids, enabled: an });
    zustand.markiert.clear();
    meldung(`${ids.length} Sender: Mithören ${an ? "an" : "aus"}.`, "erfolg");
  });
  neuLaden();
}

async function senderEntfernen(ids) {
  const ok = await bestaetigen("Sender entfernen",
    `${ids.length} Sender aus der Liste entfernen? Bisherige Mitschnitte bleiben erhalten.`,
    "Entfernen");
  if (!ok) return;
  await mitFehler(async () => {
    await api("/api/stations/delete", "POST", { ids });
    zustand.markiert.clear();
    meldung("Entfernt.", "erfolg");
  });
  neuLaden();
}

// ------------------------------------------------------------- Dialoge
function oeffnen(titel, rumpf, fuss, { breit = false } = {}) {
  const huelle = $("#dialog");
  huelle.innerHTML = `
    <div class="dialog${breit ? " breit" : ""}" role="dialog" aria-modal="true" aria-label="${esc(titel)}">
      <div class="dialog-kopf"><span>${esc(titel)}</span>
        <button data-zu aria-label="Schliessen">${sym("zu")}</button></div>
      <div class="dialog-rumpf">${rumpf}</div>
      <div class="dialog-fuss">${fuss}</div>
    </div>`;
  huelle.hidden = false;
  return huelle.firstElementChild;
}

function schliessen() {
  const huelle = $("#dialog");
  huelle.hidden = true;
  huelle.innerHTML = "";
  huelle.dispatchEvent(new Event("zu"));
}

$("#dialog").addEventListener("click", (e) => {
  if (e.target.id === "dialog" || e.target.closest("[data-zu]")) schliessen();
});
document.addEventListener("keydown", (e) => {
  if (e.key === "Escape" && !$("#dialog").hidden) schliessen();
});

function bestaetigen(titel, text, knopfText) {
  return new Promise((fertig) => {
    let antwort = false;
    const d = oeffnen(titel, `<p>${esc(text)}</p>`,
      `<button class="knopf" data-zu>Abbrechen</button>
       <button class="knopf gefahr" id="ja">${esc(knopfText)}</button>`);
    $("#ja", d).addEventListener("click", () => { antwort = true; schliessen(); });
    $("#dialog").addEventListener("zu", () => fertig(antwort), { once: true });
    $("#ja", d).focus();
  });
}

function wunschDialog() {
  const d = oeffnen("Wunsch hinzufügen", `
    <form id="wunsch-form" class="formular" autocomplete="off">
      <label>Interpret<input name="artist" required></label>
      <label>Titel<input name="title" required></label>
      <label>Album <small>(optional, landet im Tag)</small><input name="album"></label>
      <p class="hilfe">Wird mitgeschnitten, sobald es auf einem ausgewählten Sender läuft.
        Titel aus Lidarr trägst du besser dort ein: beobachte das Album, dann kommt der
        Wunsch von selbst und der Mitschnitt geht an Lidarr zurück.</p>
    </form>`,
    `<button class="knopf" data-zu>Fertig</button>
     <button class="knopf primaer" type="submit" form="wunsch-form">${sym("plus")}Hinzufügen</button>`);
  const form = $("form", d);
  form.addEventListener("submit", async (e) => {
    e.preventDefault();
    const f = new FormData(form);
    const a = await mitFehler(() => api("/api/wishes", "POST",
      { artist: f.get("artist"), title: f.get("title"), album: f.get("album") }));
    if (!a) return;
    meldung(a.bekannt ? "Steht schon auf der Liste." : "Wunsch eingetragen.",
            a.bekannt ? "warnung" : "erfolg");
    // Offen lassen: wer einen Titel eintraegt, hat oft gleich den naechsten.
    form.reset();
    form.artist.focus();
    neuLaden();
  });
  form.artist.focus();
}

function senderSuchen() {
  const angekreuzt = new Set();
  let treffer = [];
  const d = oeffnen("Sender suchen", `
    <form class="suchzeile" id="such-form" autocomplete="off">
      <input name="name" placeholder="Name, z. B. Swiss Jazz">
      <input name="tag" placeholder="Stilrichtung, z. B. funk">
      <input name="country" class="kurz" placeholder="Land" maxlength="2" title="Ländercode, z. B. CH">
      <button class="knopf primaer" type="submit">${sym("suche")}Suchen</button>
    </form>
    <div id="such-treffer"><p class="leer">Name, Stilrichtung oder Land eingeben, dann mehrere
      Sender ankreuzen und gemeinsam übernehmen. Die Bitrate im Katalog ist Selbstauskunft,
      nach dem Übernehmen misst aircheckarr selbst nach.</p></div>`,
    `<span class="fuss-info" id="such-zahl"></span>
     <button class="knopf" data-zu>Abbrechen</button>
     <button class="knopf primaer" id="such-ok" disabled>Übernehmen</button>`,
    { breit: true });

  const zahl = () => {
    const n = angekreuzt.size;
    $("#such-zahl", d).textContent = n ? `${n} angekreuzt` : "";
    $("#such-ok", d).disabled = n === 0;
    $("#such-ok", d).textContent = n ? `${n} Sender übernehmen` : "Übernehmen";
  };

  const zeichnen = () => {
    const ziel = $("#such-treffer", d);
    if (!treffer.length) {
      ziel.innerHTML = '<p class="leer">Nichts gefunden. Weniger eingrenzen oder anders schreiben.</p>';
      return;
    }
    const waehlbar = treffer.filter((t) => !t.ausgewaehlt);
    const alle = waehlbar.length > 0 && waehlbar.every((t) => angekreuzt.has(t.id));
    ziel.innerHTML = `<div class="tabelle-huelle">
      <table class="tabelle klickbar">
        <thead><tr>
          <th class="schmal"><input type="checkbox" data-alle ${alle ? "checked" : ""}
              aria-label="Alle ankreuzen"></th>
          <th>Sender</th><th class="weg-klein">Land</th>
          <th class="zahl schmal">kbit/s</th><th class="schmal weg-klein">Codec</th><th class="schmal"></th>
        </tr></thead>
        <tbody>${treffer.map((t) => `
          <tr data-id="${esc(t.id)}">
            <td><input type="checkbox" ${t.ausgewaehlt || angekreuzt.has(t.id) ? "checked" : ""}
                ${t.ausgewaehlt ? "disabled" : ""} aria-label="${esc(t.name)}"></td>
            <td>${esc(t.name)}${t.tags
              ? `<div class="klein">${esc(t.tags.split(",").slice(0, 4).join(", "))}</div>` : ""}</td>
            <td class="weg-klein">${esc(t.country ?? "")}</td>
            <td class="zahl">${t.katalogBitrate || "–"}</td>
            <td class="weg-klein">${esc(t.katalogCodec ?? "–")}</td>
            <td>${t.ausgewaehlt ? etikett("Schon dabei", "erfolg")
                : t.bekannt ? etikett("Bekannt", "rahmen", "Steht in der Liste, Mithören ist aus") : ""}</td>
          </tr>`).join("")}</tbody>
      </table></div>`;
  };

  $("#such-treffer", d).addEventListener("click", (e) => {
    if (e.target.matches("[data-alle]")) {
      const an = e.target.checked;
      for (const t of treffer) {
        if (t.ausgewaehlt) continue;
        if (an) angekreuzt.add(t.id); else angekreuzt.delete(t.id);
      }
    } else {
      const zeile = e.target.closest("tr[data-id]");
      const t = zeile && treffer.find((x) => x.id === zeile.dataset.id);
      if (!t || t.ausgewaehlt) return;
      if (angekreuzt.has(t.id)) angekreuzt.delete(t.id); else angekreuzt.add(t.id);
    }
    zeichnen();
    zahl();
  });

  const form = $("#such-form", d);
  form.addEventListener("submit", async (e) => {
    e.preventDefault();
    const f = new FormData(form);
    const q = new URLSearchParams();
    for (const k of ["name", "tag", "country"]) {
      const v = String(f.get(k) ?? "").trim();
      if (v) q.set(k, v);
    }
    if (![...q.keys()].length) { meldung("Name, Stilrichtung oder Land angeben.", "warnung"); return; }
    q.set("limit", "150");
    $("#such-treffer", d).innerHTML = '<p class="laedt">Katalog wird durchsucht …</p>';
    const r = await mitFehler(() => api(`/api/catalog?${q}`));
    treffer = r ?? [];
    zeichnen();
  });

  $("#such-ok", d).addEventListener("click", async (e) => {
    e.target.disabled = true;
    const r = await mitFehler(() => api("/api/stations/add", "POST", { ids: [...angekreuzt] }));
    if (!r) { e.target.disabled = false; return; }
    meldung(`${r.uebernommen} Sender übernommen. Sie werden jetzt gemessen und hören mit, sobald sie taugen.`,
            "erfolg");
    schliessen();
    if (zustand.seite === "sender") neuLaden(); else location.hash = "#/sender";
  });

  form.name.focus();
}

function katalogDialog() {
  const d = oeffnen("Sender nach Stilrichtung holen", `
    <form id="katalog-form" class="formular" autocomplete="off">
      <label>Stilrichtung<input name="tag" placeholder="z. B. rock, funk, oldies"></label>
      <label>Land <small>(Ländercode, optional)</small><input name="country" maxlength="2" placeholder="z. B. CH"></label>
      <label>Höchstens<input name="limit" type="number" value="300" min="1" max="2000"></label>
      <label class="reihe"><input type="checkbox" name="select"> Gleich alle zum Mithören auswählen</label>
      <p class="hilfe">Holt die beliebtesten Sender dieser Richtung in die Liste. Ohne das Häkchen
        wählst du danach selbst aus, welche mithören; untaugliche bleiben so oder so stumm.</p>
    </form>`,
    `<button class="knopf" data-zu>Abbrechen</button>
     <button class="knopf primaer" type="submit" form="katalog-form">Holen</button>`);
  const form = $("form", d);
  form.addEventListener("submit", async (e) => {
    e.preventDefault();
    const f = new FormData(form);
    e.submitter.disabled = true;
    const r = await mitFehler(() => api("/api/stations/refresh", "POST", {
      tag: f.get("tag"), country: f.get("country"),
      limit: Number(f.get("limit")) || 300, select: f.get("select") === "on",
    }));
    e.submitter.disabled = false;
    if (!r) return;
    meldung(`${r.geholt} Sender geholt.`, "erfolg");
    schliessen();
    neuLaden();
  });
  form.tag.focus();
}

// -------------------------------------------------------------- Zeichnen
function navigation() {
  $("#navigation").innerHTML = Object.values(SEITEN).map((s) => `
    <a href="#/${s.name}" data-seite="${s.name}">${sym(s.symbol)}<span>${esc(s.titel)}</span>
      <span class="zaehler" id="z-${s.name}"></span></a>`).join("");
}

function zaehler() {
  const s = zustand.status;
  if (!s) return;
  $("#z-wuensche").textContent = s.wuensche.offen || "";
  $("#z-aktivitaet").textContent = s.sender.aktiv || "";
}

function werkzeug() {
  const seite = SEITEN[zustand.seite];
  const links = seite.werkzeug.map((w, i) => w === "|"
    ? '<span class="trenner"></span>'
    : `<button class="wz" data-i="${i}" title="${esc(w[1])}">${sym(w[0])}<span>${esc(w[1])}</span></button>`
  ).join("");
  const f = zustand.filter[seite.name] ?? "alle";
  const rechts = seite.filter
    ? `<label class="wz-filter">${sym("filter")}<select id="filter" aria-label="Filter">${
        seite.filter.map(([k, t]) => `<option value="${k}"${k === f ? " selected" : ""}>${esc(t)}</option>`).join("")
      }</select></label>`
    : "";
  $("#werkzeug").innerHTML = `<div class="wz-links">${links}</div><div class="wz-rechts">${rechts}</div>`;
}

function vergleiche(a, b) {
  if (a === b) return 0;
  if (typeof a === "number" && typeof b === "number") return a - b;
  return String(a).localeCompare(String(b), "de", { numeric: true, sensitivity: "base" });
}

function inhalt() {
  const seite = SEITEN[zustand.seite];
  const s = zustand.status;
  const ziel = $("#inhalt");
  if (!s) return;
  if (seite.zeichnen) { ziel.innerHTML = seite.zeichnen(s); return; }

  const f = zustand.filter[seite.name] ?? "alle";
  const q = zustand.suche.trim().toLowerCase();
  let zeilen = zustand.daten
    .filter((z) => !seite.filtern || seite.filtern(z, f))
    .filter((z) => !q || seite.suchtext(z).toLowerCase().includes(q));

  const sort = zustand.sortierung[seite.name];
  if (sort) {
    const wert = seite.spalten[sort.spalte].wert;
    zeilen = [...zeilen].sort((a, b) => vergleiche(wert(a), wert(b)) * (sort.ab ? -1 : 1));
  }

  // Was nicht mehr zu sehen ist, ist auch nicht mehr markiert. Sonst
  // loeschte ein Klick Zeilen, die man gerade weggefiltert hat.
  zustand.sichtbar = zeilen.map((z) => String(z.id));
  const sichtbar = new Set(zustand.sichtbar);
  for (const id of [...zustand.markiert]) if (!sichtbar.has(id)) zustand.markiert.delete(id);

  const hinweise = (seite.hinweise?.(s) ?? []).map(([art, text]) =>
    `<div class="hinweis ${art}">${sym("info")}<div>${esc(text)}</div></div>`).join("");

  if (!zeilen.length) {
    const text = zustand.daten.length ? "Nichts passt zu Filter oder Suche." : seite.leer(s);
    ziel.innerHTML = `${hinweise}<p class="leer">${esc(text)}</p>`;
    auswahlLeiste();
    return;
  }

  const markierbar = !!seite.aktionen;
  const alle = zeilen.every((z) => zustand.markiert.has(String(z.id)));
  const kopf = seite.spalten.map((sp, i) => `
    <th class="${sp.klasse ?? ""}${sp.wert ? " sortierbar" : ""}" data-spalte="${i}">${esc(sp.titel)}${
      sort?.spalte === i ? sym(sort.ab ? "ab" : "auf") : ""}</th>`).join("");
  const rumpf = zeilen.map((z) => {
    const id = String(z.id);
    const m = zustand.markiert.has(id);
    return `<tr data-id="${esc(id)}"${m ? ' class="markiert"' : ""}>${
      markierbar ? `<td class="schmal"><input type="checkbox" data-marke ${m ? "checked" : ""} aria-label="Markieren"></td>` : ""
    }${seite.spalten.map((sp) => `<td class="${sp.klasse ?? ""}">${sp.zelle(z)}</td>`).join("")}</tr>`;
  }).join("");

  ziel.innerHTML = `${hinweise}
    <div class="tabelle-huelle"><table class="tabelle">
      <thead><tr>${markierbar
        ? `<th class="schmal"><input type="checkbox" data-alle ${alle ? "checked" : ""} aria-label="Alle markieren"></th>`
        : ""}${kopf}</tr></thead>
      <tbody>${rumpf}</tbody>
    </table></div>
    <div class="fuss-zahl">${zeilen.length === zustand.daten.length
      ? `${zeilen.length} Einträge` : `${zeilen.length} von ${zustand.daten.length} Einträgen`}</div>`;
  auswahlLeiste();
}

function auswahlLeiste() {
  const seite = SEITEN[zustand.seite];
  const n = zustand.markiert.size;
  const leiste = $("#auswahl");
  const zeigen = !!seite.aktionen && n > 0;
  leiste.hidden = !zeigen;
  document.body.classList.toggle("mit-auswahl", zeigen);
  if (!zeigen) return;
  leiste.innerHTML = `<span class="anzahl">${n} markiert</span>
    <div class="knoepfe">${seite.aktionen.map(([symbol, text, , art], i) =>
      `<button class="knopf ${art ?? ""}" data-i="${i}">${sym(symbol)}${esc(text)}</button>`).join("")}
      <button class="knopf" data-weg>Markierung aufheben</button></div>`;
}

// ------------------------------------------------------------- Ereignisse
$("#werkzeug").addEventListener("click", (e) => {
  const knopf = e.target.closest("button.wz");
  if (!knopf) return;
  SEITEN[zustand.seite].werkzeug[Number(knopf.dataset.i)][2](knopf);
});
$("#werkzeug").addEventListener("change", (e) => {
  if (e.target.id !== "filter") return;
  zustand.filter[zustand.seite] = e.target.value;
  inhalt();
});

$("#inhalt").addEventListener("click", async (e) => {
  const seite = SEITEN[zustand.seite];

  const th = e.target.closest("th.sortierbar");
  if (th) {
    const i = Number(th.dataset.spalte);
    const alt = zustand.sortierung[seite.name];
    zustand.sortierung[seite.name] = { spalte: i, ab: alt?.spalte === i ? !alt.ab : false };
    inhalt();
    return;
  }

  if (e.target.matches("[data-alle]")) {
    if (e.target.checked) zustand.sichtbar.forEach((id) => zustand.markiert.add(id));
    else zustand.markiert.clear();
    inhalt();
    return;
  }

  if (e.target.matches("[data-marke]")) {
    const id = e.target.closest("tr").dataset.id;
    const an = e.target.checked;
    // Umschalttaste markiert den ganzen Bereich seit dem letzten Klick,
    // wie in Sonarr. Bei hundert Sendern der Unterschied zwischen einem
    // und hundert Klicks.
    if (e.shiftKey && zustand.letzteMarke !== null) {
      const von = zustand.sichtbar.indexOf(zustand.letzteMarke);
      const bis = zustand.sichtbar.indexOf(id);
      if (von >= 0 && bis >= 0) {
        for (const x of zustand.sichtbar.slice(Math.min(von, bis), Math.max(von, bis) + 1)) {
          if (an) zustand.markiert.add(x); else zustand.markiert.delete(x);
        }
      }
    }
    if (an) zustand.markiert.add(id); else zustand.markiert.delete(id);
    zustand.letzteMarke = id;
    inhalt();
    return;
  }

  const aktion = e.target.closest("[data-aktion]");
  if (aktion?.dataset.aktion === "umschalten") {
    const station = zustand.daten.find((s) => s.id === aktion.dataset.id);
    if (!station) return;
    aktion.classList.toggle("an");
    await mitFehler(() => api("/api/stations/enabled", "POST",
      { ids: [station.id], enabled: !station.enabled }));
    neuLaden();
  }
});

$("#auswahl").addEventListener("click", (e) => {
  const knopf = e.target.closest("button");
  if (!knopf) return;
  if (knopf.hasAttribute("data-weg")) {
    zustand.markiert.clear();
    inhalt();
    return;
  }
  const aktion = SEITEN[zustand.seite].aktionen[Number(knopf.dataset.i)];
  aktion[2]([...zustand.markiert]);
});

$("#suche").addEventListener("input", (e) => {
  zustand.suche = e.target.value;
  inhalt();
});

$("#menue").addEventListener("click", () => document.body.classList.toggle("leiste-offen"));
$("#leiste-schatten").addEventListener("click", () => document.body.classList.remove("leiste-offen"));

// ------------------------------------------------------ Laden und Seiten
let ladeNummer = 0;

async function neuLaden(knopf) {
  const seite = SEITEN[zustand.seite];
  const nummer = ++ladeNummer;
  knopf?.classList.add("dreht");
  try {
    const [status, daten] = await Promise.all([
      api("/api/status"),
      seite.laden ? seite.laden() : Promise.resolve([]),
    ]);
    // Eine langsame Antwort fuer die vorige Seite darf die neue nicht
    // ueberschreiben.
    if (nummer !== ladeNummer) return;
    zustand.status = status;
    zustand.daten = daten ?? [];
    $("#verbindung").hidden = true;
  } catch {
    if (nummer === ladeNummer) $("#verbindung").hidden = false;
    return;
  } finally {
    knopf?.classList.remove("dreht");
  }
  zaehler();
  inhalt();
}

function seiteWechseln() {
  const gewuenscht = location.hash.replace(/^#\/?/, "");
  const name = SEITEN[gewuenscht] ? gewuenscht : "wuensche";
  if (name !== zustand.seite) {
    zustand.seite = name;
    zustand.daten = [];
    zustand.markiert.clear();
    zustand.letzteMarke = null;
    zustand.suche = "";
    $("#suche").value = "";
    $(".suche").hidden = !!SEITEN[name].zeichnen;
    document.title = `${SEITEN[name].titel} – aircheckarr`;
    document.querySelectorAll("#navigation a").forEach((a) =>
      a.classList.toggle("aktiv", a.dataset.seite === name));
    document.body.classList.remove("leiste-offen");
    werkzeug();
    auswahlLeiste();
    $("#inhalt").innerHTML = '<p class="laedt">wird geladen …</p>';
  }
  neuLaden();
}

navigation();
window.addEventListener("hashchange", seiteWechseln);
seiteWechseln();

// Selbst auffrischen, aber nicht mitten in einem Dialog: dort koennte
// gerade jemand Sender ankreuzen, und die Seite dahinter wuerde flackern.
setInterval(() => {
  if ($("#dialog").hidden && document.visibilityState === "visible") neuLaden();
}, 10000);
