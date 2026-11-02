using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using WebText.Web.Api;
using WebText.Web.Auth;
using WebText.Web.Data;
using WebText.Web.Services;

namespace WebText.Web.Pages.Admin;

/// <summary>Admin-only (folder convention in Program.cs). All configuration except the DB connection lives here.</summary>
public class SettingsModel : PageModel
{
    private readonly SettingsService _settings;
    private readonly AppDbContext _db;
    private readonly SmsSenders _senders;
    private readonly GroupMembership _groups;

    private readonly LocalAccounts _accounts;
    private readonly VoipMsDailyLimit _dailyLimit;
    private readonly Microsoft.Extensions.Caching.Memory.IMemoryCache _cache;

    public SettingsModel(SettingsService settings, AppDbContext db, SmsSenders senders, GroupMembership groups, WebhookGuard guard,
        LocalAccounts accounts, Microsoft.Extensions.Caching.Memory.IMemoryCache cache, VoipMsDailyLimit dailyLimit)
    {
        _dailyLimit = dailyLimit;
        _settings = settings; _db = db; _senders = senders; _groups = groups; Guard = guard; _accounts = accounts; _cache = cache;
    }

    public string LocalUserSummary { get; private set; } = "";

    public WebhookGuard Guard { get; }

    public AppSettingsSnapshot Current { get; private set; } = null!;
    public Dictionary<string, SettingMeta> Meta { get; private set; } = new();
    public Dictionary<string, string> Errors { get; } = new();
    public Dictionary<string, string?> Posted { get; } = new();
    public List<AppSettingChange> History { get; private set; } = new();
    public IReadOnlyList<TimeZoneInfo> TimeZones { get; } = TimeZoneInfo.GetSystemTimeZones();

    // diagnostics
    public string SuggestedWebhookUrl { get; private set; } = "";
    public string? VoipMsCallbackUrl { get; private set; }
    public (int Used, int Limit, int Remaining)? DailyLimit { get; private set; }

    private static string NewToken() =>
        Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    public DateTime? LastWebhookUtc { get; private set; }
    public int Webhooks24h { get; private set; }
    public List<WebhookEvent> RecentWebhookErrors { get; private set; } = new();
    public int QueuedTexts { get; private set; }
    public bool YouAreUser { get; private set; }
    public bool YouAreAdmin { get; private set; }

    [TempData] public string? TestResult { get; set; }
    [TempData] public bool TestOk { get; set; }

    public async Task OnGetAsync() => await LoadAsync();

