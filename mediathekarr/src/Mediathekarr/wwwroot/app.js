"use strict";
// Oberflaeche von mediathekarr, gleich aufgebaut wie aircheckarr und
// Sonarr: Seiten ueber die Adresse (#/stoebern/SRF), Werkzeugleiste oben,
// Tabellen mit Markieren, Dialoge statt Browser-Popups.
//
// Anders als in aircheckarr wird hier nicht im Browser gefiltert: eine
// Mediathek hat Zehntausende Beitraege. Suche, Sender und Sendung gehen als
// Abfrage an den Dienst, und die Liste waechst seitenweise mit "Mehr laden".
//
// Die eine Falle: Titel und Beschreibungen kommen von den Sendern und sind
// fremder Text. Sie gehen nur ueber esc() ins HTML.

// ------------------------------------------------------------ Werkzeuge
const $ = (sel, wurzel = document) => wurzel.querySelector(sel);

const esc = (s) => String(s ?? "").replace(/[&<>"']/g,
  (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));

const SYMBOLE = {
  stoebern: '<rect x="2" y="4" width="20" height="14" rx="2"/><path d="M8 22h8M12 18v4"/><path d="M10 8.5v5l4.5-2.5z"/>',
  downloads: '<path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4"/><path d="M7 10l5 5 5-5M12 15V3"/>',
  indexer: '<circle cx="11" cy="11" r="7"/><path d="M21 21l-4.3-4.3"/>',
  system: '<circle cx="12" cy="12" r="10"/><path d="M12 16v-4M12 8h.01"/>',
  aktualisieren: '<path d="M23 4v6h-6M1 20v-6h6"/><path d="M3.5 9a9 9 0 0 1 14.9-3.4L23 10M1 14l4.6 4.4A9 9 0 0 0 20.5 15"/>',
  laden: '<path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4"/><path d="M7 10l5 5 5-5M12 15V3"/>',
  zu: '<path d="M18 6L6 18M6 6l12 12"/>',
  info: '<circle cx="12" cy="12" r="10"/><path d="M12 16v-4M12 8h.01"/>',
  filter: '<path d="M22 3H2l8 9.5V19l4 2v-8.5L22 3z"/>',
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

const dauer = (sek) => {
  const m = Math.round((sek || 0) / 60);
  return m >= 60 ? `${Math.floor(m / 60)} h ${String(m % 60).padStart(2, "0")}` : `${m} min`;
};

const groesse = (bytes) => bytes >= 1e9
  ? `${(bytes / 1e9).toFixed(1)} GB`
  : bytes > 0 ? `${Math.round(bytes / 1e6)} MB` : "–";

function meldung(text, art = "info") {
  const el = document.createElement("div");
  el.className = `meldung ${art}`;
  el.textContent = text;
  $("#meldungen").append(el);
  setTimeout(() => el.classList.add("weg"), 5000);
  setTimeout(() => el.remove(), 5500);
}

async function mitFehler(arbeit) {
  try { return await arbeit(); }
  catch (e) { meldung(e.message, "gefahr"); return undefined; }
}

// ------------------------------------------------------------- Zustand
const SEITENGROESSE = 50;
const zustand = {
  seite: null,
  sender: "",      // leer = alle Mediatheken
  sendung: "",     // Sendereihe, auf die eingeschraenkt ist
  suche: "",
  kurz: false,
  status: null,
  treffer: [],
  gesamt: 0,
  weiter: 0,       // Position der naechsten Seite, vom Dienst vorgegeben
  markiert: new Set(),
  letzteMarke: null,
  downloads: [],
  indexer: null,
};

const SEITEN = {
  stoebern: { titel: "Stöbern", symbol: "stoebern", suche: "In dieser Mediathek suchen" },
  downloads: { titel: "Downloads", symbol: "downloads" },
  indexer: { titel: "Indexer-Test", symbol: "indexer", suche: "Wie Sonarr suchen, z. B. Tatort" },
  system: { titel: "System", symbol: "system" },
};

// -------------------------------------------------------------- Adresse
// #/stoebern/SRF?sendung=Tagesschau - so funktionieren Zurueck-Knopf und
// Lesezeichen wie erwartet.
function adresseLesen() {
  const [pfad, abfrage = ""] = location.hash.replace(/^#\/?/, "").split("?");
  const [seite, sender = ""] = pfad.split("/");
  const p = new URLSearchParams(abfrage);
  return {
    seite: SEITEN[seite] ? seite : "stoebern",
    sender: decodeURIComponent(sender),
    sendung: p.get("sendung") ?? "",
  };
}

function adresse(sender, sendung) {
  const s = sender ? `/${encodeURIComponent(sender)}` : "";
  const q = sendung ? `?sendung=${encodeURIComponent(sendung)}` : "";
  return `#/stoebern${s}${q}`;
}

// -------------------------------------------------------------- Zeichnen
function navigation() {
  const s = zustand.status;
  const sender = s?.sender ?? [];
  const aktiv = (x) => (x ? " aktiv" : "");
  const unter = zustand.seite === "stoebern"
    ? `<div class="unter-titel">Mediatheken</div>
       <a class="unter${aktiv(!zustand.sender)}" href="${adresse("", "")}">Alle</a>
       ${sender.map((c) => `<a class="unter${aktiv(zustand.sender === c)}" href="${adresse(c, "")}">${esc(c)}</a>`).join("")}`
    : "";
  $("#navigation").innerHTML = `
    <a href="#/stoebern" class="${zustand.seite === "stoebern" ? "aktiv" : ""}">${sym("stoebern")}<span>Stöbern</span></a>
    ${unter}
    <a href="#/downloads" class="${zustand.seite === "downloads" ? "aktiv" : ""}">${sym("downloads")}<span>Downloads</span>
      <span class="zaehler">${s?.laufend || ""}</span></a>
    <a href="#/indexer" class="${zustand.seite === "indexer" ? "aktiv" : ""}">${sym("indexer")}<span>Indexer-Test</span></a>
    <a href="#/system" class="${zustand.seite === "system" ? "aktiv" : ""}">${sym("system")}<span>System</span></a>`;
}

function werkzeug() {
  let links = `<button class="wz" data-wz="aktualisieren" title="Aktualisieren">${sym("aktualisieren")}<span>Aktualisieren</span></button>`;
  let rechts = "";
  if (zustand.seite === "stoebern") {
    links = `<button class="wz" data-wz="markierte" title="Markierte herunterladen">${sym("laden")}<span>Herunterladen</span></button>
      <span class="trenner"></span>${links}`;
    rechts = `<label class="wz-filter" title="Beiträge unter ${zustand.status?.mindestdauer ?? 600} s zeigen: Nachrichten, Ausschnitte">
        <input type="checkbox" id="kurz" ${zustand.kurz ? "checked" : ""}> Auch kurze Beiträge</label>`;
  }
  $("#werkzeug").innerHTML = `<div class="wz-links">${links}</div><div class="wz-rechts">${rechts}</div>`;
}

function zustandEtikett(t) {
  if (t.vorhanden) return etikett("Vorhanden", "erfolg", "Liegt schon in der Plex-Bibliothek");
  if (t.laedt) return etikett("Lädt", "marke");
  return `<button class="knopf" data-laden="${esc(t.id)}">${sym("laden")}Laden</button>`;
}

function stoebernZeichnen() {
  const ziel = $("#inhalt");
  const kopf = [];
  const wo = zustand.sender || "allen Mediatheken";
  if (zustand.sendung) {
    kopf.push(`<div class="hinweis">${sym("filter")}<div>Sendung <strong>${esc(zustand.sendung)}</strong>
      in ${esc(wo)}. <a href="${adresse(zustand.sender, "")}">Alle Sendungen zeigen</a></div></div>`);
  }
  if (!zustand.treffer.length) {
    ziel.innerHTML = `${kopf.join("")}<p class="leer">${zustand.suche
      ? "Nichts gefunden. Anders schreiben oder „Auch kurze Beiträge“ einschalten."
      : "Hier ist gerade nichts."}</p>`;
    auswahlLeiste();
    return;
  }

  const alle = zustand.treffer.filter((t) => !t.vorhanden).every((t) => zustand.markiert.has(t.id));
  const zeilen = zustand.treffer.map((t) => {
    const m = zustand.markiert.has(t.id);
    const text = t.description ? t.description.slice(0, 160) + (t.description.length > 160 ? " …" : "") : "";
    return `<tr data-id="${esc(t.id)}"${m ? ' class="markiert"' : ""}>
      <td class="schmal"><input type="checkbox" data-marke ${m ? "checked" : ""} ${t.vorhanden ? "disabled" : ""}
          aria-label="Markieren"></td>
      <td class="schmal weg-klein">${esc(datum(t.published))}</td>
      <td class="schmal weg-klein">${zustand.sender ? "" : `${esc(t.channel)}<br>`}
        <button class="link" data-sendung="${esc(t.topic)}" title="Nur diese Sendung">${esc(t.topic)}</button></td>
      <td>${esc(t.title)}${text ? `<div class="klein" title="${esc(t.description)}">${esc(text)}</div>` : ""}</td>
      <td class="zahl schmal">${dauer(t.duration)}</td>
      <td class="schmal weg-klein">${t.hd ? etikett("HD", "info") : etikett("SD", "rahmen")}
        ${t.untertitel ? etikett("UT", "rahmen", "Mit Untertiteln") : ""}</td>
      <td class="schmal">${zustandEtikett(t)}</td>
    </tr>`;
  }).join("");

  const mehr = zustand.weiter < zustand.gesamt
    ? `<div class="mehr"><button class="knopf" id="mehr">Mehr laden</button></div>` : "";
  const gesamt = zustand.gesamt >= 10000 ? "über 10 000" : zustand.gesamt;
  ziel.innerHTML = `${kopf.join("")}
    <div class="tabelle-huelle"><table class="tabelle">
      <thead><tr>
        <th class="schmal"><input type="checkbox" data-alle ${alle ? "checked" : ""} aria-label="Alle markieren"></th>
        <th class="weg-klein">Gesendet</th><th class="weg-klein">Sendung</th><th>Titel</th>
        <th class="zahl">Dauer</th><th class="weg-klein">Qualität</th><th></th>
      </tr></thead>
      <tbody>${zeilen}</tbody>
    </table></div>
    ${mehr}
    <div class="fuss-zahl">${zustand.treffer.length} Beiträge angezeigt, ${gesamt} insgesamt, neueste zuerst</div>`;
  auswahlLeiste();
}

function downloadsZeichnen() {
  const ziel = $("#inhalt");
  if (!zustand.downloads.length) {
    ziel.innerHTML = `<p class="leer">Noch nichts heruntergeladen. Unter „Stöbern“ eine Sendung
      auswählen, oder Sonarr und Radarr holen hier selbst ab.</p>`;
    return;
  }
  const art = { direkt: "Direkt", tv: "Sonarr", movies: "Radarr" };
  const stand = (d) => {
    if (d.status === "fertig") return etikett("Fertig", "erfolg");
    if (d.status === "fehlgeschlagen") return etikett("Fehlgeschlagen", "gefahr", d.fehler);
    if (d.status === "abgebrochen") return etikett("Abgebrochen", "warnung", "Der Dienst wurde währenddessen neu gestartet");
    if (d.status === "wartet") return etikett("Wartet");
    const pr = d.gesamt > 0 ? Math.min(100, Math.round((d.bytes / d.gesamt) * 100)) : null;
    return pr === null ? etikett("Lädt", "marke")
      : `<span class="balken" title="${pr} %"><span style="width:${pr}%"></span></span> ${pr} %`;
  };
  ziel.innerHTML = `<div class="tabelle-huelle"><table class="tabelle">
    <thead><tr><th class="schmal">Erstellt</th><th>Name</th><th class="schmal weg-klein">Art</th>
      <th class="zahl schmal">Größe</th><th class="schmal">Stand</th></tr></thead>
    <tbody>${zustand.downloads.map((d) => `<tr>
      <td class="schmal">${esc(datum(d.erstellt))}</td>
      <td>${esc(d.name)}</td>
      <td class="schmal weg-klein">${etikett(art[d.art] ?? d.art, d.art === "direkt" ? "marke" : "rahmen")}</td>
      <td class="zahl schmal">${groesse(d.bytes)}</td>
      <td class="schmal">${stand(d)}</td></tr>`).join("")}</tbody>
  </table></div>`;
}

function indexerZeichnen() {
  const ziel = $("#inhalt");
  const hilfe = `<div class="hinweis">${sym("info")}<div>Zeigt, was Sonarr und Radarr bei einer Suche
    angeboten bekämen, samt Release-Namen. Zum Prüfen, ob eine Sendereihe überhaupt gefunden wird.
    Oben einen Begriff eingeben.</div></div>`;
  if (zustand.indexer === null) { ziel.innerHTML = hilfe; return; }
  if (!zustand.indexer.length) { ziel.innerHTML = `${hilfe}<p class="leer">Keine Treffer.</p>`; return; }
  ziel.innerHTML = `${hilfe}<div class="tabelle-huelle"><table class="tabelle">
    <thead><tr><th>Release-Name</th><th class="schmal">Sender</th><th class="zahl schmal">Größe</th></tr></thead>
    <tbody>${zustand.indexer.map((r) => `<tr><td>${esc(r.name)}</td>
      <td class="schmal">${esc(r.channel)}</td><td class="zahl schmal">${r.mb} MB</td></tr>`).join("")}</tbody>
  </table></div>`;
}

function systemZeichnen() {
  const s = zustand.status;
  const zeile = (k, v) => `<dt>${esc(k)}</dt><dd>${v}</dd>`;
  $("#inhalt").innerHTML = `
    <section class="kasten"><h2>Über</h2><dl class="eigenschaften">
      ${zeile("Version", esc(s.version ?? "–"))}
      ${zeile("Quelle", `<a href="https://mediathekviewweb.de" target="_blank" rel="noopener">MediathekViewWeb</a>`)}
      ${zeile("Mediatheken", esc(s.sender.join(", ")))}
      ${zeile("Mindestdauer", `${Math.round(s.mindestdauer / 60)} Minuten, kürzere nur mit „Auch kurze Beiträge“`)}
    </dl></section>
    <section class="kasten"><h2>Ordner</h2><dl class="eigenschaften">
      ${zeile("Direkt-Downloads", esc(s.pfade.bibliothek) + " (Plex-Bibliothek „Mediathek“)")}
      ${zeile("Blackhole", esc(s.pfade.blackhole) + " (Sonarr und Radarr legen hier ab)")}
      ${zeile("Fertig", esc(s.pfade.fertig) + " (hier holen Sonarr und Radarr ab)")}
    </dl></section>
    <p class="hilfe">Die Einstellungen stehen in der .env, siehe docs/11-mediathek.md.</p>`;
}

function inhalt() {
  if (!zustand.status) return;
  ({ stoebern: stoebernZeichnen, downloads: downloadsZeichnen,
     indexer: indexerZeichnen, system: systemZeichnen })[zustand.seite]();
}

function auswahlLeiste() {
  const n = zustand.markiert.size;
  const leiste = $("#auswahl");
  const zeigen = zustand.seite === "stoebern" && n > 0;
  leiste.hidden = !zeigen;
  document.body.classList.toggle("mit-auswahl", zeigen);
  if (!zeigen) return;
  leiste.innerHTML = `<span class="anzahl">${n} markiert</span>
    <div class="knoepfe">
      <button class="knopf primaer" data-aktion="laden">${sym("laden")}Herunterladen</button>
      <button class="knopf" data-aktion="weg">Markierung aufheben</button></div>`;
}

// ------------------------------------------------------------- Laden
let ladeNummer = 0;

async function statusHolen() {
  try {
    zustand.status = await api("/api/status");
    $("#verbindung").hidden = true;
  } catch {
    $("#verbindung").hidden = false;
  }
  navigation();
}

async function stoebernLaden(anhaengen = false) {
  const nummer = ++ladeNummer;
  const q = new URLSearchParams({
    offset: String(anhaengen ? zustand.weiter : 0),
    size: String(SEITENGROESSE),
  });
  if (zustand.sender) q.set("channel", zustand.sender);
  if (zustand.sendung) q.set("topic", zustand.sendung);
  if (zustand.suche.trim()) q.set("q", zustand.suche.trim());
  if (zustand.kurz) q.set("kurz", "true");
  if (!anhaengen) $("#inhalt").innerHTML = '<p class="laedt">Mediathek wird durchsucht …</p>';

  const d = await mitFehler(() => api(`/api/browse?${q}`));
  // Eine langsame Antwort auf eine alte Suche darf die neue nicht ueberschreiben.
  if (!d || nummer !== ladeNummer) return;
  // Doppelte auch ueber Seitengrenzen hinweg weglassen.
  const schon = new Set(anhaengen ? zustand.treffer.map((t) => t.id) : []);
  const neu = d.items.filter((t) => !schon.has(t.id));
  zustand.treffer = anhaengen ? zustand.treffer.concat(neu) : neu;
  zustand.gesamt = d.total;
  zustand.weiter = d.weiter;
  if (!anhaengen) zustand.markiert.clear();
  inhalt();
}

async function downloadsLaden() {
  const d = await mitFehler(() => api("/api/downloads"));
  if (d) zustand.downloads = d;
  if (zustand.seite === "downloads") inhalt();
}

async function indexerLaden() {
  const q = zustand.suche.trim();
  if (!q) { zustand.indexer = null; inhalt(); return; }
  $("#inhalt").innerHTML = '<p class="laedt">Wird gesucht …</p>';
  const d = await mitFehler(() => api(`/api/search?q=${encodeURIComponent(q)}`));
  zustand.indexer = d ?? [];
  inhalt();
}

function neuLaden() {
  statusHolen().then(() => {
    if (zustand.seite === "stoebern") stoebernLaden();
    else if (zustand.seite === "downloads") downloadsLaden();
    else if (zustand.seite === "indexer") indexerLaden();
    else inhalt();
  });
}

async function herunterladen(ids) {
  const r = await mitFehler(() => api("/api/downloads", "POST", { ids }));
  if (!r) return;
  const teile = [];
  if (r.eingereiht) teile.push(`${r.eingereiht} in die Warteschlange gestellt`);
  if (r.vorhanden) teile.push(`${r.vorhanden} schon vorhanden oder unterwegs`);
  if (r.unbekannt) teile.push(`${r.unbekannt} nicht mehr bekannt, bitte neu laden`);
  meldung(teile.join(", ") + ".", r.eingereiht ? "erfolg" : "warnung");
  for (const t of zustand.treffer) if (ids.includes(t.id) && !t.vorhanden) t.laedt = true;
  zustand.markiert.clear();
  inhalt();
  statusHolen();
}

// ------------------------------------------------------------- Ereignisse
$("#werkzeug").addEventListener("click", (e) => {
  const k = e.target.closest("[data-wz]");
  if (!k) return;
  if (k.dataset.wz === "aktualisieren") neuLaden();
  if (k.dataset.wz === "markierte") {
    if (zustand.markiert.size) herunterladen([...zustand.markiert]);
    else meldung("Erst links Sendungen ankreuzen.", "warnung");
  }
});
$("#werkzeug").addEventListener("change", (e) => {
  if (e.target.id !== "kurz") return;
  zustand.kurz = e.target.checked;
  stoebernLaden();
});

$("#inhalt").addEventListener("click", (e) => {
  const laden = e.target.closest("[data-laden]");
  if (laden) { herunterladen([laden.dataset.laden]); return; }

  const sendung = e.target.closest("[data-sendung]");
  if (sendung) {
    const t = zustand.treffer.find((x) => x.id === sendung.closest("tr").dataset.id);
    // Eine Sendereihe gehoert zu einem Sender; von "Alle" aus dorthin wechseln.
    location.hash = adresse(zustand.sender || t?.channel || "", sendung.dataset.sendung);
    return;
  }

  if (e.target.id === "mehr") { stoebernLaden(true); return; }

  if (e.target.matches("[data-alle]")) {
    for (const t of zustand.treffer) {
      if (t.vorhanden) continue;
      if (e.target.checked) zustand.markiert.add(t.id); else zustand.markiert.delete(t.id);
    }
    inhalt();
    return;
  }

  if (e.target.matches("[data-marke]")) {
    const id = e.target.closest("tr").dataset.id;
    const an = e.target.checked;
    // Umschalttaste markiert den ganzen Bereich seit dem letzten Klick.
    if (e.shiftKey && zustand.letzteMarke) {
      const ids = zustand.treffer.filter((t) => !t.vorhanden).map((t) => t.id);
      const von = ids.indexOf(zustand.letzteMarke);
      const bis = ids.indexOf(id);
      if (von >= 0 && bis >= 0) {
        for (const x of ids.slice(Math.min(von, bis), Math.max(von, bis) + 1)) {
          if (an) zustand.markiert.add(x); else zustand.markiert.delete(x);
        }
      }
    }
    if (an) zustand.markiert.add(id); else zustand.markiert.delete(id);
    zustand.letzteMarke = id;
    inhalt();
  }
});

$("#auswahl").addEventListener("click", (e) => {
  const k = e.target.closest("[data-aktion]");
  if (!k) return;
  if (k.dataset.aktion === "laden") herunterladen([...zustand.markiert]);
  else { zustand.markiert.clear(); inhalt(); }
});

// Suchen erst, wenn eine Weile nichts mehr getippt wurde: jede Abfrage
// geht an MediathekViewWeb, und deren Betreiber zahlen den Server selbst.
let tippPause;
$("#suche").addEventListener("input", (e) => {
  zustand.suche = e.target.value;
  clearTimeout(tippPause);
  tippPause = setTimeout(() => {
    if (zustand.seite === "stoebern") stoebernLaden();
    else if (zustand.seite === "indexer") indexerLaden();
  }, 450);
});

$("#menue").addEventListener("click", () => document.body.classList.toggle("leiste-offen"));
$("#leiste-schatten").addEventListener("click", () => document.body.classList.remove("leiste-offen"));

// ------------------------------------------------------------- Seiten
function seiteWechseln() {
  const a = adresseLesen();
  const neueSeite = a.seite !== zustand.seite;
  zustand.seite = a.seite;
  zustand.sender = a.sender;
  zustand.sendung = a.sendung;
  if (neueSeite) {
    zustand.suche = "";
    $("#suche").value = "";
    zustand.indexer = null;
  }
  const s = SEITEN[a.seite];
  $(".suche").hidden = !s.suche;
  $("#suche").placeholder = s.suche ?? "";
  document.title = `${zustand.sender || s.titel} – mediathekarr`;
  document.body.classList.remove("leiste-offen");
  zustand.markiert.clear();
  navigation();
  werkzeug();
  auswahlLeiste();
  neuLaden();
}

window.addEventListener("hashchange", seiteWechseln);
seiteWechseln();

// Downloads laufen sichtbar mit. Das Stoebern bleibt still: dort wuerde ein
// Auffrischen mitten im Ankreuzen die Liste unter der Maus neu aufbauen.
setInterval(() => {
  if (document.visibilityState !== "visible") return;
  if (zustand.seite === "downloads") downloadsLaden();
  statusHolen();
}, 3000);
