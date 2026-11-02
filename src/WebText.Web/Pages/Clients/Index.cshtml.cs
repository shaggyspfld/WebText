using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using WebText.Web.Api;
using WebText.Web.Data;
using WebText.Web.Services;

namespace WebText.Web.Pages.Clients;

public class IndexModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly TimeDisplay _time;
    public IndexModel(AppDbContext db, TimeDisplay time) { _db = db; _time = time; }

    public const int PageSize = 50;

    [BindProperty(SupportsGet = true)] public string? Q { get; set; }
    /// <summary>Filter: a list id, or "none" for clients on no list.</summary>
    [BindProperty(SupportsGet = true)] public string? List { get; set; }
    /// <summary>Filter: "optedout", "needsname", "nevertexted".</summary>
    [BindProperty(SupportsGet = true)] public string? Show { get; set; }
    [BindProperty(SupportsGet = true)] public int P { get; set; } = 1;

    [BindProperty] public List<int> Selected { get; set; } = new();
    [BindProperty] public int? TargetGroupId { get; set; }

    public List<Row> Rows { get; private set; } = new();
    public int Total { get; private set; }
    public List<ClientGroup> Groups { get; private set; } = new();

    public record Row(int Id, string Name, string Phone, string? Account, string? Email, bool OptedOut,
        bool AutoCreated, DateTime? LastMessageAtUtc, int MessageCount, List<string> Lists);

    public async Task OnGetAsync()
    {
        Groups = await _db.ClientGroups.AsNoTracking().OrderBy(g => g.Name).ToListAsync();
        var query = Filtered();

        Total = await query.CountAsync();
        P = Math.Max(1, P);
        var rows = await query
            .OrderBy(c => c.LastName).ThenBy(c => c.FirstName)
            .Skip((P - 1) * PageSize).Take(PageSize)
            .Select(c => new
            {
                c.Id, c.FirstName, c.LastName, c.PhoneE164, c.AccountNumber, c.Email, c.OptedOut, c.IsAutoCreated,
                c.LastMessageAtUtc,
                Count = c.Messages.Count(m => m.Direction != MessageDirection.Note),
                Lists = c.GroupMemberships.Select(m => m.Group.Name).ToList()
            })
            .ToListAsync();

        Rows = rows.Select(r => new Row(r.Id, $"{r.LastName}, {r.FirstName}".Trim(' ', ','),
            PhoneNumber.Format(r.PhoneE164), r.AccountNumber, r.Email, r.OptedOut, r.IsAutoCreated,
            r.LastMessageAtUtc, r.Count, r.Lists.OrderBy(x => x).ToList())).ToList();
    }

    /// <summary>Exports every client matching the current search/filter (not just the visible page).</summary>
    public async Task<IActionResult> OnGetExportAsync()
    {
        var bytes = await ClientLookup.ExportCsvAsync(_db, Filtered(), _time);
        return File(bytes, "text/csv", $"clients-{DateTime.Now:yyyyMMdd-HHmm}.csv");
    }

    public async Task<IActionResult> OnPostAddToListAsync()
    {
        if (TargetGroupId is not { } gid || !await _db.ClientGroups.AnyAsync(g => g.Id == gid))
        {
            TempData["FlashError"] = "Choose a list to add the selected clients to.";
            return Redirect(ReturnUrl());
        }
        if (Selected.Count == 0)
        {
            TempData["FlashError"] = "Tick at least one client.";
            return Redirect(ReturnUrl());
        }
        var existing = await _db.ClientGroupMembers.Where(m => m.GroupId == gid && Selected.Contains(m.ClientId))
                                .Select(m => m.ClientId).ToListAsync();
        var user = User.UserName();
        var toAdd = Selected.Distinct().Except(existing).ToList();
        foreach (var cid in toAdd)
            _db.ClientGroupMembers.Add(new ClientGroupMember { GroupId = gid, ClientId = cid, AddedBy = user });
        await _db.SaveChangesAsync();

        var name = await _db.ClientGroups.Where(g => g.Id == gid).Select(g => g.Name).FirstAsync();
        TempData["Flash"] = $"Added {toAdd.Count} client{(toAdd.Count == 1 ? "" : "s")} to \"{name}\"." +
                            (existing.Count > 0 ? $" {existing.Count} were already on it." : "");
        return Redirect(ReturnUrl());
    }

    private IQueryable<Client> Filtered()
    {
        var query = ClientLookup.Search(_db.Clients.AsNoTracking(), Q);
        if (List == "none") query = query.Where(c => !c.GroupMemberships.Any());
        else if (int.TryParse(List, out var gid)) query = query.Where(c => c.GroupMemberships.Any(m => m.GroupId == gid));
        query = Show switch
        {
            "optedout" => query.Where(c => c.OptedOut),
            "needsname" => query.Where(c => c.IsAutoCreated),
            "nevertexted" => query.Where(c => c.LastMessageAtUtc == null),
            _ => query
        };
        return query;
    }

    public string FilterQuery(int? page = null, string? handler = null)
    {
        var qs = new Dictionary<string, string?>
        {
            ["q"] = Q, ["list"] = List, ["show"] = Show,
            ["p"] = page?.ToString(), ["handler"] = handler
        };
        return "?" + string.Join("&", qs.Where(kv => !string.IsNullOrEmpty(kv.Value))
                                       .Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value!)}"));
    }

    private string ReturnUrl() => "/Clients" + FilterQuery(P > 1 ? P : null);
}
