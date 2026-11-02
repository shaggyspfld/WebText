using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using WebText.Web.Data;
using WebText.Web.Services;

namespace WebText.Web.Pages;

/// <summary>Searchable audit log of every text and note, with CSV export.</summary>
public class LogModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly TimeDisplay _time;
    public LogModel(AppDbContext db, TimeDisplay time) { _db = db; _time = time; }

    public const int PageSize = 100;

    [BindProperty(SupportsGet = true)] public string? Q { get; set; }
    [BindProperty(SupportsGet = true)] public string? SentBy { get; set; }
    [BindProperty(SupportsGet = true)] public string? Direction { get; set; }
    [BindProperty(SupportsGet = true)] public string? Status { get; set; }
    [BindProperty(SupportsGet = true)] public DateTime? From { get; set; }
    [BindProperty(SupportsGet = true)] public DateTime? To { get; set; }
    [BindProperty(SupportsGet = true)] public Guid? Batch { get; set; }
    [BindProperty(SupportsGet = true)] public int P { get; set; } = 1;

    public List<Message> Rows { get; private set; } = new();
    public int Total { get; private set; }
    public List<string> Users { get; private set; } = new();
    public Dictionary<string, int> BatchSummary { get; private set; } = new();

    public async Task OnGetAsync()
    {
        Users = await _db.Messages.Where(m => m.SentBy != null).Select(m => m.SentBy!).Distinct().OrderBy(x => x).ToListAsync();

        var q = Filtered();
        Total = await q.CountAsync();
        P = Math.Max(1, P);
        Rows = await q.Include(m => m.Client)
            .OrderByDescending(m => m.CreatedAtUtc).ThenByDescending(m => m.Id)
            .Skip((P - 1) * PageSize).Take(PageSize).ToListAsync();

        if (Batch != null)
            BatchSummary = await _db.Messages.Where(m => m.BatchId == Batch)
                .GroupBy(m => m.Status).Select(g => new { g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.Key, x => x.Count);
    }

    public async Task<IActionResult> OnGetExportAsync()
    {
        var rows = await Filtered().Include(m => m.Client)
            .OrderBy(m => m.CreatedAtUtc).ThenBy(m => m.Id).Take(100_000).ToListAsync();

        var sb = new StringBuilder();
        sb.AppendLine("Date,Client,Account #,Phone,Direction,Staff,Status,Message,Error");
        foreach (var m in rows)
        {
            sb.AppendJoin(',', new[]
            {
                _time.Local(m.CreatedAtUtc, "yyyy-MM-dd HH:mm:ss"),
                m.Client.DisplayName,
                m.Client.AccountNumber ?? "",
                PhoneNumber.Format(m.Client.PhoneE164),
                m.Direction.ToString(),
                m.SentBy ?? "",
                m.Status,
                m.Body,
                m.ErrorDetail ?? ""
            }.Select(Csv.Escape));
            sb.AppendLine();
        }
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
        return File(bytes, "text/csv", $"text-log-{DateTime.Now:yyyyMMdd-HHmm}.csv");
    }

    private IQueryable<Message> Filtered()
    {
        var q = _db.Messages.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(Q))
        {
            var term = Q.Trim();
            var phone = PhoneNumber.ToE164(term);
            q = q.Where(m => m.Body.Contains(term) ||
                             m.Client.FirstName.Contains(term) || m.Client.LastName.Contains(term) ||
                             (m.Client.AccountNumber != null && m.Client.AccountNumber.Contains(term)) ||
                             (phone != null && m.Client.PhoneE164 == phone));
        }
        if (!string.IsNullOrWhiteSpace(SentBy)) q = q.Where(m => m.SentBy == SentBy);
        if (Enum.TryParse<MessageDirection>(Direction, true, out var dir)) q = q.Where(m => m.Direction == dir);
        if (Status == "failed")
            q = q.Where(m => m.Status == MessageStatus.Failed || m.Status == MessageStatus.SendingFailed ||
                             m.Status == MessageStatus.DeliveryFailed || m.Status == MessageStatus.Blocked);
        else if (!string.IsNullOrWhiteSpace(Status)) q = q.Where(m => m.Status == Status);
        if (From is { } f) { var u = _time.ToUtc(f.Date); q = q.Where(m => m.CreatedAtUtc >= u); }
        if (To is { } t) { var u = _time.ToUtc(t.Date.AddDays(1)); q = q.Where(m => m.CreatedAtUtc < u); }
        if (Batch is { } b) q = q.Where(m => m.BatchId == b);
        return q;
    }

    public string PageUrl(int p)
    {
        var qs = new Dictionary<string, string?>
        {
            ["q"] = Q, ["sentBy"] = SentBy, ["direction"] = Direction, ["status"] = Status,
            ["from"] = From?.ToString("yyyy-MM-dd"), ["to"] = To?.ToString("yyyy-MM-dd"),
            ["batch"] = Batch?.ToString(), ["p"] = p.ToString()
        };
        return "?" + string.Join("&", qs.Where(kv => !string.IsNullOrEmpty(kv.Value))
                                       .Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value!)}"));
    }
}
