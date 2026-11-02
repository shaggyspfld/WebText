using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using WebText.Web.Api;
using WebText.Web.Auth;
using WebText.Web.Data;
using WebText.Web.Services;

namespace WebText.Web.Pages.Admin;

/// <summary>
/// Local users and groups (used when Settings → Sign-in is "Local"), plus the security log. Admin-only (folder
/// convention). An admin who is signed in locally must enter an authenticator code before making changes; after that
/// they aren't asked again for a few minutes.
/// </summary>
public class UsersModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly LocalAccounts _accounts;
    private readonly SettingsService _settings;
    private readonly GroupMembership _groups;
    private readonly IMemoryCache _cache;
    private readonly TimeDisplay _time;

    public UsersModel(AppDbContext db, LocalAccounts accounts, SettingsService settings, GroupMembership groups,
        IMemoryCache cache, TimeDisplay time)
    {
        _db = db; _accounts = accounts; _settings = settings; _groups = groups; _cache = cache; _time = time;
    }

    public sealed record UserRow(LocalUser User, List<string> Groups, bool Locked);

    public List<UserRow> Users { get; private set; } = new();
    public List<(LocalGroup Group, int Members)> Groups { get; private set; } = new();
    public List<SecurityEvent> Events { get; private set; } = new();
    public bool LocalSignIn => _settings.Current.LocalSignIn;
    public string UserGroupName => GroupMembership.LocalGroupName(_settings.Current.UserGroup);
    public string AdminGroupName => GroupMembership.LocalGroupName(_settings.Current.AdminGroup);
    public bool NeedCode { get; private set; }
    public string? Error { get; private set; }
    public LocalUser? Editing { get; private set; }
    public HashSet<int> EditingGroupIds { get; private set; } = new();

    [BindProperty(SupportsGet = true)] public string? EventUser { get; set; }
    [BindProperty] public string? AdminCode { get; set; }
    [BindProperty] public string? NewUserName { get; set; }
    [BindProperty] public string? DisplayName { get; set; }
    [BindProperty] public string? Email { get; set; }
    [BindProperty] public List<int> GroupIds { get; set; } = new();
    [BindProperty] public bool IsActive { get; set; } = true;
    [BindProperty] public string? GroupName { get; set; }
    [BindProperty] public string? GroupDescription { get; set; }

    private string Me => User.UserName();
    private string? Ip => LocalAccounts.ClientIp(HttpContext);

    public async Task OnGetAsync(int? edit) => await LoadAsync(edit);

    // ---------------------------------------------------------------- users

    public async Task<IActionResult> OnPostCreateUserAsync()
    {
        if (!await StepUpAsync()) return await ReloadAsync();
        var name = LocalAccounts.NormalizeUserName(NewUserName);
        if (name == null) return await FailAsync("User names are 2-64 characters: letters, numbers and . _ - @ (e.g. jsmith).");
        if (await _db.LocalUsers.AnyAsync(u => u.UserName == name)) return await FailAsync($"There's already a user called {name}.");
        var (dn, em, err) = CleanDetails();
        if (err != null) return await FailAsync(err);

        var temp = LocalAccounts.TemporaryPassword();
        var u = new LocalUser { UserName = name, DisplayName = dn, Email = em, MustChangePassword = true, CreatedBy = Me };
        u.PasswordHash = LocalAccounts.HashPassword(u, temp);
        var groups = await _db.LocalGroups.Where(g => GroupIds.Contains(g.Id)).ToListAsync();
        foreach (var g in groups) u.Groups.Add(new LocalGroupMember { Group = g });
        _db.LocalUsers.Add(u);
        await _db.SaveChangesAsync();
        await _accounts.LogAsync("UserCreated", name, Me, Ip,
            $"Name: {dn ?? "(blank)"}\nEmail: {em ?? "(blank)"}\nGroups: {Join(groups.Select(g => g.Name))}");
        TempData["TempPassword"] = $"{name}|{temp}";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostSaveUserAsync(int id)
    {
        if (!await StepUpAsync()) return await ReloadAsync(id);
        var u = await _db.LocalUsers.Include(x => x.Groups).ThenInclude(m => m.Group).FirstOrDefaultAsync(x => x.Id == id);
        if (u == null) return RedirectToPage();
        var (dn, em, err) = CleanDetails();
        if (err != null) return await FailAsync(err, id);

        var newGroups = await _db.LocalGroups.Where(g => GroupIds.Contains(g.Id)).ToListAsync();
        var oldNames = u.Groups.Select(m => m.Group.Name).OrderBy(n => n).ToList();
        var newNames = newGroups.Select(g => g.Name).OrderBy(n => n).ToList();
        var isMe = User.LocalUserId() == u.Id;

        if (isMe && !IsActive) return await FailAsync("You can't disable your own account.", id);
        if (isMe && !newNames.Contains(AdminGroupName, StringComparer.OrdinalIgnoreCase))
            return await FailAsync($"You can't remove yourself from {AdminGroupName}; you'd lose access to this page.", id);
        if (await WouldLeaveNoAdminAsync(u.Id, IsActive && newNames.Contains(AdminGroupName, StringComparer.OrdinalIgnoreCase)))
            return await FailAsync($"That would leave no active member of {AdminGroupName}, and nobody could sign in to administer the app.", id);

        var changes = new List<string>();
        if (dn != u.DisplayName) changes.Add($"Name: \"{u.DisplayName}\" → \"{dn}\"");
        if (em != u.Email) changes.Add($"Email: \"{u.Email}\" → \"{em}\"");
        if (IsActive != u.IsActive) changes.Add(IsActive ? "Enabled the account" : "Disabled the account");
        if (!oldNames.SequenceEqual(newNames, StringComparer.OrdinalIgnoreCase))
            changes.Add($"Groups: {Join(oldNames)} → {Join(newNames)}");
        if (changes.Count == 0) { TempData["Flash"] = "No changes."; return RedirectToPage(); }

        u.DisplayName = dn; u.Email = em;
        if (IsActive != u.IsActive)
        {
            u.IsActive = IsActive;
            u.SecurityStamp = Guid.NewGuid().ToString("N");   // disabling ends their sessions at once
        }
        _db.LocalGroupMembers.RemoveRange(u.Groups.Where(m => !GroupIds.Contains(m.GroupId)));
        foreach (var g in newGroups.Where(g => u.Groups.All(m => m.GroupId != g.Id)))
            u.Groups.Add(new LocalGroupMember { GroupId = g.Id, UserId = u.Id });
        u.UpdatedAtUtc = DateTime.UtcNow; u.UpdatedBy = Me;
        await _db.SaveChangesAsync();
        _accounts.Forget(u.Id);
        await _accounts.LogAsync("UserChanged", u.UserName, Me, Ip, string.Join("\n", changes));
        TempData["Flash"] = $"Saved {u.UserName}.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostResetPasswordAsync(int id)
    {
        if (!await StepUpAsync()) return await ReloadAsync();
        var u = await _db.LocalUsers.FindAsync(id);
        if (u == null) return RedirectToPage();
        if (User.LocalUserId() == u.Id) return await FailAsync("Change your own password on the My account page.");
        var temp = LocalAccounts.TemporaryPassword();
        u.PasswordHash = LocalAccounts.HashPassword(u, temp);
        u.MustChangePassword = true;
        u.SecurityStamp = Guid.NewGuid().ToString("N");
        u.FailedAttempts = 0; u.LockedUntilUtc = null;
        u.UpdatedAtUtc = DateTime.UtcNow; u.UpdatedBy = Me;
        await _db.SaveChangesAsync();
        _accounts.Forget(u.Id);
        await _accounts.LogAsync("PasswordReset", u.UserName, Me, Ip, "Temporary password issued; they must choose a new one at next sign-in");
        TempData["TempPassword"] = $"{u.UserName}|{temp}";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostResetAuthenticatorAsync(int id)
    {
        if (!await StepUpAsync()) return await ReloadAsync();
        var u = await _db.LocalUsers.FindAsync(id);
        if (u == null) return RedirectToPage();
        if (User.LocalUserId() == u.Id) return await FailAsync("Move your own authenticator on the My account page.");
        u.TotpSecretProtected = null;
        u.TotpLastStep = 0;
        u.SecurityStamp = Guid.NewGuid().ToString("N");
        u.UpdatedAtUtc = DateTime.UtcNow; u.UpdatedBy = Me;
        await _db.SaveChangesAsync();
        _accounts.Forget(u.Id);
        await _accounts.LogAsync("AuthenticatorReset", u.UserName, Me, Ip, "They'll set up a new authenticator at next sign-in");
        TempData["Flash"] = $"{u.UserName}'s authenticator was removed. They'll be asked to set up a new one the next time they sign in with their password.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostUnlockAsync(int id)
    {
        if (!await StepUpAsync()) return await ReloadAsync();
        var u = await _db.LocalUsers.FindAsync(id);
        if (u == null) return RedirectToPage();
        u.FailedAttempts = 0; u.LockedUntilUtc = null;
        await _db.SaveChangesAsync();
        await _accounts.LogAsync("Unlocked", u.UserName, Me, Ip, null);
        TempData["Flash"] = $"Unlocked {u.UserName}.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteUserAsync(int id)
    {
        if (!await StepUpAsync()) return await ReloadAsync();
        var u = await _db.LocalUsers.Include(x => x.Groups).ThenInclude(m => m.Group).FirstOrDefaultAsync(x => x.Id == id);
        if (u == null) return RedirectToPage();
        if (User.LocalUserId() == u.Id) return await FailAsync("You can't delete your own account.");
        if (await WouldLeaveNoAdminAsync(u.Id, false))
            return await FailAsync($"That would leave no active member of {AdminGroupName}.");
        _db.LocalUsers.Remove(u);   // texts they sent keep their user name in the history
        await _db.SaveChangesAsync();
        _accounts.Forget(u.Id);
        await _accounts.LogAsync("UserDeleted", u.UserName, Me, Ip,
            $"Name: {u.DisplayName ?? "(blank)"}\nGroups: {Join(u.Groups.Select(m => m.Group.Name))}");
        TempData["Flash"] = $"Deleted {u.UserName}. Texts they sent stay in the history under their user name.";
        return RedirectToPage();
    }

    // ---------------------------------------------------------------- groups

    public async Task<IActionResult> OnPostCreateGroupAsync()
    {
        if (!await StepUpAsync()) return await ReloadAsync();
        var name = (GroupName ?? "").Trim();
        if (name.Length is < 2 or > 128 || name.IndexOfAny(new[] { '\\', '/', '"', '<', '>' }) >= 0)
            return await FailAsync("Group names are 2-128 characters, without \\ / \" < >.");
        if (await _db.LocalGroups.AnyAsync(g => g.Name == name)) return await FailAsync($"There's already a group called {name}.");
        var desc = string.IsNullOrWhiteSpace(GroupDescription) ? null : GroupDescription.Trim()[..Math.Min(500, GroupDescription.Trim().Length)];
        _db.LocalGroups.Add(new LocalGroup { Name = name, Description = desc, CreatedBy = Me });
        await _db.SaveChangesAsync();
        await _accounts.LogAsync("GroupCreated", null, Me, Ip, $"Group: {name}" + (desc == null ? "" : $"\nDescription: {desc}"));
        TempData["Flash"] = $"Added the group {name}.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteGroupAsync(int id)
    {
        if (!await StepUpAsync()) return await ReloadAsync();
        var g = await _db.LocalGroups.Include(x => x.Members).ThenInclude(m => m.User).FirstOrDefaultAsync(x => x.Id == id);
        if (g == null) return RedirectToPage();
        if (IsAccessGroup(g.Name))
            return await FailAsync($"{g.Name} is named on the Settings page as an access group, so it can't be deleted.");
        _db.LocalGroups.Remove(g);
        await _db.SaveChangesAsync();
        foreach (var m in g.Members) _accounts.Forget(m.UserId);
        await _accounts.LogAsync("GroupDeleted", null, Me, Ip, $"Group: {g.Name}\nMembers: {Join(g.Members.Select(m => m.User.UserName))}");
        TempData["Flash"] = $"Deleted the group {g.Name}.";
        return RedirectToPage();
    }

    // ---------------------------------------------------------------- security log export

    public async Task<IActionResult> OnGetEventsCsvAsync()
    {
        var q = _db.SecurityEvents.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(EventUser)) q = q.Where(e => e.UserName == EventUser.Trim() || e.By == EventUser.Trim());
        var rows = await q.OrderByDescending(e => e.AtUtc).ThenByDescending(e => e.Id).Take(100_000).ToListAsync();
        var sb = new System.Text.StringBuilder("When,Event,Account,By,IP address,Details\r\n");
        static string Csv(string? v)
        {
            v ??= "";
            if (v.Length > 0 && "=+-@\t\r".Contains(v[0])) v = "'" + v;
            return "\"" + v.Replace("\"", "\"\"") + "\"";
        }
        foreach (var e in rows)
            sb.Append(Csv(_time.Local(e.AtUtc, "yyyy-MM-dd HH:mm:ss"))).Append(',').Append(Csv(EventLabel(e.Event))).Append(',')
              .Append(Csv(e.UserName)).Append(',').Append(Csv(e.By)).Append(',').Append(Csv(e.IpAddress)).Append(',')
              .Append(Csv(e.Details)).Append("\r\n");
        return File(System.Text.Encoding.UTF8.GetPreamble().Concat(System.Text.Encoding.UTF8.GetBytes(sb.ToString())).ToArray(),
            "text/csv", $"security-log-{DateTime.Now:yyyyMMdd}.csv");
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// Admins signed in locally confirm with an authenticator code (remembered for a few minutes).
    /// Windows admins are already verified by Windows.
    /// </summary>
    private async Task<bool> StepUpAsync()
    {
        var me = await _accounts.CurrentAsync(User);
        if (me == null || _accounts.RecentlyVerified(me)) return true;
        if (await _accounts.VerifyCodeAsync(me, AdminCode, Ip))
        {
            _accounts.MarkRecentlyVerified(me);
            return true;
        }
        Error = _accounts.IsLocked(me)
            ? "Too many failed attempts: your account is now locked."
            : "Enter the current code from your authenticator app to make changes.";
        return false;
    }

    /// <summary>True if, after this change, no active user would be in the admin group (only matters for Local sign-in).</summary>
    private async Task<bool> WouldLeaveNoAdminAsync(int changingUserId, bool stillAdminAfter)
    {
        if (!LocalSignIn || stillAdminAfter) return false;
        var admin = AdminGroupName;
        return !await _db.LocalGroupMembers.AnyAsync(m => m.Group.Name == admin && m.User.IsActive && m.UserId != changingUserId);
    }

    private bool IsAccessGroup(string name) =>
        string.Equals(name, UserGroupName, StringComparison.OrdinalIgnoreCase) || string.Equals(name, AdminGroupName, StringComparison.OrdinalIgnoreCase);

    private (string? Name, string? Email, string? Error) CleanDetails()
    {
        var dn = string.IsNullOrWhiteSpace(DisplayName) ? null : DisplayName.Trim();
        var em = string.IsNullOrWhiteSpace(Email) ? null : Email.Trim();
        if (dn?.Length > 200) return (null, null, "The name is too long.");
        if (em != null && (em.Length > 256 || !em.Contains('@'))) return (null, null, "Enter a valid email address, or leave it blank.");
        return (dn, em, null);
    }

    private static string Join(IEnumerable<string> names)
    {
        var list = names.OrderBy(n => n).ToList();
        return list.Count == 0 ? "(none)" : string.Join(", ", list);
    }

    private async Task<IActionResult> FailAsync(string message, int? edit = null)
    {
        Error = message;
        return await ReloadAsync(edit);
    }

    private async Task<IActionResult> ReloadAsync(int? edit = null)
    {
        await LoadAsync(edit, keepForm: true);
        return Page();
    }

    private async Task LoadAsync(int? edit = null, bool keepForm = false)
    {
        await SeedData.EnsureLocalAccessGroupsAsync(_db, _settings.Current, "system");

        var users = await _db.LocalUsers.AsNoTracking().Include(u => u.Groups).ThenInclude(m => m.Group)
            .OrderBy(u => u.UserName).ToListAsync();
        Users = users.Select(u => new UserRow(u, u.Groups.Select(m => m.Group.Name).OrderBy(n => n).ToList(), _accounts.IsLocked(u))).ToList();

        var groups = await _db.LocalGroups.AsNoTracking().OrderBy(g => g.Name)
            .Select(g => new { g, n = g.Members.Count }).ToListAsync();
        Groups = groups.Select(x => (x.g, x.n)).ToList();

        var q = _db.SecurityEvents.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(EventUser)) q = q.Where(e => e.UserName == EventUser.Trim() || e.By == EventUser.Trim());
        Events = await q.OrderByDescending(e => e.AtUtc).ThenByDescending(e => e.Id).Take(100).ToListAsync();

        var me = await _accounts.CurrentAsync(User);
        NeedCode = me != null && !_accounts.RecentlyVerified(me);

        if (edit is { } id && users.FirstOrDefault(u => u.Id == id) is { } e)
        {
            Editing = e;
            if (!keepForm)
            {
                DisplayName = e.DisplayName; Email = e.Email; IsActive = e.IsActive;
                GroupIds = e.Groups.Select(m => m.GroupId).ToList();
            }
        }
        else if (!keepForm)
        {
            GroupIds = Groups.Where(g => string.Equals(g.Group.Name, UserGroupName, StringComparison.OrdinalIgnoreCase))
                .Select(g => g.Group.Id).ToList();   // new users: user group ticked by default
        }
        EditingGroupIds = GroupIds.ToHashSet();
    }

    public static string EventLabel(string e) => e switch
    {
        "SignIn" => "Signed in",
        "SignOut" => "Signed out",
        "SignInFailed" => "Failed sign-in",
        "LockedOut" => "Locked out",
        "Unlocked" => "Unlocked",
        "PasswordChanged" => "Changed password",
        "PasswordReset" => "Password reset by admin",
        "AuthenticatorSetUp" => "Set up authenticator",
        "AuthenticatorChanged" => "Moved authenticator",
        "AuthenticatorReset" => "Authenticator reset by admin",
        "SignedOutEverywhere" => "Signed out other sessions",
        "ProfileChanged" => "Changed own details",
        "UserCreated" => "User added",
        "UserChanged" => "User changed",
        "UserDeleted" => "User deleted",
        "GroupCreated" => "Group added",
        "GroupDeleted" => "Group deleted",
        "SignInModeChanged" => "Sign-in method changed",
        "BackupCreated" => "Backup downloaded",
        "BackupRestored" => "Backup restored",
        _ => e
    };
}
