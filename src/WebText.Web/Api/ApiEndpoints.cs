using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using WebText.Web.Data;
using WebText.Web.Services;

namespace WebText.Web.Api;

public record SendRequest(string Body);
public record NoteRequest(string Body);

/// <summary>JSON endpoints used by the inbox page. All require a signed-in Windows user (fallback policy).</summary>
public static class ApiEndpoints
{
    public static string UserName(this ClaimsPrincipal user) => user.Identity?.Name ?? "unknown";

    public static void MapApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");

        // CSRF guard: browsers send Windows credentials automatically, so state-changing calls must carry a
        // custom header. A cross-site page can't add one without a CORS preflight, which this app never allows.
        api.AddEndpointFilter(async (ctx, next) =>
        {
            var req = ctx.HttpContext.Request;
            if (!HttpMethods.IsGet(req.Method) && req.Headers["X-WebText"] != "1")
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            return await next(ctx);
        });

        api.MapGet("/me", (ClaimsPrincipal user) => Results.Ok(new { name = user.UserName() }));

        // Conversation list: clients with at least one message, newest first. Optional search and unread filter.
        api.MapGet("/conversations", async (AppDbContext db, string? q, bool? unread, int? take) =>
        {
            var query = db.Clients.AsNoTracking().Where(c => c.LastMessageAtUtc != null);

            if (!string.IsNullOrWhiteSpace(q))
            {
                var term = q.Trim();
                var phone = PhoneNumber.ToE164(term);
                var digits = new string(term.Where(char.IsDigit).ToArray());
                query = query.Where(c =>
                    c.FirstName.Contains(term) || c.LastName.Contains(term) ||
                    (c.FirstName + " " + c.LastName).Contains(term) ||
                    (c.AccountNumber != null && c.AccountNumber.Contains(term)) ||
                    (phone != null && c.PhoneE164 == phone) ||
                    (digits.Length >= 4 && c.PhoneE164.Contains(digits)) ||
                    c.Messages.Any(m => m.Body.Contains(term)));
            }
            if (unread == true)
                query = query.Where(c => c.Messages.Any(m => m.Direction == MessageDirection.Inbound && !m.IsRead));

            var rows = await query
                .OrderByDescending(c => c.LastMessageAtUtc)
                .Take(Math.Clamp(take ?? 200, 1, 1000))
                .Select(c => new
                {
                    c.Id, c.FirstName, c.LastName, c.PhoneE164, c.AccountNumber, c.OptedOut, c.IsAutoCreated,
                    c.LastMessageAtUtc,
                    Last = c.Messages.Where(m => m.Direction != MessageDirection.Note)
                                     .OrderByDescending(m => m.CreatedAtUtc)
                                     .Select(m => new { m.Body, m.Direction }).FirstOrDefault(),
                    Unread = c.Messages.Count(m => m.Direction == MessageDirection.Inbound && !m.IsRead)
                })
                .ToListAsync();

            return Results.Ok(rows.Select(r => new ConversationDto(
                r.Id,
                string.IsNullOrWhiteSpace(r.FirstName + r.LastName) ? PhoneNumber.Format(r.PhoneE164) : $"{r.FirstName} {r.LastName}".Trim(),
                r.PhoneE164,
                PhoneNumber.Format(r.PhoneE164),
                r.AccountNumber,
                r.OptedOut,
                r.IsAutoCreated,
                r.LastMessageAtUtc is { } d ? DateTime.SpecifyKind(d, DateTimeKind.Utc) : null,
                r.Last == null ? null : (r.Last.Body.Length > 90 ? r.Last.Body[..90] + "…" : r.Last.Body),
                r.Last?.Direction.ToString().ToLowerInvariant(),
                r.Unread)));
        });

        api.MapGet("/unread-count", async (AppDbContext db) =>
            Results.Ok(new
            {
                count = await db.Messages.CountAsync(m => m.Direction == MessageDirection.Inbound && !m.IsRead)
            }));

