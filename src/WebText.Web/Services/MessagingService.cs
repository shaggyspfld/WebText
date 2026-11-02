using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using WebText.Web.Data;
using WebText.Web.Hubs;

namespace WebText.Web.Services;

/// <summary>Wakes the outbound worker as soon as something is queued.</summary>
public class OutboundSignal
{
    private readonly SemaphoreSlim _sem = new(0);
    public void Notify() { if (_sem.CurrentCount == 0) _sem.Release(); }
    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken ct) => _sem.WaitAsync(timeout, ct);
}

public record InboundMedia(string Url, string? ContentType, long? SizeBytes);

/// <summary>An incoming text, whichever provider it came from.</summary>
public record InboundText(string? From, string? To, string? Text, string? ProviderMessageId, DateTime ReceivedUtc, IReadOnlyList<InboundMedia> Media);

public class MessagingService
{
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
        { "STOP", "STOPALL", "UNSUBSCRIBE", "CANCEL", "END", "QUIT", "OPTOUT", "OPT OUT", "REVOKE" };
    private static readonly HashSet<string> StartWords = new(StringComparer.OrdinalIgnoreCase)
        { "START", "UNSTOP", "YES", "OPTIN", "OPT IN" };

    private readonly AppDbContext _db;
    private readonly IHubContext<MessagingHub> _hub;
    private readonly OutboundSignal _signal;
    private readonly SettingsService _settings;
    private readonly ILogger<MessagingService> _log;

    public MessagingService(AppDbContext db, IHubContext<MessagingHub> hub, OutboundSignal signal,
        SettingsService settings, ILogger<MessagingService> log)
    {
        _db = db; _hub = hub; _signal = signal; _settings = settings; _log = log;
    }

    // ------------------------------------------------------------------ outbound

    public async Task<(Message? message, string? error)> QueueOutboundAsync(
        int clientId, string body, string user, Guid? batchId = null, bool broadcast = true, CancellationToken ct = default)
    {
        body = (body ?? "").Trim();
        if (body.Length == 0) return (null, "Message is empty.");
        if (body.Length > 1600) return (null, "Message is longer than 1600 characters.");
        if (!_settings.Current.TextingReady)
            return (null, $"Texting isn't set up yet: an admin needs to enter the {_settings.Current.ProviderName} account details and sending number on the Settings page.");

        var client = await _db.Clients.FirstOrDefaultAsync(c => c.Id == clientId, ct);
        if (client == null) return (null, "Client not found.");
        if (client.OptedOut) return (null, $"{client.DisplayName} has opted out of text messages (replied STOP).");

        var now = DateTime.UtcNow;
        var msg = new Message
        {
            ClientId = client.Id,
            Direction = MessageDirection.Outbound,
            Body = body,
            FromNumber = _settings.Current.FromNumber,
            ToNumber = client.PhoneE164,
            Status = MessageStatus.Pending,
            SentBy = user,
            BatchId = batchId,
            Segments = SmsSegments.Count(body),
            CreatedAtUtc = now,
            StatusUpdatedAtUtc = now,
            IsRead = true
        };
        _db.Messages.Add(msg);
        client.LastMessageAtUtc = now;
        await _db.SaveChangesAsync(ct);

        _signal.Notify();
        if (broadcast) await BroadcastNewAsync(msg);
        return (msg, null);
    }

    public async Task<(Message? message, string? error)> AddNoteAsync(int clientId, string body, string user, CancellationToken ct = default)
    {
        body = (body ?? "").Trim();
        if (body.Length == 0) return (null, "Note is empty.");
        var client = await _db.Clients.FirstOrDefaultAsync(c => c.Id == clientId, ct);
        if (client == null) return (null, "Client not found.");

        var msg = new Message
        {
            ClientId = clientId,
            Direction = MessageDirection.Note,
            Body = body.Length > 4000 ? body[..4000] : body,
            Status = MessageStatus.Note,
            SentBy = user,
            IsRead = true,
            Segments = 0
        };
        _db.Messages.Add(msg);
        await _db.SaveChangesAsync(ct);
        await BroadcastNewAsync(msg);
        return (msg, null);
    }

    public async Task MarkThreadReadAsync(int clientId, string user, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var n = await _db.Messages
            .Where(m => m.ClientId == clientId && m.Direction == MessageDirection.Inbound && !m.IsRead)
            .ExecuteUpdateAsync(s => s
                .SetProperty(m => m.IsRead, true)
                .SetProperty(m => m.ReadBy, user)
                .SetProperty(m => m.ReadAtUtc, now), ct);
        if (n > 0) await _hub.Clients.All.SendAsync("threadRead", clientId, user, ct);
    }

    // ------------------------------------------------------------------ webhooks

