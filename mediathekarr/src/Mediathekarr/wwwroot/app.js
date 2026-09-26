// Zwei Ansichten, beide lesen nur. Gesteuert wird der Dienst aus Sonarr
// und Radarr heraus, nicht von hier.
const $ = (id) => document.getElementById(id);

async function auftraege() {
  try {
    const d = await (await fetch("api/status")).json();
    if (!d.jobs.length) { $("auftraege").className = "leer"; $("auftraege").textContent = "Noch nichts abgeholt."; return; }
    $("auftraege").className = "";
    $("auftraege").innerHTML = `<table><thead><tr>
      <th>Release</th><th>Art</th><th>Status</th><th class="zahl">MB</th><th>Wann</th>
      </tr></thead><tbody>${d.jobs.map((j) => `<tr>
        <td class="name">${esc(j.releaseName)}${j.error ? `<br><small>${esc(j.error)}</small>` : ""}</td>
        <td>${j.category === "tv" ? "Serie" : "Film"}</td>
        <td><span class="etikett ${j.status}">${j.status}</span></td>
        <td class="zahl">${j.mb}</td>
        <td>${esc(j.created)}</td></tr>`).join("")}</tbody></table>`;
  } catch { /* beim naechsten Durchlauf erneut */ }
}

$("suchform").addEventListener("submit", async (e) => {
  e.preventDefault();
  const q = $("suche").value.trim();
  if (!q) return;
  $("treffer").className = "leer";
  $("treffer").textContent = "Suche …";
  try {
    const r = await (await fetch("api/search?q=" + encodeURIComponent(q))).json();
    if (!r.length) { $("treffer").textContent = "Nichts gefunden. Kurze Beiträge werden bewusst ausgelassen."; return; }
    $("treffer").className = "";
    $("treffer").innerHTML = `<table><thead><tr>
      <th>Release-Name</th><th>Sender</th><th class="zahl">MB</th>
      </tr></thead><tbody>${r.map((x) => `<tr>
        <td class="name">${esc(x.name)}</td><td>${esc(x.channel)}</td>
        <td class="zahl">${x.mb}</td></tr>`).join("")}</tbody></table>`;
  } catch {
    $("treffer").textContent = "Die Abfrage hat nicht geklappt.";
  }
});

const esc = (s) => String(s ?? "").replace(/[&<>"]/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" }[c]));
auftraege();
setInterval(auftraege, 10000);
