using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using WebText.Web.Api;
using WebText.Web.Auth;
using WebText.Web.Data;

namespace WebText.Web.Pages;

public class TemplatesModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly GroupMembership _groups;
    public TemplatesModel(AppDbContext db, GroupMembership groups) { _db = db; _groups = groups; }

    /// <summary>Everyone can use templates; only admins can create, edit or hide them.</summary>
    public bool CanEdit => _groups.IsAdmin(User);

    public List<MessageTemplate> Items { get; private set; } = new();

    [BindProperty] public int? EditId { get; set; }
    [BindProperty, Required, StringLength(100)] public string Name { get; set; } = "";
    [BindProperty, Required, StringLength(1600)] public string Body { get; set; } = "";

    public async Task OnGetAsync(int? edit)
    {
        await LoadAsync();
        if (edit is { } id && Items.FirstOrDefault(t => t.Id == id) is { } t)
        {
            EditId = t.Id; Name = t.Name; Body = t.Body;
        }
    }

    public async Task<IActionResult> OnPostSaveAsync()
    {
        if (!CanEdit) return Forbid();
        if (!ModelState.IsValid) { await LoadAsync(); return Page(); }
        MessageTemplate? t = null;
        if (EditId is { } id) t = await _db.MessageTemplates.FindAsync(id);
        if (t == null)
        {
            t = new MessageTemplate { CreatedBy = User.UserName() };
            _db.MessageTemplates.Add(t);
        }
        t.Name = Name.Trim();
        t.Body = Body.Trim();
        t.IsActive = true;
        await _db.SaveChangesAsync();
        TempData["Flash"] = $"Saved template \"{t.Name}\".";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostToggleAsync(int id)
    {
        if (!CanEdit) return Forbid();
        var t = await _db.MessageTemplates.FindAsync(id);
        if (t != null) { t.IsActive = !t.IsActive; await _db.SaveChangesAsync(); }
        return RedirectToPage();
    }

    private async Task LoadAsync() =>
        Items = await _db.MessageTemplates.AsNoTracking().OrderByDescending(t => t.IsActive).ThenBy(t => t.Name).ToListAsync();
}