    /// <summary>Stores and processes one Telnyx webhook. Safe to call more than once for the same event.</summary>
    public async Task HandleWebhookAsync(string rawJson, CancellationToken ct = default)
    {
        using var doc = JsonDocument.Parse(rawJson);
        var data = doc.RootElement.GetProperty("data");
        var eventId = data.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
        var eventType = data.TryGetProperty("event_type", out var et) ? et.GetString() ?? "" : "";
        DateTime? occurred = data.TryGetProperty("occurred_at", out var oa) && oa.TryGetDateTimeOffset(out var dto)
            ? dto.UtcDateTime : null;

        if (string.IsNullOrEmpty(eventId)) eventId = "noid-" + Guid.NewGuid().ToString("N");

        // De-duplicate: Telnyx retries and may deliver the same event more than once.
        if (await _db.WebhookEvents.AnyAsync(e => e.EventId == eventId, ct))
        {
            _log.LogInformation("Duplicate webhook {EventId} ignored", eventId);
            return;
        }

        var evt = new WebhookEvent
        {
            EventId = eventId,
            EventType = eventType,
            OccurredAtUtc = occurred,
            RawJson = rawJson
        };
        _db.WebhookEvents.Add(evt);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Lost a race with a concurrent retry of the same event.
            _db.ChangeTracker.Clear();
            return;
        }

