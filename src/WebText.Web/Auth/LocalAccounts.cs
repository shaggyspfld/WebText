using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using WebText.Web.Api;
using WebText.Web.Data;
using WebText.Web.Services;

namespace WebText.Web.Auth;

public static class AuthSchemes
{
    /// <summary>Default scheme: forwards to Windows or Local depending on Settings → Sign-in.</summary>
    public const string Selector = "WebText";
    /// <summary>Cookie for app-hosted (local) users.</summary>
    public const string Local = "WebText.Local";
}

public static class LocalClaims
{
    public const string UserId = "webtext:local-user-id";
    public const string Stamp = "webtext:stamp";
    public const string SignedInAt = "webtext:signed-in-at";
    public const string DisplayName = "webtext:display-name";

    public static int? LocalUserId(this ClaimsPrincipal user) =>
        user.Identity?.AuthenticationType == AuthSchemes.Local && int.TryParse(user.FindFirstValue(UserId), out var id) ? id : null;

    public static bool IsLocalUser(this ClaimsPrincipal user) => user.LocalUserId() != null;
}

public enum PasswordCheck { Ok, Wrong, LockedOut, Disabled }

/// <summary>
/// Everything about app-hosted accounts: passwords, authenticator codes, lockout, the security log and building the
/// signed-in identity. Used by the sign-in, profile and Users pages.
/// </summary>
public class LocalAccounts
{
    /// <summary>After a code is entered for an admin task, further admin tasks don't ask again for this long.</summary>
    public static readonly TimeSpan RecentCodeWindow = TimeSpan.FromMinutes(5);

    private static readonly PasswordHasher<LocalUser> Hasher = new();
    // Verified against when the user name doesn't exist, so a wrong name takes as long as a wrong password.
    private static readonly string DummyHash = Hasher.HashPassword(new LocalUser(), Guid.NewGuid().ToString());
    private static readonly Regex UserNamePattern = new(@"^[a-z0-9][a-z0-9._@-]{1,63}$");
    private static readonly HashSet<string> CommonPasswords = new(StringComparer.OrdinalIgnoreCase)
    {
        "password", "password1", "password123", "passw0rd", "123456789012", "qwertyuiop", "letmein", "welcome1",
        "iloveyou", "admin123", "changeme", "taxseason", "summer2026", "winter2026", "spring2026", "fall2026"
    };

    private readonly AppDbContext _db;
    private readonly SettingsService _settings;
    private readonly IMemoryCache _cache;
    private readonly IDataProtector _totpProtector;
    private readonly ILogger<LocalAccounts> _log;

    public LocalAccounts(AppDbContext db, SettingsService settings, IMemoryCache cache, IDataProtectionProvider dp,
        ILogger<LocalAccounts> log, AppInstance instance)
    {
        _db = db; _settings = settings; _cache = cache; _log = log;
        _totpProtector = dp.CreateProtector(instance.Purpose("LocalUsers.Totp.v1"));
    }

    // ---------------------------------------------------------------- names and passwords

    /// <summary>Lower-case user name, or null if it isn't allowed (2-64 of a-z 0-9 . _ - @, starting with a letter/digit).</summary>
    public static string? NormalizeUserName(string? name)
    {
        var n = (name ?? "").Trim().ToLowerInvariant();
        return UserNamePattern.IsMatch(n) ? n : null;
    }

    /// <summary>Null if the password is acceptable, otherwise the reason it isn't.</summary>
    public string? PasswordProblem(string? password, string? userName)
    {
        var min = _settings.Current.MinPasswordLength;
        if (string.IsNullOrEmpty(password) || password.Length < min) return $"Use at least {min} characters. A short sentence works well.";
        if (password.Length > 256) return "Use 256 characters or fewer.";
        if (password.Distinct().Count() < 5) return "Use more different characters.";
        if (CommonPasswords.Contains(password)) return "That password is too common.";
        if (!string.IsNullOrEmpty(userName) && password.Contains(userName, StringComparison.OrdinalIgnoreCase))
            return "Don't include your user name in the password.";
        return null;
    }

    public static string HashPassword(LocalUser u, string password) => Hasher.HashPassword(u, password);

    public static bool PasswordMatches(LocalUser u, string? password) =>
        Hasher.VerifyHashedPassword(u, u.PasswordHash, password ?? "") != PasswordVerificationResult.Failed;

