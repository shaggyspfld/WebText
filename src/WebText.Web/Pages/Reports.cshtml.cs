using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WebText.Web.Services;

namespace WebText.Web.Pages;

[Authorize(Policy = WebText.Web.Auth.Policies.Admin)]
public class ReportsModel : PageModel
{
    private readonly ReportService _reports;
    private readonly TimeDisplay _time;
    public ReportsModel(ReportService reports, TimeDisplay time) { _reports = reports; _time = time; }

    public const int ClientRowsShown = 100;

    [BindProperty(SupportsGet = true)] public string? Range { get; set; }
    [BindProperty(SupportsGet = true)] public DateTime? From { get; set; }
    [BindProperty(SupportsGet = true)] public DateTime? To { get; set; }
    [BindProperty(SupportsGet = true)] public string? Sort { get; set; }

    public UsageReport Report { get; private set; } = null!;
    public List<ClientRow> ClientRows { get; private set; } = new();

    public static readonly (string Key, string Label)[] Ranges =
    {
        ("7d", "Last 7 days"), ("30d", "Last 30 days"), ("month", "This month"), ("lastmonth", "Last month"),
        ("quarter", "This quarter"), ("ytd", "Year to date"), ("12m", "Last 12 months"), ("custom", "Custom dates")
    };

    public async Task OnGetAsync()
    {
        var (from, to) = ResolveRange();
        Report = await _reports.BuildAsync(from, to);
        ClientRows = SortClients(Report.Clients).Take(ClientRowsShown).ToList();
    }

    public async Task<IActionResult> OnGetExportAsync(string what)
    {
        var (from, to) = ResolveRange();
        var r = await _reports.BuildAsync(from, to);
        var sb = new StringBuilder();
        string Row(params object?[] cells) => string.Join(",", cells.Select(c => Csv.Escape(c?.ToString())));
        string Dur(TimeSpan? t) => t is { } d ? d.TotalMinutes.ToString("0") : "";

        switch (what)
        {
            case "staff":
                sb.AppendLine("Windows user,Texts sent,One-to-one,Bulk,Delivered,Failed,SMS segments,Clients texted,Notes,Replies handled,Median response (minutes),Last activity");
                foreach (var s in r.Staff)
                    sb.AppendLine(Row(s.User, s.Sent, s.OneToOne, s.Bulk, s.Delivered, s.Failed, s.Segments, s.ClientsTexted,
                        s.Notes, s.RepliesHandled, Dur(s.MedianResponse), _time.Local(s.LastActivityUtc, "yyyy-MM-dd HH:mm")));
                break;
            case "clients":
                sb.AppendLine("Client,Mobile,Account #,Opted out,Texts sent,Texts received,Total,Notes,Failed,Staff involved,Last activity");
                foreach (var c in SortClients(r.Clients))
                    sb.AppendLine(Row(c.Name, c.Phone, c.Account, c.OptedOut ? "Yes" : "No", c.Sent, c.Received, c.Total,
                        c.Notes, c.Failed, c.Staff, _time.Local(c.LastActivityUtc, "yyyy-MM-dd HH:mm")));
                break;
            case "periods":
                sb.AppendLine($"{(r.PeriodUnit == "week" ? "Week starting" : "Date")},Texts sent,Texts received,Delivered,Failed");
                foreach (var p in r.Periods)
                    sb.AppendLine(Row(p.Start.ToString("yyyy-MM-dd"), p.Sent, p.Received, p.Delivered, p.Failed));
                break;
            default:
                sb.AppendLine("Metric,Value");
                sb.AppendLine(Row("From", r.FromLocal.ToString("yyyy-MM-dd")));
                sb.AppendLine(Row("To", r.ToLocal.ToString("yyyy-MM-dd")));
                sb.AppendLine(Row("Texts sent", r.Sent));
                sb.AppendLine(Row("  one-to-one", r.OneToOneTexts));
                sb.AppendLine(Row("  in bulk sends", r.BulkTexts));
                sb.AppendLine(Row("  auto-replies", r.AutoReplies));
                sb.AppendLine(Row("Bulk sends", r.BulkSends));
                sb.AppendLine(Row("Delivered", r.Delivered));
                sb.AppendLine(Row("Failed", r.Failed));
                sb.AppendLine(Row("Blocked (opted out)", r.Blocked));
                sb.AppendLine(Row("Still in progress", r.InFlight));
                sb.AppendLine(Row("Delivery rate %", r.DeliveryRate is { } dr ? (dr * 100).ToString("0.0") : ""));
                sb.AppendLine(Row("Texts received", r.Received));
                sb.AppendLine(Row("Internal notes", r.Notes));
                sb.AppendLine(Row("Clients texted", r.ClientsTexted));
                sb.AppendLine(Row("Clients who texted in", r.ClientsReplied));
                sb.AppendLine(Row("Reply rate %", r.ReplyRate is { } rr ? (rr * 100).ToString("0.0") : ""));
                sb.AppendLine(Row("Client texts answered", r.InboundAnswered));
                sb.AppendLine(Row("Client texts awaiting a reply", r.InboundUnanswered));
                sb.AppendLine(Row("Median response (minutes)", Dur(r.MedianResponse)));
                sb.AppendLine(Row("Answered within 1 hour", r.AnsweredWithinHour));
                sb.AppendLine(Row("Active staff", r.ActiveStaff));
                sb.AppendLine(Row("Opt-outs", r.OptOuts));
                sb.AppendLine(Row("New unknown numbers", r.UnknownNumbers));
                sb.AppendLine(Row("SMS segments sent", r.SegmentsSent));
                sb.AppendLine(Row("SMS segments received", r.SegmentsReceived));
                if (r.EstimatedCost is { } cost) sb.AppendLine(Row("Estimated texting cost", cost.ToString("0.00")));
                what = "summary";
                break;
        }

        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
        return File(bytes, "text/csv", $"texting-{what}-{r.FromLocal:yyyyMMdd}-{r.ToLocal:yyyyMMdd}.csv");
    }