        try
        {
            var payload = data.GetProperty("payload");
            switch (eventType)
            {
                case "message.received":
                    await HandleInboundAsync(payload, occurred ?? DateTime.UtcNow, ct);
                    break;
                case "message.sent":
                case "message.finalized":
                    await HandleStatusAsync(eventType, payload, occurred ?? DateTime.UtcNow, ct);
                    break;
                default:
                    _log.LogDebug("Ignoring webhook type {Type}", eventType);
                    break;
            }
            evt.Processed = true;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Error processing webhook {EventId} ({Type})", eventId, eventType);
            evt.ProcessingError = ex.Message.Length > 1000 ? ex.Message[..1000] : ex.Message;
        }
        await _db.SaveChangesAsync(ct);
    }

    private async Task HandleInboundAsync(JsonElement p, DateTime occurredUtc, CancellationToken ct)
    {
        var from = p.GetProperty("from").GetProperty("phone_number").GetString();
        string? to = null;
        if (p.TryGetProperty("to", out var toArr) && toArr.ValueKind == JsonValueKind.Array && toArr.GetArrayLength() > 0)
            to = toArr[0].GetProperty("phone_number").GetString();
        var text = p.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
        var telnyxId = p.TryGetProperty("id", out var tid) ? tid.GetString() : null;
        var media = new List<InboundMedia>();
        if (p.TryGetProperty("media", out var mediaArr) && mediaArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var m in mediaArr.EnumerateArray())
            {
                var url = m.TryGetProperty("url", out var u) ? u.GetString() : null;
                if (string.IsNullOrEmpty(url)) continue;
                media.Add(new InboundMedia(url,
                    m.TryGetProperty("content_type", out var c) ? c.GetString() : null,
                    m.TryGetProperty("size", out var sz) && sz.TryGetInt64(out var n) ? n : null));
            }
        }
        await ReceiveAsync(new InboundText(from, to, text, telnyxId, occurredUtc, media), ct);
    }

    /// <summary>
    /// A VoIP.ms URL callback (one inbound text). Recorded as a WebhookEvent first, so a repeated callback (VoIP.ms retries
    /// every 30 minutes when it doesn't get "ok") is filed only once.
    /// </summary>
    public async Task HandleVoipMsCallbackAsync(InboundText inbound, string rawForLog, CancellationToken ct = default)
    {
        var eventId = "voipms-" + (string.IsNullOrWhiteSpace(inbound.ProviderMessageId)
            ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes($"{inbound.From}|{inbound.To}|{inbound.Text}|{rawForLog}")))[..32]
            : inbound.ProviderMessageId);
        if (await _db.WebhookEvents.AnyAsync(e => e.EventId == eventId, ct)) return;

        var evt = new WebhookEvent { EventId = eventId, EventType = "voipms.message.received", OccurredAtUtc = inbound.ReceivedUtc, RawJson = rawForLog };
        _db.WebhookEvents.Add(evt);
        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { _db.ChangeTracker.Clear(); return; }

        try
        {
            await ReceiveAsync(inbound, ct);
            evt.Processed = true;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Error processing VoIP.ms callback {EventId}", eventId);
            evt.ProcessingError = ex.Message.Length > 1000 ? ex.Message[..1000] : ex.Message;
        }
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>Files an incoming text (from any provider) in the right client's thread.</summary>
    private async Task ReceiveAsync(InboundText inbound, CancellationToken ct)
    {
        var from = PhoneNumber.ToE164(inbound.From) ?? "";
        var to = inbound.To;
        var text = inbound.Text ?? "";
        var telnyxId = inbound.ProviderMessageId;
        var occurredUtc = inbound.ReceivedUtc;

        if (string.IsNullOrEmpty(from)) throw new InvalidOperationException("Inbound message has no sender number.");

        // If several clients share the number (e.g. spouses), file the reply under whoever we texted most recently.
        var candidates = await _db.Clients.Where(c => c.PhoneE164 == from).Select(c => c.Id).ToListAsync(ct);
        int clientId;
        if (candidates.Count == 1)
        {
            clientId = candidates[0];
        }
        else if (candidates.Count > 1)
        {
            clientId = await _db.Messages
                .Where(m => candidates.Contains(m.ClientId) && m.Direction == MessageDirection.Outbound)
                .OrderByDescending(m => m.CreatedAtUtc)
                .Select(m => (int?)m.ClientId)
                .FirstOrDefaultAsync(ct) ?? candidates.Min();
        }
        else
        {
            var newClient = new Client
            {
                FirstName = "Unknown",
                LastName = PhoneNumber.Format(from),
                PhoneE164 = from,
                IsAutoCreated = true,
                CreatedBy = "system (inbound text)",
                Notes = "Created automatically from an inbound text. Update the name or merge with the right client."
            };
            _db.Clients.Add(newClient);
            await _db.SaveChangesAsync(ct);
            clientId = newClient.Id;
        }

        var msg = new Message
        {
            ClientId = clientId,
            Direction = MessageDirection.Inbound,
            Body = text.Length > 4000 ? text[..4000] : text,
            FromNumber = from,
            ToNumber = to,
            TelnyxMessageId = telnyxId,
            Status = MessageStatus.Received,
            CreatedAtUtc = occurredUtc,
            StatusUpdatedAtUtc = occurredUtc,
            Segments = SmsSegments.Count(text),
            IsRead = false
        };

        foreach (var m in inbound.Media.Where(m => !string.IsNullOrWhiteSpace(m.Url)).Take(10))
            msg.Media.Add(new MessageMedia { Url = m.Url.Length > 2000 ? m.Url[..2000] : m.Url, ContentType = m.ContentType, SizeBytes = m.SizeBytes });
        _db.Messages.Add(msg);

        // Opt-out / opt-in keywords. Telnyx also enforces STOP at the carrier level for long codes; VoIP.ms doesn't,
        // so the app's own check is what stops further texts there.
        var keyword = text.Trim().TrimEnd('.', '!');
        var clientIds = candidates.Count > 0 ? candidates : new List<int> { clientId };
        var clients = await _db.Clients.Where(c => clientIds.Contains(c.Id)).ToListAsync(ct);
        foreach (var c in clients)
        {
            if (StopWords.Contains(keyword)) { c.OptedOut = true; c.OptedOutAtUtc = DateTime.UtcNow; }
            else if (StartWords.Contains(keyword)) { c.OptedOut = false; c.OptedOutAtUtc = null; }
            if (c.Id == clientId) c.LastMessageAtUtc = occurredUtc > (c.LastMessageAtUtc ?? DateTime.MinValue) ? occurredUtc : c.LastMessageAtUtc;
        }

        await _db.SaveChangesAsync(ct);
        await BroadcastNewAsync(msg);

        if (!StopWords.Contains(keyword) && !StartWords.Contains(keyword))
        {
            try { await TryAutoReplyAsync(clientId, msg, text, ct); }
            catch (Exception ex) { _log.LogError(ex, "Auto-reply failed for client {ClientId}", clientId); }   // never lose the inbound text
        }
    }

    /// <summary>
    /// Sends the matching auto-reply, if any. Safeguards: off switch on the Settings page, never to opted-out clients,
    /// a per-keyword cooldown per client, and a cap on auto-replies per client per hour (stops reply loops with
    /// other automated senders). The reply goes through the normal send queue, so it appears in the thread and log.
    /// </summary>
    private async Task TryAutoReplyAsync(int clientId, Message inbound, string text, CancellationToken ct)
    {
        var settings = _settings.Current;
        if (!settings.AutoRepliesEnabled) return;

        var client = await _db.Clients.FirstOrDefaultAsync(c => c.Id == clientId, ct);
        if (client == null || client.OptedOut) return;

        var responders = await _db.AutoResponders.AsNoTracking().Where(r => r.IsActive).ToListAsync(ct);
        var match = AutoReplies.FindMatch(responders, text);
        if (match == null) return;

        var sender = AutoReplies.SenderName(match.Keyword);
        var now = DateTime.UtcNow;
        if (match.CooldownMinutes > 0)
        {
            var since = now.AddMinutes(-match.CooldownMinutes);
            if (await _db.Messages.AnyAsync(m => m.ClientId == clientId && m.Direction == MessageDirection.Outbound
                                                 && m.SentBy == sender && m.CreatedAtUtc >= since, ct))
            {
                _log.LogInformation("Auto-reply {Keyword} skipped for client {ClientId}: sent within the last {Minutes} min", match.Keyword, clientId, match.CooldownMinutes);
                return;
            }
        }
        var hourAgo = now.AddHours(-1);
        var lastHour = await _db.Messages.CountAsync(m => m.ClientId == clientId && m.Direction == MessageDirection.Outbound
                                                          && m.SentBy != null && m.SentBy.StartsWith(AutoReplies.SenderPrefix)
                                                          && m.CreatedAtUtc >= hourAgo, ct);
        if (lastHour >= settings.AutoReplyMaxPerClientPerHour)
        {
            _log.LogWarning("Auto-reply {Keyword} skipped for client {ClientId}: hourly limit of {Max} reached", match.Keyword, clientId, settings.AutoReplyMaxPerClientPerHour);
            return;
        }

        var body = AutoReplies.Merge(match.Reply, client);
        if (body.Length > 1600) body = body[..1600];
        var (queued, error) = await QueueOutboundAsync(clientId, body, sender, ct: ct);
        if (queued == null)
        {
            _log.LogWarning("Auto-reply {Keyword} not sent to client {ClientId}: {Error}", match.Keyword, clientId, error);
            return;
        }

        await _db.AutoResponders.Where(r => r.Id == match.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.TimesUsed, r => r.TimesUsed + 1)
                                      .SetProperty(r => r.LastUsedAtUtc, now), ct);

        if (match.MarkHandled && !inbound.IsRead)
        {
            inbound.IsRead = true;
            inbound.ReadBy = sender;
            inbound.ReadAtUtc = now;
            await _db.SaveChangesAsync(ct);
            await _hub.Clients.All.SendAsync("threadRead", clientId, sender, ct);
        }
    }

    private async Task HandleStatusAsync(string eventType, JsonElement p, DateTime occurredUtc, CancellationToken ct)
    {
        var telnyxId = p.TryGetProperty("id", out var tid) ? tid.GetString() : null;
        if (string.IsNullOrEmpty(telnyxId)) return;

        var msg = await _db.Messages.FirstOrDefaultAsync(m => m.TelnyxMessageId == telnyxId, ct);
        if (msg == null)
        {
            // Sent outside this app (or before the send response was saved). Nothing to update.
            _log.LogInformation("Status webhook for unknown message {Id}", telnyxId);
            return;
        }

        string? newStatus = null;
        if (p.TryGetProperty("to", out var toArr) && toArr.ValueKind == JsonValueKind.Array && toArr.GetArrayLength() > 0
            && toArr[0].TryGetProperty("status", out var st))
            newStatus = st.GetString();
        newStatus ??= eventType == "message.sent" ? MessageStatus.Sent : null;
        if (newStatus == null) return;

        // Webhooks can arrive out of order; never move a message backwards.
        if (MessageStatus.Rank(newStatus) < MessageStatus.Rank(msg.Status)) return;

        msg.Status = newStatus;
        msg.StatusUpdatedAtUtc = occurredUtc;
        if (newStatus == MessageStatus.Sent && msg.SentAtUtc == null) msg.SentAtUtc = occurredUtc;

        if (p.TryGetProperty("errors", out var errs) && errs.ValueKind == JsonValueKind.Array && errs.GetArrayLength() > 0)
        {
            var parts = errs.EnumerateArray().Select(e =>
            {
                var code = e.TryGetProperty("code", out var c) ? c.ToString() : "";
                var title = e.TryGetProperty("title", out var tt) ? tt.GetString() : "";
                var detail = e.TryGetProperty("detail", out var d) ? d.GetString() : "";
                return $"{code} {title} {detail}".Trim();
            });
            var err = string.Join("; ", parts);
            msg.ErrorDetail = err.Length > 1000 ? err[..1000] : err;
        }

        await _db.SaveChangesAsync(ct);
        await BroadcastUpdatedAsync(msg);
    }

    // ------------------------------------------------------------------ broadcast

    public async Task BroadcastNewAsync(Message msg)
    {
        var dto = MessageDto.From(msg);
        await _hub.Clients.All.SendAsync("messageAdded", dto);
    }

    public async Task BroadcastUpdatedAsync(Message msg)
    {
        var dto = MessageDto.From(msg);
        await _hub.Clients.All.SendAsync("messageUpdated", dto);
    }
}