    public async Task<IActionResult> OnPostSaveAsync()
    {
        var values = new Dictionary<string, string?>();
        foreach (var def in SettingDefs.All)
        {
            var field = "v:" + def.Name;
            if (def.Type == SettingType.Token)
            {
                if (Request.Form["regen:" + def.Name].Count > 0) values[def.Name] = NewToken();
                continue;
            }
            if (def.Type == SettingType.Secret)
            {
                var clear = Request.Form["clear:" + def.Name].Count > 0;
                var entered = Request.Form[field].ToString();
                if (clear) values[def.Name] = "";
                else if (!string.IsNullOrWhiteSpace(entered))
                {
                    if (def.Name == SettingDefs.BackupPassword && entered.Trim().Length < BackupService.MinPasswordLength)
                        Errors[def.Name] = $"Use at least {BackupService.MinPasswordLength} characters.";
                    else values[def.Name] = entered.Trim();
                }
                continue;   // blank = keep the stored secret
            }
            if (!Request.Form.ContainsKey(field)) continue;
            var v = Request.Form[field].ToString().Trim();
            Posted[def.Name] = v;
            var (normalised, error) = Validate(def, v);
            if (error != null) Errors[def.Name] = error;
            else values[def.Name] = normalised;
        }

        // Lock-out guard: you can't move admin rights to a group you aren't in.
        if (!Errors.ContainsKey(SettingDefs.AdminGroup) && values.TryGetValue(SettingDefs.AdminGroup, out var newAdmin)
            && !string.IsNullOrWhiteSpace(newAdmin)
            && !string.Equals(newAdmin, _settings.Current.AdminGroup, StringComparison.OrdinalIgnoreCase)
            && !_groups.IsMember(User, newAdmin))
        {
            Errors[SettingDefs.AdminGroup] =
                _settings.Current.LocalSignIn
                    ? $"You aren't in the local group \"{newAdmin}\". Saving it would lock you out of Settings. Create the group on the Users page and add yourself first."
                    : $"You aren't a member of \"{newAdmin}\" (or the name doesn't exist in AD). Saving it would lock you out of Settings. " +
                      "Add yourself to that group first, sign out of Windows and back in, then change this.";
        }

        // Scheduled backups need a password to encrypt the files with.
        var backupOn = values.TryGetValue(SettingDefs.BackupEnabled, out var bo) ? bo == "true" : _settings.Current.Backup.Enabled;
        var passwordAfter = values.TryGetValue(SettingDefs.BackupPassword, out var bp) ? bp : _settings.Current.Backup.Password;
        if (backupOn && string.IsNullOrEmpty(passwordAfter) && !Errors.ContainsKey(SettingDefs.BackupPassword))
            Errors[SettingDefs.BackupPassword] = "Scheduled backups need a password to encrypt the files with.";

        // VoIP.ms needs a callback key in its URL: create one the first time VoIP.ms is selected.
        var provider = values.TryGetValue(SettingDefs.Provider, out var pv) && pv != null ? pv : _settings.Current.Get(SettingDefs.Provider);
        if (string.Equals(provider, "VoipMs", StringComparison.OrdinalIgnoreCase) && !values.ContainsKey(SettingDefs.VoipMsCallbackToken)
            && string.IsNullOrEmpty(_settings.Current.VoipMs.CallbackToken))
            values[SettingDefs.VoipMsCallbackToken] = NewToken();

        // Sign-in method: don't allow a switch that nobody could sign in with afterwards.
        var wasLocal = _settings.Current.LocalSignIn;
        var toLocal = values.TryGetValue(SettingDefs.AuthMode, out var mode) && string.Equals(mode, "Local", StringComparison.OrdinalIgnoreCase);
        if (values.ContainsKey(SettingDefs.AuthMode) && toLocal != wasLocal && toLocal && !Errors.ContainsKey(SettingDefs.AdminGroup))
        {
            var adminGroup = GroupMembership.LocalGroupName(values.TryGetValue(SettingDefs.AdminGroup, out var ag) && !string.IsNullOrWhiteSpace(ag)
                ? ag! : _settings.Current.AdminGroup);
            var admins = await _db.LocalGroupMembers.CountAsync(m => m.Group.Name == adminGroup && m.User.IsActive);
            if (admins == 0)
                Errors[SettingDefs.AuthMode] = $"No local user is in the {adminGroup} group yet, so nobody could administer the app after switching. " +
                    "Add yourself on the Users page first (you can do that while still on Windows sign-in).";
        }

        if (Errors.Count > 0)
        {
            await LoadAsync();
            return Page();
        }

        var n = await _settings.SaveAsync(values, User.UserName());
        if (values.ContainsKey(SettingDefs.AuthMode) && toLocal != wasLocal)
        {
            _settings.Invalidate();
            LocalSessionCache.ForgetAll(_cache, _db);
            await SeedData.EnsureLocalAccessGroupsAsync(_db, _settings.Current, User.UserName());
            await _accounts.LogAsync("SignInModeChanged", null, User.UserName(), LocalAccounts.ClientIp(HttpContext),
                toLocal ? "Windows → Local (user name, password and authenticator code)" : "Local → Windows (Active Directory)");
            // The person who switched is now signed out (their current sign-in belongs to the other method).
            return Redirect(toLocal ? "/Account/Login" : "/");
        }
        if (values.TryGetValue(SettingDefs.UserGroup, out _) || values.TryGetValue(SettingDefs.AdminGroup, out _))
            await SeedData.EnsureLocalAccessGroupsAsync(_db, _settings.Current, User.UserName());
        TempData["Flash"] = n == 0 ? "No changes to save." : $"Saved {n} setting{(n == 1 ? "" : "s")}. Changes apply within a few seconds; no restart needed.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostTestAsync()
    {
        _settings.Invalidate();
        var r = await _senders.Current.TestConnectionAsync(HttpContext.RequestAborted);
        TestResult = r.Message; TestOk = r.Success;
        return Redirect("/Admin/Settings#telnyx");
    }

    public async Task<IActionResult> OnPostSendTestAsync(string? testPhone)
    {
        var to = PhoneNumber.ToE164(testPhone);
        if (to == null) { TestResult = "Enter a valid mobile number for the test text."; TestOk = false; return Redirect("/Admin/Settings#telnyx"); }
        _settings.Invalidate();
        var sender = _senders.Current;
        var r = await sender.SendAsync(to, $"Test text from {_settings.Current.OfficeName}. Reply to check that incoming texts reach the app.", HttpContext.RequestAborted);
        TestOk = r.Success;
        TestResult = r.Success
            ? $"{sender.Name} accepted the test text to {PhoneNumber.Format(to)} (status: {r.Status}). If you reply, the reply should appear in the inbox under that number."
            : $"Test text failed: {r.Error}";
        return Redirect("/Admin/Settings#telnyx");
    }

    private (string? value, string? error) Validate(SettingDef def, string v)
    {
        if (v.Length == 0)
        {
            return def.Type == SettingType.Group ? (null, "A group name is required.") : (null, null);   // blank = default
        }
        switch (def.Type)
        {
            case SettingType.Phone:
                var e164 = PhoneNumber.ToE164(v);
                return e164 == null ? (null, "Enter a valid phone number.") : (e164, null);
            case SettingType.Number:
                return decimal.TryParse(v, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) && d >= 0
                    ? (d.ToString(CultureInfo.InvariantCulture), null) : (null, "Enter a number (0 or more).");
            case SettingType.Bool:
                return bool.TryParse(v, out var b) ? (b ? "true" : "false", null) : (null, "Choose yes or no.");
            case SettingType.TimeZone:
                try { TimeZoneInfo.FindSystemTimeZoneById(v); return (v, null); }
                catch (Exception) { return (null, "Unknown time zone."); }
            case SettingType.Weekdays:
                var days = BackupSchedule.ParseDays(v);
                return days.Count == 0 ? (null, "Choose at least one day.")
                    : (string.Join(",", Enumerable.Range(0, 7).Where(i => days.Contains((DayOfWeek)i)).Select(i => BackupSchedule.DayCodes[i])), null);
            case SettingType.TimeOfDay:
                return BackupSchedule.ParseTime(v) is { } t ? (t.ToString("HH:mm"), null) : (null, "Enter a time such as 23:00.");
            case SettingType.Folder:
                return (FolderProblem(v), v.Trim()) switch { (null, var f) => (f, null), (var err, _) => (null, err) };
            case SettingType.Provider:
                return v.Equals("VoipMs", StringComparison.OrdinalIgnoreCase) ? ("VoipMs", null)
                    : v.Equals("Telnyx", StringComparison.OrdinalIgnoreCase) ? ("Telnyx", null) : (null, "Choose a provider.");
            case SettingType.AuthMode:
                return v.Equals("Local", StringComparison.OrdinalIgnoreCase) ? ("Local", null)
                    : v.Equals("Windows", StringComparison.OrdinalIgnoreCase) ? ("Windows", null) : (null, "Choose Windows or Local.");
            case SettingType.Group:
                return v.IndexOfAny(new[] { '/', '"', '<', '>' }) >= 0 ? (null, "Use a group name like txtAppUser or DOMAIN\\txtAppUser.") : (v, null);
            case SettingType.IpList:
                var normalised = v.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
                var (nets, bad) = IpRanges.Parse(normalised);
                if (bad.Count > 0) return (null, "Not a valid IP address or CIDR range: " + string.Join(", ", bad.Take(5)));
                if (nets != null && nets.Count == 0) return (null, "Enter at least one IP range, or the word any.");
                return (normalised, null);
        }
        if (def.Name == SettingDefs.TelnyxWebhookPublicKey && TelnyxSignatureVerifier.ParseKey(v) == null)
            return (null, "That doesn't look like a Telnyx public key (base64, 32-byte Ed25519 key).");
        if (def.Name == SettingDefs.TelnyxBaseUrl || def.Name == SettingDefs.TelnyxWebhookUrl || def.Name == SettingDefs.VoipMsBaseUrl)
            return Uri.TryCreate(v, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps ? (v, null) : (null, "Enter a full https:// URL.");
        return (v, null);
    }

    private async Task LoadAsync()
    {
        _settings.Invalidate();
        Current = _settings.Current;
        Meta = (await _settings.GetMetaAsync()).ToDictionary(m => m.Name);
        History = await _db.AppSettingChanges.AsNoTracking().OrderByDescending(c => c.ChangedAtUtc).Take(50).ToListAsync();

        SuggestedWebhookUrl = $"https://{Request.Host}/api/webhooks/telnyx";
        VoipMsCallbackUrl = string.IsNullOrEmpty(Current.VoipMs.CallbackToken) ? null
            : $"https://{Request.Host}{CallerInfo.VoipMsWebhookPrefix}?key={Current.VoipMs.CallbackToken}" +
              "&to={TO}&from={FROM}&message={MESSAGE}&id={ID}&date={TIMESTAMP}&media={MEDIA}";
        DailyLimit = await _dailyLimit.TodayAsync();
        LastWebhookUtc = await _db.WebhookEvents.MaxAsync(e => (DateTime?)e.ReceivedAtUtc);
        var since = DateTime.UtcNow.AddDays(-1);
        Webhooks24h = await _db.WebhookEvents.CountAsync(e => e.ReceivedAtUtc >= since);
        RecentWebhookErrors = await _db.WebhookEvents.AsNoTracking().Where(e => e.ProcessingError != null)
            .OrderByDescending(e => e.ReceivedAtUtc).Take(5).ToListAsync();
        QueuedTexts = await _db.Messages.CountAsync(m => m.Status == MessageStatus.Pending);
        YouAreUser = _groups.IsMember(User, Current.UserGroup);
        YouAreAdmin = _groups.IsAdmin(User);
        var localUsers = await _db.LocalUsers.CountAsync(u => u.IsActive);
        LocalUserSummary = localUsers == 0 ? "none yet" : $"{localUsers} active user{(localUsers == 1 ? "" : "s")}";
    }

    /// <summary>Null if the backup folder is usable as typed; otherwise why not.</summary>
    public static string? FolderProblem(string path)
    {
        path = path.Trim();
        if (path.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || path.Contains('"')) return "That folder name has characters Windows doesn't allow.";
        if (path.StartsWith(@"\\")) return path.TrimStart('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries).Length >= 2 ? null
            : @"Write a network folder as \\server\share or \\server\share\folder.";
        if (!Path.IsPathFullyQualified(path)) return @"Enter a full path, e.g. C:\txtappbackup or \\server\share\txtappbackup.";
        if (OperatingSystem.IsWindows() && path.Length >= 2 && path[1] == ':')
        {
            try
            {
                var drive = new DriveInfo(path[..1]);
                if (drive.DriveType == DriveType.Network)
                    return $@"{path[..2]} is a mapped network drive, which the website's service account can't see. Use the network path instead (\\server\share\…).";
                if (drive.DriveType == DriveType.NoRootDirectory)
                    return $"There's no {path[..2]} drive on this server. If it's a mapped network drive, use its network path (\\\\server\\share\\…).";
            }
            catch (ArgumentException) { return "That isn't a valid drive."; }
        }
        return null;
    }

    /// <summary>Value to show in a (non-secret) field: what was just posted, else the stored/default value.</summary>
    public string Display(SettingDef def) =>
        Posted.TryGetValue(def.Name, out var p) ? p ?? "" : Current.Get(def.Name) ?? "";
}
