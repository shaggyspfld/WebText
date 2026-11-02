using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using WebText.Web.Auth;
using WebText.Web.Data;
using WebText.Web.Services;

namespace WebText.Web.Pages.Account;

/// <summary>
/// Local sign-in, in steps: user name + password → authenticator code (or first-time authenticator setup) →
/// choose a new password if an admin set a temporary one → signed in. Between steps, progress is kept in a short-lived
/// encrypted cookie; nobody is signed in until every step has passed.
/// </summary>
[EnableRateLimiting(AuthSetup.SignInRateLimit)]
public class LoginModel : PageModel
{
    public enum Step { WindowsMode, Password, Code, Enroll, NewPassword }

    private readonly string PendingCookie;
    private static readonly TimeSpan PendingLifetime = TimeSpan.FromMinutes(10);

    private readonly LocalAccounts _accounts;
    private readonly AppDbContext _db;
    private readonly SettingsService _settings;
    private readonly ITimeLimitedDataProtector _protector;

    public LoginModel(LocalAccounts accounts, AppDbContext db, SettingsService settings, IDataProtectionProvider dp, AppInstance instance)
    {
        PendingCookie = instance.Name + ".SignIn";
        _accounts = accounts; _db = db; _settings = settings;
        _protector = dp.CreateProtector(instance.Purpose("SignIn.Pending.v1")).ToTimeLimitedDataProtector();
    }

    public Step Current { get; private set; } = Step.Password;
    public string? Error { get; private set; }
    public string? Info { get; private set; }
    public string? UserNameShown { get; private set; }
    public string? EnrollUri { get; private set; }
    public string? EnrollKey { get; private set; }
    public int MinPasswordLength => _settings.Current.MinPasswordLength;

    [BindProperty(SupportsGet = true)] public string? ReturnUrl { get; set; }
    [BindProperty] public string? UserName { get; set; }
    [BindProperty] public string? Password { get; set; }
    [BindProperty] public string? Code { get; set; }
    [BindProperty] public string? NewPassword { get; set; }
    [BindProperty] public string? ConfirmPassword { get; set; }

    private sealed class Pending
    {
        public int UserId { get; set; }
        public string Stamp { get; set; } = "";
        public bool CodeOk { get; set; }
        public string? NewSecret { get; set; }   // base32, only while setting up an authenticator
        public string? ReturnUrl { get; set; }
    }

    private string? Ip => LocalAccounts.ClientIp(HttpContext);

    public async Task<IActionResult> OnGetAsync()
    {
        if (!_settings.Current.LocalSignIn) { Current = Step.WindowsMode; return Page(); }
        if (User.IsLocalUser()) return LocalRedirect(SafeReturn(ReturnUrl));
        if (Request.Query.ContainsKey("signedout")) Info = "You've been signed out.";
        if (Request.Query.ContainsKey("expired")) Info = "Your session ended. Please sign in again.";
        if (Request.Query.ContainsKey("restored")) Info = "The backup was restored. Sign in with your account from the backup.";
        var (p, user) = await LoadPendingAsync();
        if (p == null || user == null) { Current = Step.Password; return Page(); }
        return await ShowStepAsync(p, user);
    }

    public async Task<IActionResult> OnPostPasswordAsync()
    {
        if (!_settings.Current.LocalSignIn) return RedirectToPage();
        ClearPending();
        var (result, user) = await _accounts.CheckPasswordAsync(UserName, Password, Ip);
        Password = null;
        switch (result)
        {
            case PasswordCheck.LockedOut:
                Error = "This account is locked after too many failed attempts. Try again later, or ask an administrator to unlock it.";
                return Page();
            case PasswordCheck.Disabled:
                Error = "This account has been disabled. Ask an administrator.";
                return Page();
            case PasswordCheck.Wrong:
                Error = "The user name or password is incorrect.";
                return Page();
        }
        var p = new Pending { UserId = user!.Id, Stamp = user.SecurityStamp, ReturnUrl = SafeReturn(ReturnUrl) };
        return await ShowStepAsync(p, user);
    }

    public async Task<IActionResult> OnPostCodeAsync()
    {
        var (p, user) = await LoadPendingAsync();
        if (p == null || user == null) return Restart("Your sign-in took too long. Please start again.");
        if (!_accounts.HasAuthenticator(user)) return await ShowStepAsync(p, user);

        if (!await _accounts.VerifyCodeAsync(user, Code, Ip))
        {
            if (_accounts.IsLocked(user))
                return Restart("This account is locked after too many failed attempts. Try again later, or ask an administrator to unlock it.");
            Error = "That code isn't right. Enter the current 6-digit code from your authenticator app (each code works once).";
            return await ShowStepAsync(p, user, Step.Code);
        }
        p.CodeOk = true;
        return await ShowStepAsync(p, user);
    }

