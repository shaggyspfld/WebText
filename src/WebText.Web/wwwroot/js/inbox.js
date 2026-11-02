(function () {
    "use strict";
    const { api, esc, shortUser, fmtTime, fmtFull, segments } = TT;
    const $ = id => document.getElementById(id);

    const STATUS_LABEL = {
        pending: "Queued", sending: "Sending…", queued: "Queued at Telnyx", sent: "Sent",
        delivered: "Delivered", delivery_unconfirmed: "Sent (delivery unconfirmed)",
        sending_failed: "Failed to send", delivery_failed: "Not delivered", failed: "Failed",
        blocked: "Blocked (opted out)"
    };
    const FAILED = new Set(["sending_failed", "delivery_failed", "failed", "blocked"]);

    let currentId = null;        // client id of the open thread
    let currentClient = null;
    let mode = "sms";            // "sms" | "note"
    let convos = [];
    let templates = [];
    const rendered = new Map();  // message id -> element

    // ---------------------------------------------------------------- conversation list
    async function loadConvos() {
        const q = $("convo-search").value.trim();
        const unread = $("convo-unread").checked;
        const params = new URLSearchParams();
        if (q) params.set("q", q);
        if (unread) params.set("unread", "true");
        try {
            convos = await api("/api/conversations?" + params);
            renderConvos();
        } catch (e) { console.error(e); }
    }

    function renderConvos() {
        const ul = $("convo-list");
        if (!convos.length) {
            ul.innerHTML = `<li class="empty muted">No conversations${$("convo-search").value ? " match" : " yet"}.</li>`;
            return;
        }
        ul.innerHTML = convos.map(c => `
            <li class="convo ${c.clientId === currentId ? "active" : ""} ${c.unread ? "unread" : ""}" data-id="${c.clientId}" tabindex="0">
                <div class="convo-top">
                    <span class="convo-name">${esc(c.name)}${c.isAutoCreated ? ' <span class="tag warn">new #</span>' : ""}${c.optedOut ? ' <span class="tag bad">opted out</span>' : ""}</span>
                    <span class="convo-time">${fmtTime(c.lastMessageAtUtc)}</span>
                </div>
                <div class="convo-bottom">
                    <span class="convo-preview">${c.lastDirection === "outbound" ? "You: " : ""}${esc(c.lastMessagePreview || "")}</span>
                    ${c.unread ? `<span class="pill">${c.unread}</span>` : ""}
                </div>
            </li>`).join("");
    }

    $("convo-list").addEventListener("click", e => {
        const li = e.target.closest("li.convo");
        if (li) openThread(+li.dataset.id);
    });
    $("convo-list").addEventListener("keydown", e => {
        const li = e.target.closest("li.convo");
        if (li && e.key === "Enter") openThread(+li.dataset.id);
    });

    let searchTimer;
    $("convo-search").addEventListener("input", () => { clearTimeout(searchTimer); searchTimer = setTimeout(loadConvos, 250); });
    $("convo-unread").addEventListener("change", loadConvos);

    // ---------------------------------------------------------------- thread
    async function openThread(clientId) {
        currentId = clientId;
        rendered.clear();
        history.replaceState(null, "", "/?client=" + clientId);
        $("thread-empty").hidden = true;
        $("thread").hidden = false;
        $("t-messages").innerHTML = `<p class="muted center">Loading…</p>`;
        $("t-viewers").innerHTML = "";
        $("t-composing").hidden = true;
        renderConvos();

        try {
            const [client, messages] = await Promise.all([
                api(`/api/clients/${clientId}`),
                api(`/api/clients/${clientId}/messages`)
            ]);
            if (currentId !== clientId) return;
            currentClient = client;
            renderHeader();
            $("t-messages").innerHTML = "";
            if (!messages.length) $("t-messages").innerHTML = `<p class="muted center" id="t-nomsg">No messages yet.</p>`;
            messages.forEach(m => appendMessage(m, false));
            scrollToBottom();
            markRead();
            if (TT.hub && TT.hub.state === "Connected") TT.hub.invoke("JoinThread", clientId).catch(() => { });
            $("t-body").focus();
        } catch (e) {
            $("t-messages").innerHTML = `<p class="error-text center">${esc(e.message)}</p>`;
        }
    }

    function renderHeader() {
        const c = currentClient;
        $("t-name").textContent = c.name;
        $("t-phone").textContent = c.phoneFormatted;
        $("t-account").textContent = c.accountNumber ? ` · Acct ${c.accountNumber}` : "";
        $("t-edit").href = `/Clients/Edit?id=${c.id}`;
        const warns = [];
        if (c.optedOut) warns.push(`<div class="banner bad">This client replied STOP and has opted out. Texts can't be sent until they reply START.</div>`);
        if (c.isAutoCreated) warns.push(`<div class="banner warn">This number isn't linked to a known client yet. <a href="/Clients/Edit?id=${c.id}">Add their name</a>.</div>`);
        if (c.otherClientsOnNumber > 0) warns.push(`<div class="banner info">${c.otherClientsOnNumber} other client record(s) share this phone number. Replies go to whoever was texted most recently.</div>`);
        $("t-warnings").innerHTML = warns.join("");
        setMode(mode);
    }

    function messageHtml(m) {
        if (m.direction === "note") {
            return `<div class="note-body">${esc(m.body)}</div>
                    <div class="meta">Note by <b>${esc(shortUser(m.sentBy))}</b> · <span title="${esc(fmtFull(m.createdAtUtc))}">${fmtTime(m.createdAtUtc)}</span></div>`;
        }
        const media = (m.media || []).map(x =>
            (x.contentType || "").startsWith("image/")
                ? `<a href="${esc(x.url)}" target="_blank" rel="noopener"><img class="mms" src="${esc(x.url)}" alt="attachment" /></a>`
                : `<a href="${esc(x.url)}" target="_blank" rel="noopener">📎 Attachment (${esc(x.contentType || "file")})</a>`
        ).join("");
        let meta;
        if (m.direction === "outbound") {
            const label = STATUS_LABEL[m.status] || m.status;
            const failed = FAILED.has(m.status);
            meta = `Sent by <b>${esc(shortUser(m.sentBy))}</b> · <span title="${esc(fmtFull(m.createdAtUtc))}">${fmtTime(m.createdAtUtc)}</span>
                    · <span class="status ${failed ? "bad" : m.status === "delivered" ? "ok" : ""}" title="${esc(m.error || "")}">${esc(label)}</span>
                    ${failed && m.status !== "blocked" ? `<button type="button" class="link resend" data-id="${m.id}">Resend</button>` : ""}
                    ${m.error ? `<div class="err-detail">${esc(m.error)}</div>` : ""}`;
        } else {
            meta = `<span title="${esc(fmtFull(m.createdAtUtc))}">${fmtTime(m.createdAtUtc)}</span>${m.readBy ? ` · read by ${esc(shortUser(m.readBy))}` : ""}`;
        }
        return `<div class="bubble">${esc(m.body)}${media ? `<div class="media">${media}</div>` : ""}</div><div class="meta">${meta}</div>`;
    }

    function appendMessage(m, scroll = true) {
        if (rendered.has(m.id)) return updateMessage(m);
        const none = $("t-nomsg"); if (none) none.remove();
        const div = document.createElement("div");
        div.className = `msg ${m.direction}`;
        div.dataset.id = m.id;
        div.innerHTML = messageHtml(m);
        const box = $("t-messages");
        const nearBottom = box.scrollHeight - box.scrollTop - box.clientHeight < 120;
        box.appendChild(div);
        rendered.set(m.id, div);
        if (scroll && nearBottom) scrollToBottom();
    }

    function updateMessage(m) {
        const el = rendered.get(m.id);
        if (el) el.innerHTML = messageHtml(m);
    }

    function scrollToBottom() { const b = $("t-messages"); b.scrollTop = b.scrollHeight; }

    function markRead() {
        if (!currentId || document.hidden) return;
        api(`/api/clients/${currentId}/read`, { method: "POST" }).catch(() => { });
    }
    document.addEventListener("visibilitychange", () => { if (!document.hidden) markRead(); });

    $("t-messages").addEventListener("click", async e => {
        const btn = e.target.closest("button.resend");
        if (!btn) return;
        if (!confirm("Send this text again?")) return;
        btn.disabled = true;
        try { const m = await api(`/api/messages/${btn.dataset.id}/resend`, { method: "POST" }); appendMessage(m); }
        catch (err) { alert(err.message); btn.disabled = false; }
    });

    // ---------------------------------------------------------------- composer
    function setMode(m) {
        mode = m;
        document.querySelectorAll(".composer-tabs .tab").forEach(t => t.classList.toggle("active", t.dataset.mode === m));
        const body = $("t-body"), send = $("t-send");
        const blocked = m === "sms" && currentClient && currentClient.optedOut;
        body.placeholder = m === "sms" ? (blocked ? "Client has opted out of texts" : "Type a text message…")
                                       : "Internal note (not sent to the client), e.g. called client, left voicemail…";
        send.textContent = m === "sms" ? "Send text" : "Save note";
        send.disabled = !!blocked;
        body.disabled = !!blocked;
        $("t-template").hidden = m !== "sms";
        document.querySelector(".composer").classList.toggle("note-mode", m === "note");
        updateCount();
    }
    document.querySelectorAll(".composer-tabs .tab").forEach(t => t.addEventListener("click", () => setMode(t.dataset.mode)));

    function updateCount() {
        const text = $("t-body").value;
        if (mode !== "sms") { $("t-count").textContent = ""; return; }
        const s = segments(applyMerge(text));
        $("t-count").textContent = text ? `${s.chars} chars · ${s.segments} SMS segment${s.segments === 1 ? "" : "s"}${s.unicode ? " (emoji/special characters use shorter segments)" : ""}` : "";
    }

    function applyMerge(text) {
        if (!currentClient) return text;
        return text.replace(/\{FirstName\}/gi, currentClient.firstName || "")
                   .replace(/\{LastName\}/gi, currentClient.lastName || "")
                   .replace(/\{FullName\}/gi, currentClient.name || "");
    }

    let lastComposing = 0;
    $("t-body").addEventListener("input", () => {
        updateCount();
        $("t-error").textContent = "";
        const now = Date.now();
        if (currentId && TT.hub && TT.hub.state === "Connected" && now - lastComposing > 3000) {
            lastComposing = now;
            TT.hub.invoke("Composing", currentId).catch(() => { });
        }
    });
    $("t-body").addEventListener("keydown", e => {
        if (e.key === "Enter" && (e.ctrlKey || e.metaKey)) { e.preventDefault(); send(); }
    });
    $("t-send").addEventListener("click", send);

    async function send() {
        const raw = $("t-body").value.trim();
        if (!raw || !currentId) return;
        const body = mode === "sms" ? applyMerge(raw) : raw;
        if (mode === "sms" && /\{[A-Za-z]+\}/.test(body) && !confirm("The message still contains a {placeholder}. Send anyway?")) return;
        const btn = $("t-send");
        btn.disabled = true;
        $("t-error").textContent = "";
        try {
            const url = `/api/clients/${currentId}/${mode === "sms" ? "messages" : "notes"}`;
            const m = await api(url, { json: { body } });
            appendMessage(m);
            scrollToBottom();
            $("t-body").value = "";
            updateCount();
        } catch (e) {
            $("t-error").textContent = e.message;
        } finally {
            btn.disabled = false;
            $("t-body").focus();
        }
    }

    async function loadTemplates() {
        try {
            templates = await api("/api/templates");
            $("t-template").innerHTML = `<option value="">Insert template…</option>` +
                templates.map(t => `<option value="${t.id}">${esc(t.name)}</option>`).join("");
        } catch { /* ignore */ }
    }
    $("t-template").addEventListener("change", e => {
        const t = templates.find(x => x.id === +e.target.value);
        if (t) { $("t-body").value = applyMerge(t.body); updateCount(); $("t-body").focus(); }
        e.target.value = "";
    });

    // ---------------------------------------------------------------- new conversation dialog
    $("new-convo-btn").addEventListener("click", () => {
        $("nc-search").value = ""; $("nc-results").innerHTML = "";
        $("new-convo").showModal(); $("nc-search").focus();
    });
    let ncTimer;
    $("nc-search").addEventListener("input", () => {
        clearTimeout(ncTimer);
        ncTimer = setTimeout(async () => {
            const q = $("nc-search").value.trim();
            if (q.length < 2) { $("nc-results").innerHTML = ""; return; }
            const rows = await api("/api/clients/search?q=" + encodeURIComponent(q));
            $("nc-results").innerHTML = rows.length ? rows.map(r =>
                `<li data-id="${r.id}"><b>${esc(r.name)}</b> <span class="muted">${esc(r.phone)}${r.account ? " · " + esc(r.account) : ""}</span>${r.optedOut ? ' <span class="tag bad">opted out</span>' : ""}</li>`
            ).join("") : `<li class="muted">No matches.</li>`;
        }, 200);
    });
    $("nc-results").addEventListener("click", e => {
        const li = e.target.closest("li[data-id]");
        if (!li) return;
        $("new-convo").close();
        openThread(+li.dataset.id);
    });

    // ---------------------------------------------------------------- live updates
    TT.on("messageAdded", m => {
        if (m.clientId === currentId) {
            appendMessage(m);
            if (m.direction === "inbound") markRead();
        }
        if (m.direction === "inbound") notify(m);
        refreshConvosSoon();
    });
    TT.on("messageUpdated", m => { if (m.clientId === currentId) updateMessage(m); });
    TT.on("threadRead", () => refreshConvosSoon());
    TT.on("conversationsChanged", () => { refreshConvosSoon(); if (currentId) reloadThreadSoon(); });
    TT.on("viewers", (clientId, users) => {
        if (clientId !== currentId) return;
        const me = document.querySelector(".who").textContent.trim().toLowerCase();
        const others = users.filter(u => u.toLowerCase() !== me);
        $("t-viewers").innerHTML = others.length
            ? `<span class="muted small">Also viewing:</span> ` + others.map(u => `<span class="avatar" title="${esc(u)}">${esc(shortUser(u))}</span>`).join("")
            : "";
    });
    let composingTimer;
    TT.on("composing", (clientId, user) => {
        if (clientId !== currentId) return;
        const el = $("t-composing");
        el.textContent = `${shortUser(user)} is typing a reply…`;
        el.hidden = false;
        clearTimeout(composingTimer);
        composingTimer = setTimeout(() => el.hidden = true, 5000);
    });
    const rejoin = () => { if (currentId) TT.hub.invoke("JoinThread", currentId).catch(() => { }); };
    TT.on("connected", rejoin);
    TT.on("reconnected", () => { rejoin(); loadConvos(); if (currentId) openThread(currentId); });

    let threadTimer;
    function reloadThreadSoon() {
        clearTimeout(threadTimer);
        threadTimer = setTimeout(async () => {
            if (!currentId) return;
            const id = currentId;
            const msgs = await api(`/api/clients/${id}/messages`).catch(() => []);
            if (id === currentId) msgs.forEach(m => appendMessage(m));
        }, 500);
    }

    let convoTimer;
    function refreshConvosSoon() { clearTimeout(convoTimer); convoTimer = setTimeout(loadConvos, 400); }

    function notify(m) {
        if (!document.hidden || !("Notification" in window) || Notification.permission !== "granted") return;
        const c = convos.find(x => x.clientId === m.clientId);
        const n = new Notification("New text from " + (c ? c.name : "a client"), { body: m.body.slice(0, 120), tag: "msg-" + m.id });
        n.onclick = () => { window.focus(); openThread(m.clientId); n.close(); };
    }
    if ("Notification" in window && Notification.permission === "default") {
        document.addEventListener("click", () => Notification.requestPermission(), { once: true });
    }

    // ---------------------------------------------------------------- start
    loadTemplates();
    loadConvos();
    const startId = +new URLSearchParams(location.search).get("client");
    if (startId) openThread(startId);
})();