        // Client lookup for "start a new conversation".
        api.MapGet("/clients/search", async (AppDbContext db, string q) =>
        {
            if (string.IsNullOrWhiteSpace(q)) return Results.Ok(Array.Empty<object>());
            var term = q.Trim();
            var phone = PhoneNumber.ToE164(term);
            var digits = new string(term.Where(char.IsDigit).ToArray());
            var rows = await db.Clients.AsNoTracking()
                .Where(c => c.FirstName.Contains(term) || c.LastName.Contains(term) ||
                            (c.FirstName + " " + c.LastName).Contains(term) ||
                            (c.AccountNumber != null && c.AccountNumber.Contains(term)) ||
                            (phone != null && c.PhoneE164 == phone) ||
                            (digits.Length >= 4 && c.PhoneE164.Contains(digits)))
                .OrderBy(c => c.LastName).ThenBy(c => c.FirstName)
                .Take(15)
                .Select(c => new { c.Id, c.FirstName, c.LastName, c.PhoneE164, c.AccountNumber, c.OptedOut })
                .ToListAsync();
            return Results.Ok(rows.Select(c => new
            {
                id = c.Id,
                name = $"{c.FirstName} {c.LastName}".Trim(),
                phone = PhoneNumber.Format(c.PhoneE164),
                account = c.AccountNumber,
                optedOut = c.OptedOut
            }));
        });

        api.MapGet("/clients/{id:int}", async (AppDbContext db, int id) =>
        {
            var c = await db.Clients.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
            if (c == null) return Results.NotFound();
            var shared = await db.Clients.CountAsync(x => x.PhoneE164 == c.PhoneE164 && x.Id != id);
            return Results.Ok(ClientDto.From(c, shared));
        });

        // Full thread for one client, oldest first. ?before=<id> pages backwards for long histories.
        api.MapGet("/clients/{id:int}/messages", async (AppDbContext db, int id, long? before, int? take) =>
        {
            var q = db.Messages.AsNoTracking().Include(m => m.Media).Where(m => m.ClientId == id);
            if (before is { } b) q = q.Where(m => m.Id < b);
            var page = await q.OrderByDescending(m => m.CreatedAtUtc).ThenByDescending(m => m.Id)
                              .Take(Math.Clamp(take ?? 200, 1, 1000)).ToListAsync();
            page.Reverse();
            return Results.Ok(page.Select(MessageDto.From));
        });

        api.MapPost("/clients/{id:int}/messages", async (MessagingService svc, ClaimsPrincipal user, int id, SendRequest req) =>
        {
            var (msg, error) = await svc.QueueOutboundAsync(id, req.Body, user.UserName());
            return msg == null ? Results.BadRequest(new { error }) : Results.Ok(MessageDto.From(msg));
        });

        api.MapPost("/clients/{id:int}/notes", async (MessagingService svc, ClaimsPrincipal user, int id, NoteRequest req) =>
        {
            var (msg, error) = await svc.AddNoteAsync(id, req.Body, user.UserName());
            return msg == null ? Results.BadRequest(new { error }) : Results.Ok(MessageDto.From(msg));
        });

        api.MapPost("/clients/{id:int}/read", async (MessagingService svc, ClaimsPrincipal user, int id) =>
        {
            await svc.MarkThreadReadAsync(id, user.UserName());
            return Results.Ok();
        });

        // Resend a failed text as a new message (keeps the failed one in history).
        api.MapPost("/messages/{id:long}/resend", async (AppDbContext db, MessagingService svc, ClaimsPrincipal user, long id) =>
        {
            var m = await db.Messages.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
            if (m == null || m.Direction != MessageDirection.Outbound) return Results.NotFound();
            if (!MessageStatus.IsFailure(m.Status)) return Results.BadRequest(new { error = "Only failed messages can be resent." });
            var (msg, error) = await svc.QueueOutboundAsync(m.ClientId, m.Body, user.UserName());
            return msg == null ? Results.BadRequest(new { error }) : Results.Ok(MessageDto.From(msg));
        });

        api.MapGet("/templates", async (AppDbContext db) =>
            Results.Ok(await db.MessageTemplates.AsNoTracking().Where(t => t.IsActive)
                .OrderBy(t => t.Name).Select(t => new { t.Id, t.Name, t.Body }).ToListAsync()));
    }
}
