using System.Text;
using Microsoft.EntityFrameworkCore;
using WebText.Web.Data;

namespace WebText.Web.Services;

/// <summary>Shared client lookups used by bulk send, recipient groups and CSV export.</summary>
public static class ClientLookup
{
    /// <summary>
    /// Resolves a pasted list of account numbers and/or phone numbers (one per line, or comma/semicolon/tab separated).
    /// </summary>
    public static async Task<(List<Client> Found, List<string> Unmatched)> ResolveIdentifiersAsync(
        AppDbContext db, string? text, CancellationToken ct = default)
    {
        var tokens = (text ?? "")
            .Split(new[] { '\n', '\r', ',', ';', '\t' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (tokens.Count == 0) return (new List<Client>(), new List<string>());

        var phones = tokens.Select(t => (token: t, e164: PhoneNumber.ToE164(t))).ToList();
        var phoneSet = phones.Where(p => p.e164 != null).Select(p => p.e164!).Distinct().ToList();

        var found = new List<Client>();
        // Chunk to stay well under SQL Server's 2100-parameter limit.
        foreach (var chunk in tokens.Chunk(500))
        {
            var list = chunk.ToList();
            found.AddRange(await db.Clients.AsNoTracking()
                .Where(c => c.AccountNumber != null && list.Contains(c.AccountNumber)).ToListAsync(ct));
        }
        foreach (var chunk in phoneSet.Chunk(500))
        {
            var list = chunk.ToList();
            found.AddRange(await db.Clients.AsNoTracking().Where(c => list.Contains(c.PhoneE164)).ToListAsync(ct));
        }

        var unmatched = phones
            .Where(p => !found.Any(c => string.Equals(c.AccountNumber, p.token, StringComparison.OrdinalIgnoreCase) ||
                                        (p.e164 != null && c.PhoneE164 == p.e164)))
            .Select(p => p.token).ToList();

        var distinct = found.GroupBy(c => c.Id).Select(g => g.First())
                            .OrderBy(c => c.LastName).ThenBy(c => c.FirstName).ToList();
        return (distinct, unmatched);
    }

    /// <summary>Applies the common free-text client search (name, phone, email, account #).</summary>
    public static IQueryable<Client> Search(IQueryable<Client> query, string? q)
    {
        if (string.IsNullOrWhiteSpace(q)) return query;
        var term = q.Trim();
        var phone = PhoneNumber.ToE164(term);
        var digits = new string(term.Where(char.IsDigit).ToArray());
        return query.Where(c =>
            c.FirstName.Contains(term) || c.LastName.Contains(term) ||
            (c.FirstName + " " + c.LastName).Contains(term) ||
            (c.Email != null && c.Email.Contains(term)) ||
            (c.AccountNumber != null && c.AccountNumber.Contains(term)) ||
            (phone != null && c.PhoneE164 == phone) ||
            (digits.Length >= 4 && c.PhoneE164.Contains(digits)));
    }

    /// <summary>
    /// Builds a client CSV. The first five columns match the import format, so an export can be edited in
    /// Excel and imported back to update names and numbers in bulk.
    /// </summary>
    public static async Task<byte[]> ExportCsvAsync(AppDbContext db, IQueryable<Client> query, TimeDisplay time, CancellationToken ct = default)
    {
        var rows = await query
            .OrderBy(c => c.LastName).ThenBy(c => c.FirstName)
            .Select(c => new
            {
                c.FirstName, c.LastName, c.PhoneE164, c.Email, c.AccountNumber, c.OptedOut, c.Notes,
                c.CreatedAtUtc, c.LastMessageAtUtc, c.IsAutoCreated,
                Groups = c.GroupMemberships.Select(m => m.Group.Name).ToList(),
                Sent = c.Messages.Count(m => m.Direction == MessageDirection.Outbound),
                Received = c.Messages.Count(m => m.Direction == MessageDirection.Inbound)
            })
            .ToListAsync(ct);

        var sb = new StringBuilder();
        sb.AppendLine("FirstName,LastName,Mobile,Email,AccountNumber,OptedOut,Groups,TextsSent,TextsReceived,LastText,Created,NeedsName,Notes");
        foreach (var r in rows)
        {
            sb.AppendJoin(',', new[]
            {
                r.FirstName, r.LastName, PhoneNumber.Format(r.PhoneE164), r.Email ?? "", r.AccountNumber ?? "",
                r.OptedOut ? "Yes" : "No",
                string.Join("; ", r.Groups.OrderBy(g => g)),
                r.Sent.ToString(), r.Received.ToString(),
                time.Local(r.LastMessageAtUtc, "yyyy-MM-dd HH:mm"),
                time.Local(r.CreatedAtUtc, "yyyy-MM-dd"),
                r.IsAutoCreated ? "Yes" : "",
                r.Notes ?? ""
            }.Select(Csv.Escape));
            sb.AppendLine();
        }
        return Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
    }
}
