// Shared helpers + the single SignalR connection used by every page.
(function () {
    "use strict";

    const TT = (window.TT = {});

    TT.api = async function (url, options) {
        const opts = Object.assign({ credentials: "same-origin", headers: {} }, options || {});
        opts.headers = Object.assign({ "X-WebText": "1" }, opts.headers);
        if (opts.json !== undefined) {
            opts.method = opts.method || "POST";
            opts.headers["Content-Type"] = "application/json";
            opts.body = JSON.stringify(opts.json);
            delete opts.json;
        }
        const res = await fetch(url, opts);
        if (res.headers.get("X-WebText-SignIn") === "required") {
            // Local sign-in session ended (idle timeout, signed out elsewhere, account changed).
            location.href = "/Account/Login?expired=1&ReturnUrl=" + encodeURIComponent(location.pathname + location.search);
            throw new Error("Please sign in again.");
        }
        let data = null;
        const text = await res.text();
        if (text) { try { data = JSON.parse(text); } catch { data = text; } }
        if (!res.ok) {
            const msg = (data && data.error) || res.statusText || "Request failed";
            throw new Error(msg);
        }
        return data;
    };

    TT.esc = function (s) {
        return String(s ?? "").replace(/[&<>"']/g, c =>
            ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[c]);
    };

    // Strip the DOMAIN\ prefix for display.
    TT.shortUser = u => (u || "").split("\\").pop();

    TT.fmtTime = function (iso) {
        if (!iso) return "";
        const d = new Date(iso);
        const now = new Date();
        const sameDay = d.toDateString() === now.toDateString();
        const yesterday = new Date(now); yesterday.setDate(now.getDate() - 1);
        const time = d.toLocaleTimeString([], { hour: "numeric", minute: "2-digit" });
        if (sameDay) return time;
        if (d.toDateString() === yesterday.toDateString()) return "Yesterday " + time;
        const sameYear = d.getFullYear() === now.getFullYear();
        return d.toLocaleDateString([], sameYear ? { month: "short", day: "numeric" }
                                               : { month: "short", day: "numeric", year: "numeric" }) + " " + time;
    };

    TT.fmtFull = iso => iso ? new Date(iso).toLocaleString() : "";

    // SMS segment counter matching the server's logic.
    const GSM = "@£$¥èéùìòÇ\nØø\rÅåΔ_ΦΓΛΩΠΨΣΘΞÆæßÉ !\"#¤%&'()*+,-./0123456789:;<=>?¡ABCDEFGHIJKLMNOPQRSTUVWXYZÄÖÑÜ§¿abcdefghijklmnopqrstuvwxyzäöñüà";
    const GSM_EXT = "^{}\\[~]|€";
    TT.segments = function (text) {
        if (!text) return { chars: 0, segments: 0, unicode: false };
        let units = 0, gsm = true;
        for (const ch of text) {
            if (GSM.includes(ch)) units++;
            else if (GSM_EXT.includes(ch)) units += 2;
            else { gsm = false; break; }
        }
        if (gsm) return { chars: units, segments: units <= 160 ? 1 : Math.ceil(units / 153), unicode: false };
        const len = text.length;
        return { chars: len, segments: len <= 70 ? 1 : Math.ceil(len / 67), unicode: true };
    };

    // ---- SignalR -----------------------------------------------------------
    const handlers = {};
    TT.on = function (evt, fn) { (handlers[evt] = handlers[evt] || []).push(fn); };
    const fire = (evt, ...args) => (handlers[evt] || []).forEach(fn => { try { fn(...args); } catch (e) { console.error(e); } });

    if (window.signalR) {
        const conn = new signalR.HubConnectionBuilder()
            .withUrl("/hubs/messaging")
            .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
            .build();
        TT.hub = conn;

        ["messageAdded", "messageUpdated", "threadRead", "viewers", "composing", "conversationsChanged"].forEach(evt =>
            conn.on(evt, (...args) => fire(evt, ...args)));

        const dot = document.getElementById("conn-dot");
        const setState = ok => { if (dot) dot.className = "dot " + (ok ? "ok" : "bad"); };

        conn.onreconnecting(() => setState(false));
        conn.onreconnected(() => { setState(true); fire("reconnected"); });
        conn.onclose(() => setState(false));

        TT.hubReady = (async function start() {
            try { await conn.start(); setState(true); fire("connected"); }
            catch (e) { setState(false); console.warn("SignalR start failed, retrying", e); setTimeout(start, 5000); }
        })();
    }

    // ---- unread badge in the nav, all pages ---------------------------------
    const badge = document.getElementById("nav-unread");
    async function refreshUnread() {
        if (!badge) return;
        try {
            const r = await TT.api("/api/unread-count");
            badge.textContent = r.count;
            badge.hidden = !r.count;
            document.title = document.title.replace(/^\(\d+\) /, "");
            if (r.count) document.title = `(${r.count}) ` + document.title;
        } catch { /* ignore */ }
    }
    TT.refreshUnread = refreshUnread;
    let unreadTimer = null;
    const scheduleUnread = () => { clearTimeout(unreadTimer); unreadTimer = setTimeout(refreshUnread, 300); };
    TT.on("messageAdded", m => { if (m.direction === "inbound") scheduleUnread(); });
    TT.on("threadRead", scheduleUnread);
    TT.on("reconnected", scheduleUnread);
    refreshUnread();
})();
