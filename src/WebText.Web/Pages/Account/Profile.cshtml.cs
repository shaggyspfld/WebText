using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using WebText.Web.Auth;
using WebText.Web.Data;
using WebText.Web.Services;

namespace WebText.Web.Pages.Account;

/// <summary>
/// A local user's own account. Every change needs a current code from their authenticator app; changing the password
/// or the authenticator also needs the current password.
/// </summary>
public class ProfileModel : PageModel
{
    private readonly LocalAccounts _accounts;
    private readonly AppDbContext _db;
    private readonly SettingsService _settings;
    private readonly ITimeLimitedDataProtector _protector;

    public ProfileModel(LocalAccounts accounts, AppDbContext db, SettingsService settings, IDataProtectionProvider dp, AppInstance instance)
    {
        _accounts = accounts; _db = db; _settings = settings;
        _protector = dp.CreateProtector(instance.Purpose("Profile.NewAuthenticator.v1")).ToTimeLimitedDataProtector();
    }

    public LocalUser? Me { get; private set; }
    public List<string> MyGroups { get; private set; } = new();
    public List<SecurityEvent> Recent { get; private set; } = new();
    public Dictionary<string, string> Errors { get; } = new();
    public int MinPasswordLength => _settings.Current.MinPasswordLength;

    // new-authenticator step 2
    public string? NewAuthToken { get; private set; }
    public string? NewAuthUri { get; private set; }
    public string? NewAuthKey { get; private set; }

    [BindProperty] public string? DisplayName { get; set; }
    [BindProperty] public string? Email { get; set; }
    [BindProperty] public string? CurrentPassword { get; set; }
    [BindProperty] public string? NewPassword { get; set; }
    [BindProperty] public string? ConfirmPassword { get; set; }
    [BindProperty] public string? Code { get; set; }
    [BindProperty] public string? Token { get; set; }

    private string? Ip => LocalAccounts.ClientIp(HttpContext);

    public async Task OnGetAsync() => await LoadAsync();

