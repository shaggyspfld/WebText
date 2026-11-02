using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using WebText.Web.Api;
using WebText.Web.Data;

namespace WebText.Web.Pages.Groups;

public class IndexModel : PageModel
{
    private readonly AppDbContext _db;
    public IndexModel(AppDbContext db) => _db = db;

    public record Row(int Id, string Name, string? Description, int Members, int OptedOut, DateTime CreatedAtUtc, string CreatedBy);
    public List<Row> Rows { get; private set; } = new();

    [BindProperty, Required, StringLength(100)] public string Name { get; set; } = "";
    [BindProperty, StringLength(500)] public string? Description { get; set; }

    public async Task OnGetAsync() => await LoadAsync();

    public async Task<IActionResult> OnPostCreateAsync()
    {
        Name = (Name ?? "").Trim();
        if (ModelState.IsValid && await _db.ClientGroups.AnyAsync(g => g.Name == Name))
            ModelState.AddModelError(nameof(Name), "A list with that name already exists.");
        if (!ModelState.IsValid) { await LoadAsync(); return Page(); }

        var g = new ClientGroup { Name = Name, Description = Description?.Trim(), CreatedBy = User.UserName() };
        _db.ClientGroups.Add(g);
        await _db.SaveChangesAsync();
        return Redirect($"/Groups/Edit?id={g.Id}");
    }

    private async Task LoadAsync() =>
        Rows = await _db.ClientGroups.AsNoTracking().OrderBy(g => g.Name)
            .Select(g => new Row(g.Id, g.Name, g.Description, g.Members.Count(),
                                 g.Members.Count(m => m.Client.OptedOut), g.CreatedAtUtc, g.CreatedBy))
            .ToListAsync();
}
