using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using WebText.Web.Api;
using WebText.Web.Data;
using WebText.Web.Services;

namespace WebText.Web.Pages.Groups;

public class EditModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly TimeDisplay _time;
    public EditModel(AppDbContext db, TimeDisplay time) { _db = db; _time = time; }

    [BindProperty(SupportsGet = true)] public int Id { get; set; }
    [BindProperty(SupportsGet = true)] public string? Q { get; set; }

    [BindProperty] public string? Name { get; set; }
    [BindProperty] public string? Description { get; set; }
    [BindProperty] public string? Identifiers { get; set; }
    [BindProperty] public int? ClientId { get; set; }
    [BindProperty] public List<int> Selected { get; set; } = new();

    public ClientGroup Group { get; private set; } = null!;
    public record Row(int ClientId, string Name, string Phone, string? Account, bool OptedOut, DateTime AddedAtUtc, string AddedBy);
    public List<Row> Members { get; private set; } = new();
    public int TotalMembers { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        if (!await LoadAsync()) return NotFound();
        Name = Group.Name; Description = Group.Description;
        return Page();
    }

    public async Task<IActionResult> OnGetExportAsync()
    {
        var g = await _db.ClientGroups.AsNoTracking().FirstOrDefaultAsync(x => x.Id == Id);
        if (g == null) return NotFound();
        var q = _db.Clients.AsNoTracking().Where(c => c.GroupMemberships.Any(m => m.GroupId == Id));
        var bytes = await ClientLookup.ExportCsvAsync(_db, q, _time);
        var safe = new string(g.Name.Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray());
        return File(bytes, "text/csv", $"list-{safe}-{DateTime.Now:yyyyMMdd}.csv");
    }

    public async Task<IActionResult> OnPostSaveAsync()
    {
        var g = await _db.ClientGroups.FindAsync(Id);
        if (g == null) return NotFound();
        var name = (Name ?? "").Trim();
        if (name.Length == 0 || name.Length > 100) return Flash("List name must be 1–100 characters.", true);
        if (await _db.ClientGroups.AnyAsync(x => x.Name == name && x.Id != Id)) return Flash("Another list already has that name.", true);
        g.Name = name;
        g.Description = string.IsNullOrWhiteSpace(Description) ? null : Description.Trim()[..Math.Min(500, Description.Trim().Length)];
        await _db.SaveChangesAsync();
        return Flash("List saved.");
    }

    /// <summary>Adds pasted account numbers / phone numbers.</summary>
    public async Task<IActionResult> OnPostAddListAsync()
    {
        var (found, unmatched) = await ClientLookup.ResolveIdentifiersAsync(_db, Identifiers);
        var added = await AddAsync(found.Select(c => c.Id));
        var msg = $"Added {added} client{(added == 1 ? "" : "s")}.";
        if (found.Count > added) msg += $" {found.Count - added} already on the list.";
        if (unmatched.Count > 0) msg += $" Not found: {string.Join(", ", unmatched.Take(30))}{(unmatched.Count > 30 ? "…" : "")}";
        return Flash(msg, unmatched.Count > 0 && added == 0);
    }

    public async Task<IActionResult> OnPostAddOneAsync()
    {
        if (ClientId is not { } cid) return Flash("Pick a client to add.", true);
        var added = await AddAsync(new[] { cid });
        return Flash(added == 1 ? "Client added." : "That client is already on the list.");
    }

    public async Task<IActionResult> OnPostRemoveAsync()
    {
        var ids = Selected.ToList();
        if (ClientId is { } one) ids.Add(one);
        if (ids.Count == 0) return Flash("Tick the clients to remove.", true);
        var n = 0;
        // EF Core 10 sends one SQL parameter per list item; SQL Server allows 2100 per query.
        foreach (var chunk in ids.Distinct().Chunk(500))
        {
            var list = chunk.ToList();
            n += await _db.ClientGroupMembers.Where(m => m.GroupId == Id && list.Contains(m.ClientId)).ExecuteDeleteAsync();
        }
        return Flash($"Removed {n} client{(n == 1 ? "" : "s")}.");
    }

    public async Task<IActionResult> OnPostClearAsync()
    {
        var n = await _db.ClientGroupMembers.Where(m => m.GroupId == Id).ExecuteDeleteAsync();
        return Flash($"Removed all {n} clients from the list.");
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        var g = await _db.ClientGroups.FindAsync(Id);
        if (g == null) return NotFound();
        _db.ClientGroups.Remove(g);   // memberships cascade; clients and their texts are untouched
        await _db.SaveChangesAsync();
        TempData["Flash"] = $"Deleted list \"{g.Name}\". The clients themselves were not changed.";
        return Redirect("/Groups");
    }

    private async Task<int> AddAsync(IEnumerable<int> clientIds)
    {
        var ids = clientIds.Distinct().ToList();
        var existing = new HashSet<int>(await _db.ClientGroupMembers.Where(m => m.GroupId == Id).Select(m => m.ClientId).ToListAsync());
        var valid = new HashSet<int>();
        foreach (var chunk in ids.Where(i => !existing.Contains(i)).Chunk(500))
        {
            var list = chunk.ToList();
            valid.UnionWith(await _db.Clients.Where(c => list.Contains(c.Id)).Select(c => c.Id).ToListAsync());
        }
        var user = User.UserName();
        foreach (var cid in valid)
            _db.ClientGroupMembers.Add(new ClientGroupMember { GroupId = Id, ClientId = cid, AddedBy = user });
        await _db.SaveChangesAsync();
        return valid.Count;
    }

    private IActionResult Flash(string message, bool error = false)
    {
        TempData[error ? "FlashError" : "Flash"] = message;
        return Redirect($"/Groups/Edit?id={Id}");
    }

    private async Task<bool> LoadAsync()
    {
        var g = await _db.ClientGroups.AsNoTracking().FirstOrDefaultAsync(x => x.Id == Id);
        if (g == null) return false;
        Group = g;

        var q = _db.ClientGroupMembers.AsNoTracking().Where(m => m.GroupId == Id);
        TotalMembers = await q.CountAsync();
        if (!string.IsNullOrWhiteSpace(Q))
        {
            var matching = ClientLookup.Search(_db.Clients, Q).Select(c => c.Id);
            q = q.Where(m => matching.Contains(m.ClientId));
        }
        var rows = await q
            .OrderBy(m => m.Client.LastName).ThenBy(m => m.Client.FirstName)
            .Take(2000)
            .Select(m => new { m.ClientId, m.Client.FirstName, m.Client.LastName, m.Client.PhoneE164, m.Client.AccountNumber,
                               m.Client.OptedOut, m.AddedAtUtc, m.AddedBy })
            .ToListAsync();
        Members = rows.Select(r => new Row(r.ClientId, $"{r.LastName}, {r.FirstName}".Trim(' ', ','),
            PhoneNumber.Format(r.PhoneE164), r.AccountNumber, r.OptedOut, r.AddedAtUtc, r.AddedBy)).ToList();
        return true;
    }
}
