using Microsoft.EntityFrameworkCore;
using WebText.Web.Data;

namespace WebText.Web.Services;

public class UsageReport
{
    public DateTime FromLocal { get; init; }
    public DateTime ToLocal { get; init; }   // inclusive date

    // ---- headline numbers
    public int Sent { get; set; }
    public int Delivered { get; set; }
    public int Failed { get; set; }
    public int Blocked { get; set; }
    public int InFlight { get; set; }
    public int Received { get; set; }
    public int Notes { get; set; }
    public int SegmentsSent { get; set; }
    public int SegmentsReceived { get; set; }
    public int ClientsTexted { get; set; }
    public int ClientsReplied { get; set; }
    public int ClientsTextedWhoReplied { get; set; }
    public int ActiveStaff { get; set; }
    public int BulkSends { get; set; }
    public int BulkTexts { get; set; }
    public int OneToOneTexts { get; set; }
    public int AutoReplies { get; set; }
    public int OptOuts { get; set; }
    public int UnknownNumbers { get; set; }
    public int InboundAnswered { get; set; }
    public int InboundUnanswered { get; set; }
    public TimeSpan? MedianResponse { get; set; }
    public int AnsweredWithinHour { get; set; }
    public decimal? EstimatedCost { get; set; }

    public double? DeliveryRate => Delivered + Failed == 0 ? null : (double)Delivered / (Delivered + Failed);
    public double? ReplyRate => ClientsTexted == 0 ? null : (double)ClientsTextedWhoReplied / ClientsTexted;

    public List<StaffRow> Staff { get; set; } = new();
    public List<ClientRow> Clients { get; set; } = new();
    public List<PeriodRow> Periods { get; set; } = new();
    public string PeriodUnit { get; set; } = "day";
}

public record StaffRow(string User, int Sent, int OneToOne, int Bulk, int Delivered, int Failed, int Segments,
    int ClientsTexted, int Notes, int RepliesHandled, TimeSpan? MedianResponse, DateTime? LastActivityUtc);

public record ClientRow(int ClientId, string Name, string Phone, string? Account, bool OptedOut,
    int Sent, int Received, int Notes, int Failed, string Staff, DateTime? LastActivityUtc)
{
    public int Total => Sent + Received;
}

public record PeriodRow(DateTime Start, string Label, int Sent, int Received, int Delivered, int Failed);

/// <summary>Builds usage statistics for a date range. Everything is computed from the Messages table.</summary>
public class ReportService
{
    private readonly AppDbContext _db;
    private readonly TimeDisplay _time;
    private readonly SettingsService _settings;

    public ReportService(AppDbContext db, TimeDisplay time, SettingsService settings)
    {
        _db = db; _time = time; _settings = settings;
    }

    private sealed record M(long Id, int ClientId, MessageDirection Direction, string Status, string? SentBy,
        DateTime CreatedAtUtc, int Segments, Guid? BatchId);