    public async Task<IActionResult> OnPostEnrollAsync()
    {
        var (p, user) = await LoadPendingAsync();
        if (p == null || user == null) return Restart("Your sign-in took too long. Please start again.");
        if (_accounts.HasAuthenticator(user) || p.NewSecret == null) return await ShowStepAsync(p, user);

        if (!await _accounts.EnrollAsync(user, Totp.Base32Decode(p.NewSecret), Code, Ip, user.UserName))
        {
            if (_accounts.IsLocked(user))
                return Restart("This account is locked after too many failed attempts. Ask an administrator to unlock it.");
            Error = "That code isn't right. Check the app shows a code for this account, and that your phone's clock is set automatically.";
            return await ShowStepAsync(p, user, Step.Enroll);
        }
        p.NewSecret = null;
        p.CodeOk = true;
        p.Stamp = user.SecurityStamp;
        return await ShowStepAsync(p, user);
    }

    public async Task<IActionResult> OnPostNewPasswordAsync()
    {
        var (p, user) = await LoadPendingAsync();
        if (p == null || user == null || !p.CodeOk) return Restart("Your sign-in took too long. Please start again.");

        var problem = NewPassword != ConfirmPassword ? "The two passwords don't match." : _accounts.PasswordProblem(NewPassword, user.UserName);
        if (problem == null && LocalAccounts.PasswordMatches(user, NewPassword))
            problem = "Choose a different password from the temporary one.";
        if (problem != null)
        {
            Error = problem;
            return await ShowStepAsync(p, user, Step.NewPassword);
        }

        user.PasswordHash = LocalAccounts.HashPassword(user, NewPassword!);
        user.MustChangePassword = false;
        user.PasswordChangedAtUtc = DateTime.UtcNow;
        user.SecurityStamp = Guid.NewGuid().ToString("N");
        user.UpdatedAtUtc = DateTime.UtcNow;
        user.UpdatedBy = user.UserName;
        await _db.SaveChangesAsync();
        _accounts.Forget(user.Id);
        await _accounts.LogAsync("PasswordChanged", user.UserName, user.UserName, Ip, "Replaced a temporary password at sign-in");
        p.Stamp = user.SecurityStamp;
        return await ShowStepAsync(p, user);
    }

    public IActionResult OnPostCancel()
    {
        ClearPending();
        return RedirectToPage();
    }

    // ---------------------------------------------------------------- steps

    private async Task<IActionResult> ShowStepAsync(Pending p, LocalUser user, Step? force = null)
    {
        UserNameShown = user.UserName;
        ReturnUrl = p.ReturnUrl;

        Step next;
        if (!_accounts.HasAuthenticator(user)) next = Step.Enroll;
        else if (!p.CodeOk) next = Step.Code;
        else if (user.MustChangePassword) next = Step.NewPassword;
        else
        {
            // Every step passed: sign in.
            ClearPending();
            user.LastSignInAtUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            await HttpContext.SignInLocalAsync(_accounts.CreatePrincipal(user));
            await _accounts.LogAsync("SignIn", user.UserName, user.UserName, Ip, null);
            return LocalRedirect(SafeReturn(p.ReturnUrl));
        }
        Current = force ?? next;

        if (Current == Step.Enroll)
        {
            p.NewSecret ??= Totp.Base32Encode(Totp.NewSecret());
            var secret = Totp.Base32Decode(p.NewSecret);
            EnrollUri = Totp.ProvisioningUri(_settings.Current.OfficeName, user.UserName, secret);
            EnrollKey = Totp.FormatKey(secret);
        }
        SavePending(p);
        Code = null;
        return Page();
    }

    private IActionResult Restart(string message)
    {
        ClearPending();
        Current = Step.Password;
        Error = message;
        return Page();
    }

    private async Task<(Pending?, LocalUser?)> LoadPendingAsync()
    {
        if (!Request.Cookies.TryGetValue(PendingCookie, out var raw) || string.IsNullOrEmpty(raw)) return (null, null);
        try
        {
            var p = JsonSerializer.Deserialize<Pending>(_protector.Unprotect(raw));
            if (p == null) return (null, null);
            var user = await _db.LocalUsers.FirstOrDefaultAsync(u => u.Id == p.UserId);
            // A password/authenticator reset or disable since the password step cancels the sign-in.
            if (user == null || !user.IsActive || user.SecurityStamp != p.Stamp || _accounts.IsLocked(user)) return (null, null);
            return (p, user);
        }
        catch (Exception) { return (null, null); }   // expired or tampered
    }

    private void SavePending(Pending p) =>
        Response.Cookies.Append(PendingCookie, _protector.Protect(JsonSerializer.Serialize(p), PendingLifetime), new CookieOptions
        {
            HttpOnly = true,
            Secure = Request.IsHttps,
            SameSite = SameSiteMode.Strict,
            Path = "/Account",
            MaxAge = PendingLifetime,
            IsEssential = true
        });

    private void ClearPending() => Response.Cookies.Delete(PendingCookie, new CookieOptions { Path = "/Account" });

    private string SafeReturn(string? url) =>
        !string.IsNullOrEmpty(url) && Url.IsLocalUrl(url) && !url.StartsWith("/Account", StringComparison.OrdinalIgnoreCase) ? url : "/";
}
