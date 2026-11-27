"use strict";
const board = document.getElementById("board");
const statusLabel = document.getElementById("connection-status");
let refreshing = false;
let pending = false;
let submitting = false;
let lastMarkup;
const paused = () => document.hidden || submitting || board.contains(document.activeElement) ||
    document.querySelector("details[data-pause-refresh][open]");
async function refresh() {
    if (refreshing || paused()) { pending = true; return; }
    refreshing = true;
    pending = false;
    try {
        const response = await fetch(board.dataset.snapshot, { cache: "no-store", redirect: "manual" });
        if (response.type === "opaqueredirect" || response.status === 401 || response.status === 403) {
            statusLabel.textContent = "Sessão indisponível · recarregue";
            return;
        }
        if (!response.ok || !response.headers.get("Content-Type")?.startsWith("text/html")) return;
        const html = await response.text();
        if (paused()) { pending = true; return; }
        if (lastMarkup !== html) { board.innerHTML = html; lastMarkup = html; }
    } catch { /* The next poll retries. */ }
    finally { refreshing = false; }
}
document.addEventListener("submit", () => { submitting = true; });
window.addEventListener("pageshow", () => { submitting = false; });
document.addEventListener("visibilitychange", () => { if (!document.hidden) void refresh(); });
document.addEventListener("focusout", () => { if (pending) setTimeout(refresh, 0); });
document.addEventListener("toggle", () => { if (pending) void refresh(); }, true);
// Periodic reads capture commits by the worker and other web replicas even without a hub backplane.
setInterval(refresh, 15000);
if (window.signalR) {
    const conn = new signalR.HubConnectionBuilder()
        .withUrl("/hubs/board", { transport: signalR.HttpTransportType.ServerSentEvents })
        .withAutomaticReconnect().build();
    const fallback = () => { statusLabel.textContent = "Atualização periódica"; };
    const connected = () => { statusLabel.textContent = "Conectado · ao vivo"; void refresh(); };
    conn.on("Invalidate", refresh);
    conn.onreconnecting(fallback);
    conn.onreconnected(connected);
    conn.onclose(fallback);
    conn.start().then(connected).catch(fallback);
}