    public async Task<UsageReport> BuildAsync(DateTime fromLocal, DateTime toLocalInclusive, string? staffFilter = null, CancellationToken ct = default)
    {
        var fromUtc = _time.ToUtc(fromLocal.Date);
        var toUtc = _time.ToUtc(toLocalInclusive.Date.AddDays(1));
        // Look a week past the end of the range so a late-in-range inbound text can still find its reply.
        var lookaheadUtc = toUtc.AddDays(7);

        var all = await _db.Messages.AsNoTracking()
            .Where(m => m.CreatedAtUtc >= fromUtc && m.CreatedAtUtc < lookaheadUtc)
            .Select(m => new M(m.Id, m.ClientId, m.Direction, m.Status, m.SentBy, m.CreatedAtUtc, m.Segments, m.BatchId))
            .ToListAsync(ct);

        var inRange = all.Where(m => m.CreatedAtUtc < toUtc).ToList();
        var outbound = inRange.Where(m => m.Direction == MessageDirection.Outbound).ToList();
        var inbound = inRange.Where(m => m.Direction == MessageDirection.Inbound).ToList();
        var notes = inRange.Where(m => m.Direction == MessageDirection.Note).ToList();

        var r = new UsageReport { FromLocal = fromLocal.Date, ToLocal = toLocalInclusive.Date };

        // ---- response times: for each inbound text, the first staff text or note to that client afterwards.
        var staffActionsByClient = all.Where(m => m.Direction != MessageDirection.Inbound)
            .GroupBy(m => m.ClientId)
            .ToDictionary(g => g.Key, g => g.OrderBy(x => x.CreatedAtUtc).ToList());

        var responses = new List<(M Inbound, M? Reply)>();
        foreach (var grp in inbound.GroupBy(m => m.ClientId))
        {
            staffActionsByClient.TryGetValue(grp.Key, out var actions);
            var ordered = grp.OrderBy(m => m.CreatedAtUtc).ToList();
            for (var i = 0; i < ordered.Count; i++)
            {
                var msg = ordered[i];
                // Several texts in a row from a client count as one conversation turn: only the last one before
                // a staff reply is measured, so a client sending three quick texts isn't counted as two unanswered.
                var reply = actions?.FirstOrDefault(a => a.CreatedAtUtc > msg.CreatedAtUtc);
                if (i + 1 < ordered.Count && (reply == null || ordered[i + 1].CreatedAtUtc < reply.CreatedAtUtc)) continue;
                // Answered by a keyword auto-reply: the client got what they asked for, and no person was involved,
                // so it counts neither as a staff response nor as "awaiting a reply".
                if (reply != null && AutoReplies.IsAutoReplySender(reply.SentBy)) continue;
                responses.Add((msg, reply));
            }
        }

        // ---- headline numbers
        r.Sent = outbound.Count;
        r.Delivered = outbound.Count(m => m.Status == MessageStatus.Delivered);
        r.Blocked = outbound.Count(m => m.Status == MessageStatus.Blocked);
        r.Failed = outbound.Count(m => MessageStatus.IsFailure(m.Status) && m.Status != MessageStatus.Blocked);
        r.InFlight = outbound.Count(m => m.Status is MessageStatus.Pending or MessageStatus.Sending or MessageStatus.Queued or MessageStatus.Sent);
        r.Received = inbound.Count;
        r.Notes = notes.Count;
        r.SegmentsSent = outbound.Where(m => m.Status != MessageStatus.Blocked).Sum(m => m.Segments);
        r.SegmentsReceived = inbound.Sum(m => m.Segments);
        var textedIds = outbound.Select(m => m.ClientId).ToHashSet();
        var repliedIds = inbound.Select(m => m.ClientId).ToHashSet();
        r.ClientsTexted = textedIds.Count;
        r.ClientsReplied = repliedIds.Count;
        // "Reply rate": of the clients we texted in the range, how many texted back afterwards.
        var firstOutByClient = outbound.GroupBy(m => m.ClientId).ToDictionary(g => g.Key, g => g.Min(m => m.CreatedAtUtc));
        var lastInByClient = inbound.GroupBy(m => m.ClientId).ToDictionary(g => g.Key, g => g.Max(m => m.CreatedAtUtc));
        r.ClientsTextedWhoReplied = firstOutByClient.Count(kv =>
            lastInByClient.TryGetValue(kv.Key, out var lastIn) && lastIn > kv.Value);
        r.ActiveStaff = outbound.Concat(notes).Select(m => m.SentBy).Where(u => u != null && !AutoReplies.IsAutoReplySender(u))
                                .Distinct(StringComparer.OrdinalIgnoreCase).Count();
        r.BulkTexts = outbound.Count(m => m.BatchId != null);
        r.BulkSends = outbound.Where(m => m.BatchId != null).Select(m => m.BatchId).Distinct().Count();
        r.AutoReplies = outbound.Count(m => m.BatchId == null && AutoReplies.IsAutoReplySender(m.SentBy));
        r.OneToOneTexts = r.Sent - r.BulkTexts - r.AutoReplies;

        var answered = responses.Where(x => x.Reply != null).ToList();
        r.InboundAnswered = answered.Count;
        r.InboundUnanswered = responses.Count - answered.Count;
        r.MedianResponse = Median(answered.Select(x => x.Reply!.CreatedAtUtc - x.Inbound.CreatedAtUtc));
        r.AnsweredWithinHour = answered.Count(x => x.Reply!.CreatedAtUtc - x.Inbound.CreatedAtUtc <= TimeSpan.FromHours(1));

        r.OptOuts = await _db.Clients.CountAsync(c => c.OptedOutAtUtc >= fromUtc && c.OptedOutAtUtc < toUtc, ct);
        r.UnknownNumbers = await _db.Clients.CountAsync(c => c.CreatedAtUtc >= fromUtc && c.CreatedAtUtc < toUtc
                                                             && c.CreatedBy == "system (inbound text)", ct);

        var cost = _settings.Current.CostPerSegment;
        if (cost > 0) r.EstimatedCost = (r.SegmentsSent + r.SegmentsReceived) * cost;

        // ---- by staff member
        var users = outbound.Concat(notes).Where(m => m.SentBy != null && !AutoReplies.IsAutoReplySender(m.SentBy))
            .GroupBy(m => m.SentBy!, StringComparer.OrdinalIgnoreCase);
        r.Staff = users.Select(g =>
        {
            var outs = g.Where(m => m.Direction == MessageDirection.Outbound).ToList();
            var handled = answered.Where(x => string.Equals(x.Reply!.SentBy, g.Key, StringComparison.OrdinalIgnoreCase)).ToList();
            return new StaffRow(
                g.Key,
                outs.Count,
                outs.Count(m => m.BatchId == null),
                outs.Count(m => m.BatchId != null),
                outs.Count(m => m.Status == MessageStatus.Delivered),
                outs.Count(m => MessageStatus.IsFailure(m.Status)),
                outs.Sum(m => m.Segments),
                outs.Select(m => m.ClientId).Distinct().Count(),
                g.Count(m => m.Direction == MessageDirection.Note),
                handled.Count,
                Median(handled.Select(x => x.Reply!.CreatedAtUtc - x.Inbound.CreatedAtUtc)),
                g.Max(m => (DateTime?)m.CreatedAtUtc));
        })
        .OrderByDescending(s => s.Sent + s.Notes).ToList();

        // ---- by client / phone number
        var clientIds = inRange.Select(m => m.ClientId).Distinct().ToList();
        var clientInfo = new Dictionary<int, (string Name, string Phone, string? Account, bool OptedOut)>();
        // EF Core 10 sends one SQL parameter per list item; SQL Server allows 2100 per query.
        foreach (var chunk in clientIds.Chunk(500))
        {
            var list = chunk.ToList();
            var rows = await _db.Clients.AsNoTracking().Where(c => list.Contains(c.Id))
                .Select(c => new { c.Id, c.FirstName, c.LastName, c.PhoneE164, c.AccountNumber, c.OptedOut })
                .ToListAsync(ct);
            foreach (var c in rows)
                clientInfo[c.Id] = ($"{c.LastName}, {c.FirstName}".Trim(' ', ','), PhoneNumber.Format(c.PhoneE164), c.AccountNumber, c.OptedOut);
        }
        r.Clients = inRange.GroupBy(m => m.ClientId).Select(g =>
        {
            clientInfo.TryGetValue(g.Key, out var info);
            return new ClientRow(
                g.Key, info.Name ?? $"#{g.Key}", info.Phone ?? "", info.Account, info.OptedOut,
                g.Count(m => m.Direction == MessageDirection.Outbound),
                g.Count(m => m.Direction == MessageDirection.Inbound),
                g.Count(m => m.Direction == MessageDirection.Note),
                g.Count(m => m.Direction == MessageDirection.Outbound && MessageStatus.IsFailure(m.Status)),
                string.Join(", ", g.Where(m => m.SentBy != null).Select(m => m.SentBy!.Split('\\').Last())
                                   .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x)),
                g.Max(m => (DateTime?)m.CreatedAtUtc));
        }).ToList();