    private IEnumerable<ClientRow> SortClients(IEnumerable<ClientRow> rows) => Sort switch
    {
        "sent" => rows.OrderByDescending(c => c.Sent).ThenBy(c => c.Name),
        "received" => rows.OrderByDescending(c => c.Received).ThenBy(c => c.Name),
        "recent" => rows.OrderByDescending(c => c.LastActivityUtc),
        "name" => rows.OrderBy(c => c.Name),
        "failed" => rows.OrderByDescending(c => c.Failed).ThenBy(c => c.Name),
        _ => rows.OrderByDescending(c => c.Total).ThenBy(c => c.Name)
    };

    private (DateTime From, DateTime To) ResolveRange()
    {
        var today = _time.ToLocal(DateTime.UtcNow).Date;
        Range ??= From != null || To != null ? "custom" : "30d";
        var (f, t) = Range switch
        {
            "7d" => (today.AddDays(-6), today),
            "month" => (new DateTime(today.Year, today.Month, 1), today),
            "lastmonth" => (new DateTime(today.Year, today.Month, 1).AddMonths(-1), new DateTime(today.Year, today.Month, 1).AddDays(-1)),
            "quarter" => (new DateTime(today.Year, (today.Month - 1) / 3 * 3 + 1, 1), today),
            "ytd" => (new DateTime(today.Year, 1, 1), today),
            "12m" => (today.AddMonths(-12).AddDays(1), today),
            "custom" => ((From ?? today.AddDays(-29)).Date, (To ?? today).Date),
            _ => (today.AddDays(-29), today)
        };
        if (t < f) (f, t) = (t, f);
        if ((t - f).TotalDays > 3 * 366) f = t.AddYears(-3);   // keep the in-memory aggregation bounded
        From = f; To = t;
        return (f, t);
    }

    public string Link(string? sort = null, string? handler = null, string? what = null)
    {
        var qs = new Dictionary<string, string?>
        {
            ["range"] = Range,
            ["from"] = Range == "custom" ? From?.ToString("yyyy-MM-dd") : null,
            ["to"] = Range == "custom" ? To?.ToString("yyyy-MM-dd") : null,
            ["sort"] = sort ?? Sort, ["handler"] = handler, ["what"] = what
        };
        return "/Reports?" + string.Join("&", qs.Where(kv => !string.IsNullOrEmpty(kv.Value))
                                                .Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value!)}"));
    }
}
