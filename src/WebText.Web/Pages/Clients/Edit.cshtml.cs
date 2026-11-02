using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using WebText.Web.Api;
using WebText.Web.Auth;
using WebText.Web.Data;
using WebText.Web.Services;

namespace WebText.Web.Pages.Clients;

public class EditModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly GroupMembership _groups;
    public EditModel(AppDbContext db, GroupMembership groups) { _db = db; _groups = groups; }

    public bool IsAdmin => _groups.IsAdmin(User);

    [BindProperty(SupportsGet = true)] public int? Id { get; set; }
    [BindProperty] public InputModel Input { get; set; } = new();
    [BindProperty] public int? MergeIntoId { get; set; }
    [BindProperty] public List<int> GroupIds { get; set; } = new();

    public List<ClientGroup> AllGroups { get; private set; } = new();

    public Client? Existing { get; private set; }
    public List<Client> SharingNumber { get; private set; } = new();
    public int MessageCount { get; private set; }

    public class InputModel
    {
        [Required, StringLength(100), Display(Name = "First name")] public string FirstName { get; set; } = "";
        [StringLength(100), Display(Name = "Last name")] public string? LastName { get; set; }
        [Required, Display(Name = "Mobile number")] public string Phone { get; set; } = "";
        [EmailAddress, StringLength(200)] public string? Email { get; set; }
        [StringLength(50), Display(Name = "Account #")] public string? AccountNumber { get; set; }
        [StringLength(2000)] public string? Notes { get; set; }
        [Display(Name = "Opted out of texts")] public bool OptedOut { get; set; }
    }

    public async Task<IActionResult> OnGetAsync(int? group)
    {
        AllGroups = await _db.ClientGroups.AsNoTracking().OrderBy(g => g.Name).ToListAsync();
        if (group is { } preselect) GroupIds.Add(preselect);
        if (Id is { } id)
        {
            if (!await LoadAsync(id)) return NotFound();
            Input = new InputModel
            {
                FirstName = Existing!.IsAutoCreated && Existing.FirstName == "Unknown" ? "" : Existing.FirstName,
                LastName = Existing.IsAutoCreated && Existing.FirstName == "Unknown" ? "" : Existing.LastName,
                Phone = PhoneNumber.Format(Existing.PhoneE164),
                Email = Existing.Email,
                AccountNumber = Existing.AccountNumber,
                Notes = Existing.Notes,
                OptedOut = Existing.OptedOut
            };
            GroupIds = await _db.ClientGroupMembers.Where(m => m.ClientId == id).Select(m => m.GroupId).ToListAsync();
        }
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var phone = PhoneNumber.ToE164(Input.Phone);
        if (phone == null) ModelState.AddModelError("Input.Phone", "Enter a valid mobile number, e.g. (312) 555-0100.");

        if (Id is { } id && !await LoadAsync(id)) return NotFound();
        if (!ModelState.IsValid)
        {
            AllGroups = await _db.ClientGroups.AsNoTracking().OrderBy(g => g.Name).ToListAsync();
            return Page();
        }

        var user = User.UserName();
        var c = Existing ?? new Client { CreatedBy = user };
        c.FirstName = Input.FirstName.Trim();
        c.LastName = (Input.LastName ?? "").Trim();
        c.PhoneE164 = phone!;
        c.Email = string.IsNullOrWhiteSpace(Input.Email) ? null : Input.Email.Trim();
        c.AccountNumber = string.IsNullOrWhiteSpace(Input.AccountNumber) ? null : Input.AccountNumber.Trim();
        c.Notes = Input.Notes;
        if (c.OptedOut != Input.OptedOut)
        {
            c.OptedOut = Input.OptedOut;
            c.OptedOutAtUtc = Input.OptedOut ? DateTime.UtcNow : null;
        }
        c.IsAutoCreated = false;
        c.UpdatedAtUtc = DateTime.UtcNow;
        c.UpdatedBy = user;

        if (Existing == null) _db.Clients.Add(c);
        await _db.SaveChangesAsync();

        // Sync recipient-list membership with the ticked boxes.
        var current = await _db.ClientGroupMembers.Where(m => m.ClientId == c.Id).ToListAsync();
        var wanted = GroupIds.Distinct().ToHashSet();
        _db.ClientGroupMembers.RemoveRange(current.Where(m => !wanted.Contains(m.GroupId)));
        var wantedList = wanted.ToList();
        var validIds = await _db.ClientGroups.Where(g => wantedList.Contains(g.Id)).Select(g => g.Id).ToListAsync();
        foreach (var gid in validIds.Where(g => current.All(m => m.GroupId != g)))
            _db.ClientGroupMembers.Add(new ClientGroupMember { GroupId = gid, ClientId = c.Id, AddedBy = user });
        await _db.SaveChangesAsync();

        TempData["Flash"] = $"Saved {c.DisplayName}.";
        return Redirect(Existing == null ? $"/?client={c.Id}" : $"/Clients/Edit?id={c.Id}");
    }

    /// <summary>Moves every message from this record into another client, then deletes this record.</summary>
    public async Task<IActionResult> OnPostMergeAsync()
    {
        if (Id is not { } id || MergeIntoId is not { } targetId || id == targetId)
        {
            TempData["FlashError"] = "Choose a different client to merge into.";
            return Redirect($"/Clients/Edit?id={Id}");
        }
        var source = await _db.Clients.FirstOrDefaultAsync(c => c.Id == id);
        var target = await _db.Clients.FirstOrDefaultAsync(c => c.Id == targetId);
        if (source == null || target == null) return NotFound();

        // SQL Server retry strategy requires user transactions to run inside the execution strategy.
        var strategy = _db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync();
            await _db.Messages.Where(m => m.ClientId == id)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.ClientId, targetId));
            _db.Messages.Add(new Message
            {
                ClientId = targetId,
                Direction = MessageDirection.Note,
                Body = $"Merged texts from {source.DisplayName} ({PhoneNumber.Format(source.PhoneE164)}) into this client.",
                Status = MessageStatus.Note,
                SentBy = User.UserName(),
                IsRead = true,
                Segments = 0
            });
            if (source.LastMessageAtUtc > (target.LastMessageAtUtc ?? DateTime.MinValue))
                target.LastMessageAtUtc = source.LastMessageAtUtc;
            if (source.OptedOut && source.PhoneE164 == target.PhoneE164) target.OptedOut = true;
            _db.Clients.Remove(source);
            await _db.SaveChangesAsync();
            await tx.CommitAsync();
        });

        TempData["Flash"] = $"Merged into {target.DisplayName}.";
        return Redirect($"/?client={targetId}");
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        if (!IsAdmin) return Forbid();
        if (Id is not { } id || !await LoadAsync(id)) return NotFound();
        if (MessageCount > 0)
        {
            TempData["FlashError"] = "Clients with message history can't be deleted (the history is part of the client record). Merge instead, or mark them opted out.";
            return Redirect($"/Clients/Edit?id={id}");
        }
        _db.Clients.Remove(Existing!);
        await _db.SaveChangesAsync();
        TempData["Flash"] = "Client deleted.";
        return Redirect("/Clients");
    }

    private async Task<bool> LoadAsync(int id)
    {
        Existing = await _db.Clients.FirstOrDefaultAsync(c => c.Id == id);
        if (Existing == null) return false;
        SharingNumber = await _db.Clients.AsNoTracking()
            .Where(c => c.PhoneE164 == Existing.PhoneE164 && c.Id != id).ToListAsync();
        MessageCount = await _db.Messages.CountAsync(m => m.ClientId == id);
        return true;
    }
}
