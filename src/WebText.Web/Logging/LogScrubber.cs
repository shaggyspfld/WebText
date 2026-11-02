using System.Reflection;
using Microsoft.EntityFrameworkCore;
using WebText.Web.Data;

namespace WebText.Web.Logging;

/// <summary>
/// One-off clean-up for rows saved before redaction existed: masks SSN-like numbers in the log-type columns of
/// existing rows. Run by the installer's database step; safe to run repeatedly (rows already clean aren't touched).
/// </summary>
public static class LogScrubber
{
    private const int Batch = 500;

    public static async Task<int> ScrubAsync(AppDbContext db, CancellationToken ct = default)
    {
        var changed = 0;
        changed += await ScrubTableAsync(db, (after, n) => db.WebhookEvents.Where(x => x.Id > after).OrderBy(x => x.Id).Take(n), x => x.Id, ct);
        changed += await ScrubTableAsync(db, (after, n) => db.Messages.Where(x => x.Id > after && x.ErrorDetail != null).OrderBy(x => x.Id).Take(n), x => x.Id, ct);
        changed += await ScrubTableAsync(db, (after, n) => db.AppSettingChanges.Where(x => x.Id > after).OrderBy(x => x.Id).Take(n), x => x.Id, ct);
        changed += await ScrubTableAsync(db, (after, n) => db.AutoResponderChanges.Where(x => x.Id > after).OrderBy(x => x.Id).Take(n), x => x.Id, ct);
        changed += await ScrubTableAsync(db, (after, n) => db.SecurityEvents.Where(x => x.Id > after).OrderBy(x => x.Id).Take(n), x => x.Id, ct);
        return changed;
    }

    private static async Task<int> ScrubTableAsync<T>(AppDbContext db, Func<long, int, IQueryable<T>> page, Func<T, long> id,
        CancellationToken ct) where T : class
    {
        var props = typeof(T).GetProperties()
            .Where(p => p.PropertyType == typeof(string) && p.IsDefined(typeof(RedactSensitiveAttribute), true)).ToArray();
        if (props.Length == 0) return 0;

        var changed = 0;
        long after = 0;
        while (true)
        {
            var rows = await page(after, Batch).ToListAsync(ct);
            if (rows.Count == 0) break;
            foreach (var row in rows)
            {
                var dirty = false;
                foreach (var p in props)
                {
                    if (p.GetValue(row) is string v && SensitiveData.NeedsRedaction(v))
                    {
                        p.SetValue(row, SensitiveData.Redact(v));
                        dirty = true;
                    }
                }
                if (dirty) changed++;
            }
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
            after = id(rows[^1]);
        }
        return changed;
    }
}
