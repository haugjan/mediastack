const hole = async (pfad) => (await fetch(pfad)).json();
const sende = async (pfad, methode, koerper) =>
  fetch(pfad, {
    method: methode,
    headers: { "Content-Type": "application/json" },
    body: koerper ? JSON.stringify(koerper) : undefined,
  });

// ------------------------------------------------------------------ Reiter
document.querySelectorAll("nav button").forEach((b) =>
  b.addEventListener("click", () => {
    document.querySelectorAll("nav button").forEach((x) => x.classList.remove("aktiv"));
    b.classList.add("aktiv");
    document.querySelectorAll("main section").forEach((s) => (s.hidden = true));
    document.getElementById(b.dataset.tab).hidden = false;
    zeichne();
  })
);

// ----------------------------------------------------------------- Zustand
async function zustand() {
  const s = await hole("/api/status");
  const laeuft = s.laeuft.length ? ` · hört gerade: ${s.laeuft.join(", ")}` : "";
  document.getElementById("status").textContent =
    `${s.sender.tauglich} taugliche von ${s.sender.gemessen} gemessenen Sendern · ` +
    `${s.wuensche.offen} offene Wünsche, ${s.wuensche.erfuellt} erfüllt · ` +
    `Filter: ab ${s.filter.mindestbitrate} kbit/s, ${s.filter.codecs.join("/")}` + laeuft;
}

// ----------------------------------------------------------------- Wünsche
async function wuensche() {
  const liste = await hole("/api/wishes");
  const t = document.getElementById("wunsch-tabelle");
  if (!liste.length) {
    t.innerHTML = '<tr><td class="leer">Noch nichts gewünscht.</td></tr>';
    return;
  }
  t.innerHTML =
    "<tr><th>Interpret</th><th>Titel</th><th class='weg-klein'>Album</th>" +
    "<th class='weg-klein'>Quelle</th><th>Stand</th><th></th></tr>" +
    liste.map((w) => `
      <tr>
        <td>${esc(w.artist)}</td>
        <td>${esc(w.title)}</td>
        <td class="weg-klein">${esc(w.album ?? "")}</td>
        <td class="weg-klein">${w.source === "lidarr" ? "Lidarr" : "Hand"}</td>
        <td>${w.fulfilledAt
            ? '<span class="gut">erfüllt</span>'
            : '<span class="schwach">wartet</span>'}</td>
        <td><button class="klein" onclick="loeschen(${w.id})">löschen</button></td>
      </tr>`).join("");
}

window.loeschen = async (id) => {
  await sende(`/api/wishes/${id}`, "DELETE");
  zeichne();
};

document.getElementById("wunsch-form").addEventListener("submit", async (e) => {
  e.preventDefault();
  const f = new FormData(e.target);
  const antwort = await (await sende("/api/wishes", "POST", {
    artist: f.get("artist"), title: f.get("title"), album: f.get("album"),
  })).json();
  if (antwort.bekannt) alert("Steht schon auf der Liste.");
  e.target.reset();
  zeichne();
});

document.getElementById("aus-lidarr").addEventListener("click", async () => {
  const r = await sende("/api/wishes/import-lidarr", "POST");
  const d = await r.json();
  alert(r.ok
    ? `${d.gefunden} Titel gefunden, ${d.uebernommen} neu übernommen.`
    : d.fehler);
  zeichne();
});

// ------------------------------------------------------------------ Sender
async function sender() {
  const nurTauglich = document.getElementById("nur-tauglich").checked;
  const liste = await hole(`/api/stations?onlyUsable=${nurTauglich}`);
  const t = document.getElementById("sender-tabelle");
  if (!liste.length) {
    t.innerHTML = '<tr><td class="leer">Noch kein Sender geholt.</td></tr>';
    return;
  }
  t.innerHTML =
    "<tr><th>Sender</th><th class='weg-klein'>Land</th><th class='zahl weg-klein'>Katalog</th>" +
    "<th class='zahl'>gemessen</th><th>Codec</th><th>Zustand</th><th></th></tr>" +
    liste.slice(0, 400).map((s) => `
      <tr>
        <td>${esc(s.name)}</td>
        <td class="weg-klein">${esc(s.country ?? "")}</td>
        <td class="zahl weg-klein schwach">${s.katalogBitrate || "–"}</td>
        <td class="zahl">${s.gemessenBitrate || "–"}</td>
        <td>${esc(s.gemessenCodec ?? "–")}</td>
        <td>${s.tauglich
            ? '<span class="gut">tauglich</span>'
            : `<span class="schlecht">${esc(s.fehler ?? "ungemessen")}</span>`}</td>
        <td><button class="klein" onclick="umschalten('${s.id}', ${!s.enabled})">
              ${s.enabled ? "aus" : "an"}</button></td>
      </tr>`).join("");
}

window.umschalten = async (id, wert) => {
  await sende(`/api/stations/${id}/enabled`, "POST", { enabled: wert });
  zeichne();
};

document.getElementById("katalog-form").addEventListener("submit", async (e) => {
  e.preventDefault();
  const f = new FormData(e.target);
  e.submitter.disabled = true;
  await sende("/api/stations/refresh", "POST", {
    tag: f.get("tag"), country: f.get("country"), limit: Number(f.get("limit")),
  });
  e.submitter.disabled = false;
  zeichne();
});
document.getElementById("nur-tauglich").addEventListener("change", zeichne);

// ------------------------------------------------------------- Mitschnitte
async function mitschnitte() {
  const liste = await hole("/api/captures");
  const t = document.getElementById("mitschnitt-tabelle");
  if (!liste.length) {
    t.innerHTML = '<tr><td class="leer">Noch nichts mitgeschnitten.</td></tr>';
    return;
  }
  t.innerHTML =
    "<tr><th>Wann</th><th>Interpret</th><th>Titel</th><th class='weg-klein'>Sender</th>" +
    "<th class='zahl'>Länge</th><th>Ergebnis</th></tr>" +
    liste.map((c) => `
      <tr>
        <td>${new Date(c.startedAt).toLocaleString("de-CH")}</td>
        <td>${esc(c.artist)}</td>
        <td>${esc(c.title)}</td>
        <td class="weg-klein">${esc(c.stationName)}</td>
        <td class="zahl">${Math.round(c.seconds)} s</td>
        <td>${c.state === 0 || c.state === "Recording"
            ? "läuft"
            : c.path
              ? '<span class="gut">abgelegt</span>'
              : `<span class="schlecht">${esc(c.reason ?? "verworfen")}</span>`}</td>
      </tr>`).join("");
}

const esc = (s) => String(s ?? "").replace(/[&<>"']/g,
  (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));

async function zeichne() {
  await zustand();
  const offen = document.querySelector("main section:not([hidden])").id;
  if (offen === "wuensche") await wuensche();
  else if (offen === "sender") await sender();
  else await mitschnitte();
}

zeichne();
setInterval(zeichne, 15000);