    /// <summary>A random temporary password, easy to read out (no 0/O, 1/l/I).</summary>
    public static string TemporaryPassword()
    {
        const string chars = "abcdefghjkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var groups = Enumerable.Range(0, 4).Select(_ =>
            new string(Enumerable.Range(0, 4).Select(_ => chars[RandomNumberGenerator.GetInt32(chars.Length)]).ToArray()));
        return string.Join("-", groups);
    }

    /// <summary>Checks a user name + password at sign-in, applying lockout. Logs the outcome.</summary>
    public async Task<(PasswordCheck Result, LocalUser? User)> CheckPasswordAsync(string? userName, string? password, string? ip)
    {
        var name = NormalizeUserName(userName);
        var user = name == null ? null : await _db.LocalUsers.FirstOrDefaultAsync(u => u.UserName == name);
        if (user == null)
        {
            Hasher.VerifyHashedPassword(new LocalUser(), DummyHash, password ?? "");
            await LogAsync("SignInFailed", userName?.Trim(), null, ip, "Unknown user name");
            return (PasswordCheck.Wrong, null);
        }
        if (IsLocked(user))
        {
            await LogAsync("SignInFailed", user.UserName, null, ip, "Account is locked");
            return (PasswordCheck.LockedOut, user);
        }

        var result = Hasher.VerifyHashedPassword(user, user.PasswordHash, password ?? "");
        if (result == PasswordVerificationResult.Failed)
        {
            var locked = await RecordFailureAsync(user, ip, "Wrong password");
            return (locked ? PasswordCheck.LockedOut : PasswordCheck.Wrong, user);
        }
        if (!user.IsActive)
        {
            await LogAsync("SignInFailed", user.UserName, null, ip, "Account is disabled");
            return (PasswordCheck.Disabled, user);
        }
        if (result == PasswordVerificationResult.SuccessRehashNeeded)
        {
            user.PasswordHash = Hasher.HashPassword(user, password!);
            await _db.SaveChangesAsync();
        }
        return (PasswordCheck.Ok, user);
    }

    /// <summary>Password re-check for profile changes (no lockout reset; failures count toward lockout).</summary>
    public async Task<bool> ConfirmPasswordAsync(LocalUser user, string? password, string? ip)
    {
        if (IsLocked(user)) return false;
        if (Hasher.VerifyHashedPassword(user, user.PasswordHash, password ?? "") != PasswordVerificationResult.Failed) return true;
        await RecordFailureAsync(user, ip, "Wrong current password on the profile page");
        return false;
    }

    public bool IsLocked(LocalUser u) => u.LockedUntilUtc is { } until && until > DateTime.UtcNow;

    /// <summary>Counts a wrong password or code. Returns true if the account is now locked.</summary>
    private async Task<bool> RecordFailureAsync(LocalUser user, string? ip, string what)
    {
        var s = _settings.Current;
        user.FailedAttempts++;
        var locked = user.FailedAttempts >= s.LockoutThreshold;
        if (locked)
        {
            user.LockedUntilUtc = DateTime.UtcNow + s.LockoutLength;
            user.FailedAttempts = 0;
        }
        await _db.SaveChangesAsync();
        await LogAsync("SignInFailed", user.UserName, null, ip, what);
        if (locked)
        {
            await LogAsync("LockedOut", user.UserName, null, ip,
                $"{s.LockoutThreshold} failed attempts; locked for {s.LockoutLength.TotalMinutes:0} minutes");
            _log.LogWarning("Local account {User} locked out after failed sign-in attempts from {Ip}", user.UserName, ip);
        }
        return locked;
    }

    private async Task ClearFailuresAsync(LocalUser user)
    {
        if (user.FailedAttempts == 0 && user.LockedUntilUtc == null) return;
        user.FailedAttempts = 0;
        user.LockedUntilUtc = null;
        await _db.SaveChangesAsync();
    }

    // ---------------------------------------------------------------- authenticator codes

    public bool HasAuthenticator(LocalUser u) => !string.IsNullOrEmpty(u.TotpSecretProtected);

    public string ProtectSecret(byte[] secret) => _totpProtector.Protect(Convert.ToBase64String(secret));

    /// <summary>Backups: the user's authenticator secret in the clear (null if none, or not readable on this server).</summary>
    public byte[]? ReadSecretForBackup(LocalUser u) => UnprotectSecret(u.TotpSecretProtected);

    private byte[]? UnprotectSecret(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return null;
        try { return Convert.FromBase64String(_totpProtector.Unprotect(stored)); }
        catch (CryptographicException ex)
        {
            // e.g. the database was moved to a server that can't read the key ring. An admin must reset the authenticator.
            _log.LogError(ex, "Can't decrypt the authenticator secret for a local user");
            return null;
        }
    }

    /// <summary>
    /// Checks a 6-digit code from the user's authenticator app. Each code works once. Wrong codes count toward
    /// lockout. Logs failures (the caller logs what the code was for).
    /// </summary>
    public async Task<bool> VerifyCodeAsync(LocalUser user, string? code, string? ip)
    {
        if (IsLocked(user) || !user.IsActive) return false;
        var secret = UnprotectSecret(user.TotpSecretProtected);
        if (secret == null) return false;
        var step = Totp.Verify(secret, code, user.TotpLastStep, DateTimeOffset.UtcNow);
        if (step == null)
        {
            await RecordFailureAsync(user, ip, "Wrong or reused authenticator code");
            return false;
        }
        // Claim the step atomically, so two requests can't both use the same code.
        var claimed = await _db.LocalUsers.Where(u => u.Id == user.Id && u.TotpLastStep < step.Value)
            .ExecuteUpdateAsync(x => x.SetProperty(u => u.TotpLastStep, step.Value));
        if (claimed == 0)
        {
            await RecordFailureAsync(user, ip, "Authenticator code already used");
            return false;
        }
        user.TotpLastStep = step.Value;
        await ClearFailuresAsync(user);
        return true;
    }

    /// <summary>Checks a code from a NEW authenticator (during setup) and, if right, saves the secret.</summary>
    public async Task<bool> EnrollAsync(LocalUser user, byte[] newSecret, string? code, string? ip, string by)
    {
        var step = Totp.Verify(newSecret, code, 0, DateTimeOffset.UtcNow);
        if (step == null)
        {
            await RecordFailureAsync(user, ip, "Wrong code while setting up an authenticator");
            return false;
        }
        var replacing = HasAuthenticator(user);
        user.TotpSecretProtected = ProtectSecret(newSecret);
        user.TotpLastStep = step.Value;
        user.FailedAttempts = 0;
        user.LockedUntilUtc = null;
        if (replacing) user.SecurityStamp = Guid.NewGuid().ToString("N");   // sign out other sessions
        user.UpdatedAtUtc = DateTime.UtcNow;
        user.UpdatedBy = by;
        await _db.SaveChangesAsync();
        Forget(user.Id);
        await LogAsync(replacing ? "AuthenticatorChanged" : "AuthenticatorSetUp", user.UserName, by, ip, null);
        return true;
    }

    /// <summary>Admin tasks: has this person entered a code in the last few minutes?</summary>
    public bool RecentlyVerified(LocalUser user) => _cache.TryGetValue(RecentKey(user), out _);

    public void MarkRecentlyVerified(LocalUser user) =>
        _cache.Set(RecentKey(user), true, RecentCodeWindow);

    private static string RecentKey(LocalUser u) => $"local-recent|{u.Id}|{u.SecurityStamp}";

    // ---------------------------------------------------------------- sessions

    public ClaimsPrincipal CreatePrincipal(LocalUser user)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, user.UserName),
            new(LocalClaims.UserId, user.Id.ToString()),
            new(LocalClaims.Stamp, user.SecurityStamp),
            new(LocalClaims.SignedInAt, DateTime.UtcNow.ToString("O")),
        };
        if (!string.IsNullOrWhiteSpace(user.DisplayName)) claims.Add(new(LocalClaims.DisplayName, user.DisplayName));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, AuthSchemes.Local, ClaimTypes.Name, ClaimTypes.Role));
    }

    public async Task<LocalUser?> CurrentAsync(ClaimsPrincipal principal) =>
        principal.LocalUserId() is { } id ? await _db.LocalUsers.FirstOrDefaultAsync(u => u.Id == id) : null;

    /// <summary>Drops cached session/group data for a user so changes (disable, groups, password) apply at once.</summary>
    public void Forget(int userId) => LocalSessionCache.Forget(_cache, userId);

    // ---------------------------------------------------------------- security log

    public async Task LogAsync(string evt, string? userName, string? by, string? ip, string? details)
    {
        _db.SecurityEvents.Add(new SecurityEvent
        {
            Event = evt,
            UserName = userName?.Length > 256 ? userName[..256] : userName,
            By = by,
            IpAddress = ip,
            Details = details,
            AtUtc = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();
    }

    public static string? ClientIp(HttpContext ctx) => CallerInfo.ClientIp(ctx)?.ToString();
}

/// <summary>Short-lived cache of each local user's active/stamp state and group names, shared with GroupMembership.</summary>
public static class LocalSessionCache
{
    public static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(30);

    public sealed record State(bool Active, string Stamp, IReadOnlySet<string> Groups);

    public static string Key(int userId) => $"local-user|{userId}";

    public static void Forget(IMemoryCache cache, int userId) => cache.Remove(Key(userId));

    public static void ForgetAll(IMemoryCache cache, AppDbContext db)
    {
        foreach (var id in db.LocalUsers.Select(u => u.Id).ToList()) cache.Remove(Key(id));
    }

    /// <summary>Loads (or returns cached) state. Null if the user no longer exists.</summary>
    public static State? Get(IMemoryCache cache, IServiceScopeFactory scopes, int userId) =>
        cache.GetOrCreate(Key(userId), e =>
        {
            e.AbsoluteExpirationRelativeToNow = CacheFor;
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var u = db.LocalUsers.AsNoTracking().Where(x => x.Id == userId)
                .Select(x => new { x.IsActive, x.SecurityStamp, Groups = x.Groups.Select(g => g.Group.Name).ToList() })
                .FirstOrDefault();
            return u == null ? null
                : new State(u.IsActive, u.SecurityStamp, new HashSet<string>(u.Groups, StringComparer.OrdinalIgnoreCase));
        });
}