    public async Task<IActionResult> OnPostDetailsAsync()
    {
        if (await LoadAsync() is not { } me) return Page();
        if (!await CodeOkAsync(me, "details")) return Page();

        var name = string.IsNullOrWhiteSpace(DisplayName) ? null : DisplayName.Trim();
        var email = string.IsNullOrWhiteSpace(Email) ? null : Email.Trim();
        if (name?.Length > 200) { Errors["details"] = "The name is too long."; return Page(); }
        if (email != null && (email.Length > 256 || !email.Contains('@'))) { Errors["details"] = "Enter a valid email address, or leave it blank."; return Page(); }

        var changes = new List<string>();
        if (name != me.DisplayName) changes.Add($"Name: \"{me.DisplayName}\" → \"{name}\"");
        if (email != me.Email) changes.Add($"Email: \"{me.Email}\" → \"{email}\"");
        me.DisplayName = name; me.Email = email;
        me.UpdatedAtUtc = DateTime.UtcNow; me.UpdatedBy = me.UserName;
        await _db.SaveChangesAsync();
        if (changes.Count > 0) await _accounts.LogAsync("ProfileChanged", me.UserName, me.UserName, Ip, string.Join("\n", changes));
        await HttpContext.SignInLocalAsync(_accounts.CreatePrincipal(me));   // refresh the name shown in the top bar
        TempData["Flash"] = changes.Count == 0 ? "No changes." : "Saved your details.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostPasswordAsync()
    {
        if (await LoadAsync() is not { } me) return Page();
        if (!await _accounts.ConfirmPasswordAsync(me, CurrentPassword, Ip)) { Errors["password"] = "Your current password isn't right."; return Page(); }
        if (!await CodeOkAsync(me, "password")) return Page();
        var problem = NewPassword != ConfirmPassword ? "The two new passwords don't match."
            : LocalAccounts.PasswordMatches(me, NewPassword) ? "The new password is the same as the current one."
            : _accounts.PasswordProblem(NewPassword, me.UserName);
        if (problem != null) { Errors["password"] = problem; return Page(); }

        me.PasswordHash = LocalAccounts.HashPassword(me, NewPassword!);
        me.PasswordChangedAtUtc = DateTime.UtcNow;
        me.MustChangePassword = false;
        me.SecurityStamp = Guid.NewGuid().ToString("N");   // signs out your other browsers/devices
        me.UpdatedAtUtc = DateTime.UtcNow; me.UpdatedBy = me.UserName;
        await _db.SaveChangesAsync();
        _accounts.Forget(me.Id);
        await _accounts.LogAsync("PasswordChanged", me.UserName, me.UserName, Ip, "Changed on the profile page");
        await HttpContext.SignInLocalAsync(_accounts.CreatePrincipal(me));
        TempData["Flash"] = "Password changed. Any other browsers signed in as you have been signed out.";
        return RedirectToPage();
    }

    /// <summary>New phone: step 1 proves who you are (password + current code), then shows a new QR code.</summary>
    public async Task<IActionResult> OnPostStartAuthenticatorAsync()
    {
        if (await LoadAsync() is not { } me) return Page();
        if (!await _accounts.ConfirmPasswordAsync(me, CurrentPassword, Ip)) { Errors["auth"] = "Your current password isn't right."; return Page(); }
        if (!await CodeOkAsync(me, "auth")) return Page();
        ShowNewAuthenticator(me, Totp.NewSecret());
        return Page();
    }

    /// <summary>Step 2: a code from the NEW app confirms it's set up, then it replaces the old one.</summary>
    public async Task<IActionResult> OnPostConfirmAuthenticatorAsync()
    {
        if (await LoadAsync() is not { } me) return Page();
        byte[] secret;
        try
        {
            var parts = _protector.Unprotect(Token ?? "").Split('|');
            if (parts.Length != 3 || parts[0] != me.Id.ToString() || parts[1] != me.SecurityStamp) throw new InvalidOperationException();
            secret = Totp.Base32Decode(parts[2]);
        }
        catch (Exception)
        {
            Errors["auth"] = "That took too long. Start again.";
            return Page();
        }
        if (!await _accounts.EnrollAsync(me, secret, Code, Ip, me.UserName))
        {
            Errors["auth"] = "That code isn't right. Enter the code the NEW entry in your app shows.";
            ShowNewAuthenticator(me, secret);
            return Page();
        }
        await HttpContext.SignInLocalAsync(_accounts.CreatePrincipal(me));
        TempData["Flash"] = "Your new authenticator is set up; the old one no longer works. Other browsers have been signed out.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostSignOutOthersAsync()
    {
        if (await LoadAsync() is not { } me) return Page();
        if (!await CodeOkAsync(me, "sessions")) return Page();
        me.SecurityStamp = Guid.NewGuid().ToString("N");
        await _db.SaveChangesAsync();
        _accounts.Forget(me.Id);
        await _accounts.LogAsync("SignedOutEverywhere", me.UserName, me.UserName, Ip, null);
        await HttpContext.SignInLocalAsync(_accounts.CreatePrincipal(me));
        TempData["Flash"] = "Signed out of every other browser and device.";
        return RedirectToPage();
    }

    // ---------------------------------------------------------------- helpers

    private void ShowNewAuthenticator(LocalUser me, byte[] secret)
    {
        var b32 = Totp.Base32Encode(secret);
        NewAuthToken = _protector.Protect($"{me.Id}|{me.SecurityStamp}|{b32}", TimeSpan.FromMinutes(10));
        NewAuthUri = Totp.ProvisioningUri(_settings.Current.OfficeName, me.UserName, secret);
        NewAuthKey = Totp.FormatKey(secret);
    }

    private async Task<bool> CodeOkAsync(LocalUser me, string errorKey)
    {
        if (await _accounts.VerifyCodeAsync(me, Code, Ip)) return true;
        Errors[errorKey] = _accounts.IsLocked(me)
            ? "Too many failed attempts: your account is now locked. Ask an administrator to unlock it."
            : "That authenticator code isn't right (each code works once; wait for the next one if you just used it).";
        return false;
    }

    private async Task<LocalUser?> LoadAsync()
    {
        Me = await _accounts.CurrentAsync(User);
        if (Me == null) return null;
        if (HttpMethods.IsGet(Request.Method) || Request.Query["handler"] != "Details")
        {
            DisplayName = Me.DisplayName;
            Email = Me.Email;
        }
        MyGroups = await _db.LocalGroupMembers.Where(m => m.UserId == Me.Id).Select(m => m.Group.Name).OrderBy(n => n).ToListAsync();
        Recent = await _db.SecurityEvents.AsNoTracking().Where(e => e.UserName == Me.UserName)
            .OrderByDescending(e => e.AtUtc).Take(15).ToListAsync();
        return Me;
    }
}