        // ---- over time (daily up to ~3 months, weekly beyond)
        var days = (r.ToLocal - r.FromLocal).Days + 1;
        r.PeriodUnit = days > 92 ? "week" : "day";
        DateTime Bucket(DateTime utc)
        {
            var d = _time.ToLocal(utc).Date;
            return r.PeriodUnit == "week" ? d.AddDays(-(((int)d.DayOfWeek + 6) % 7)) : d;   // weeks start Monday
        }
        var grouped = inRange.Where(m => m.Direction != MessageDirection.Note).GroupBy(m => Bucket(m.CreatedAtUtc))
                             .ToDictionary(g => g.Key, g => g.ToList());
        var start = r.PeriodUnit == "week" ? r.FromLocal.AddDays(-(((int)r.FromLocal.DayOfWeek + 6) % 7)) : r.FromLocal;
        for (var d = start; d <= r.ToLocal; d = d.AddDays(r.PeriodUnit == "week" ? 7 : 1))
        {
            grouped.TryGetValue(d, out var list);
            list ??= new List<M>();
            r.Periods.Add(new PeriodRow(d,
                r.PeriodUnit == "week" ? "Wk of " + d.ToString("M/d") : d.ToString("ddd M/d"),
                list.Count(m => m.Direction == MessageDirection.Outbound),
                list.Count(m => m.Direction == MessageDirection.Inbound),
                list.Count(m => m.Status == MessageStatus.Delivered),
                list.Count(m => m.Direction == MessageDirection.Outbound && MessageStatus.IsFailure(m.Status))));
        }

        if (!string.IsNullOrWhiteSpace(staffFilter))
            r.Staff = r.Staff.Where(s => string.Equals(s.User, staffFilter, StringComparison.OrdinalIgnoreCase)).ToList();

        return r;
    }

    private static TimeSpan? Median(IEnumerable<TimeSpan> values)
    {
        var v = values.OrderBy(x => x).ToList();
        if (v.Count == 0) return null;
        return v.Count % 2 == 1 ? v[v.Count / 2] : TimeSpan.FromTicks((v[v.Count / 2 - 1].Ticks + v[v.Count / 2].Ticks) / 2);
    }

    public static string FormatDuration(TimeSpan? t)
    {
        if (t is not { } d) return "–";
        if (d.TotalMinutes < 1) return "< 1 min";
        if (d.TotalHours < 1) return $"{(int)d.TotalMinutes} min";
        if (d.TotalDays < 1) return $"{d.TotalHours:0.#} hr";
        return $"{d.TotalDays:0.#} days";
    }
}
