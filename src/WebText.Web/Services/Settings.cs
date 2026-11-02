using System.Globalization;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.EntityFrameworkCore;
using WebText.Web.Data;

namespace WebText.Web.Services;

public enum SettingType { Text, Secret, Bool, Number, TimeZone, Phone, Group, IpList, AuthMode, Provider, Token, Weekdays, TimeOfDay, Folder }

/// <summary>Which company sends and receives the texts (Settings → Texting provider).</summary>
public enum SmsProviderKind { Telnyx, VoipMs }

/// <param name="Provider">Only shown/used for this texting provider ("Telnyx" or "VoipMs"); null = always.</param>
public record SettingDef(
    string Name, string Section, string Label, SettingType Type, string? Default, string? Help,
    bool Advanced = false, string? Provider = null)
{
    /// <summary>Stored encrypted and never shown back in full (Token values are shown only inside the callback URL).</summary>
    public bool IsSecret => Type is SettingType.Secret or SettingType.Token;
}

/// <summary>Every setting the app understands. The Settings page is generated from this list.</summary>
public static class SettingDefs
{
    public const string Provider = "Messaging.Provider";
    public const string VoipMsUsername = "VoipMs.ApiUsername";
    public const string VoipMsPassword = "VoipMs.ApiPassword";
    public const string VoipMsDid = "VoipMs.Did";
    public const string VoipMsLongAsMms = "VoipMs.LongTextsAsMms";
    public const string VoipMsDailyLimit = "VoipMs.DailyLimit";
    public const string VoipMsCallbackToken = "VoipMs.CallbackToken";
    public const string VoipMsAllowedIps = "VoipMs.AllowedSourceIps";
    public const string VoipMsBaseUrl = "VoipMs.BaseUrl";
    public const string TelnyxApiKey = "Telnyx.ApiKey";
    public const string TelnyxFromNumber = "Telnyx.FromNumber";
    public const string TelnyxMessagingProfileId = "Telnyx.MessagingProfileId";
    public const string TelnyxWebhookPublicKey = "Telnyx.WebhookPublicKey";
    public const string TelnyxVerifySignature = "Telnyx.VerifyWebhookSignature";
    public const string TelnyxWebhookTolerance = "Telnyx.WebhookToleranceSeconds";
    public const string TelnyxWebhookUrl = "Telnyx.WebhookUrl";
    public const string TelnyxMessagesPerSecond = "Telnyx.MessagesPerSecond";
    public const string TelnyxMaxSendAttempts = "Telnyx.MaxSendAttempts";
    public const string TelnyxBaseUrl = "Telnyx.BaseUrl";
    public const string WebhookAllowedIps = "Webhook.AllowedSourceIps";
    public const string WebhookTunnelOnly = "Webhook.TunnelWebhookOnly";

    /// <summary>Telnyx's published webhook source ranges (as of October 2026). Also the WAF rule in the README.</summary>
    public const string TelnyxWebhookIpDefaults = @"# Telnyx webhook delivery IPs (support.telnyx.com: What IPs do I need to whitelist? - Webhooks section)
# North America
192.76.120.128/29
64.16.239.40/31
64.16.239.49/32
64.16.239.50/32
64.16.239.67/32
64.16.239.72/32
64.16.239.73/32
192.76.120.136/29
64.16.239.99/32
64.16.239.100/32
64.16.239.114/31
192.76.120.144/29
64.16.239.2/31
64.16.239.83/32
64.16.239.84/32
192.76.120.160/29
# Europe / Middle East
185.246.41.0/29
185.246.41.8/29
185.246.41.16/29
185.246.41.24/29
# Asia-Pacific
103.115.244.0/29
103.115.244.8/29
103.115.244.32/29";
    public const string AuthMode = "Auth.Mode";
    public const string AuthIdleMinutes = "Auth.SessionIdleMinutes";
    public const string AuthMaxSessionHours = "Auth.MaxSessionHours";
    public const string AuthLockoutThreshold = "Auth.LockoutThreshold";
    public const string AuthLockoutMinutes = "Auth.LockoutMinutes";
    public const string AuthMinPasswordLength = "Auth.MinPasswordLength";
    public const string BackupEnabled = "Backup.Enabled";
    public const string BackupDays = "Backup.Days";
    public const string BackupTime = "Backup.Time";
    public const string BackupFolder = "Backup.Folder";
    public const string BackupTimestamp = "Backup.TimestampFileName";
    public const string BackupKeep = "Backup.KeepCount";
    public const string BackupIncludeHistory = "Backup.IncludeHistory";
    public const string BackupIncludeLogs = "Backup.IncludeLogs";
    public const string BackupPassword = "Backup.Password";
    public const string UserGroup = "Access.UserGroup";
    public const string AdminGroup = "Access.AdminGroup";
    public const string OfficeName = "General.OfficeName";
    public const string TimeZone = "General.TimeZone";
    public const string CostPerSegment = "Reports.CostPerSegment";
    public const string AutoReplyEnabled = "AutoReply.Enabled";
    public const string AutoReplyMaxPerHour = "AutoReply.MaxPerClientPerHour";

    public static readonly IReadOnlyList<SettingDef> All = new[]
    {
        new SettingDef(Provider, "Texting provider", "Send and receive texts through", SettingType.Provider, "Telnyx",
            "Only the selected provider's settings are shown below. Switching affects new texts; earlier texts stay in each client's history."),

        new SettingDef(VoipMsUsername, "VoIP.ms connection", "Account email", SettingType.Text, null,
            "The email address you sign in to voip.ms with.", Provider: "VoipMs"),
        new SettingDef(VoipMsPassword, "VoIP.ms connection", "API password", SettingType.Secret, null,
            "Not your login password: voip.ms → Main Menu → SOAP and REST/JSON API → API Password. On that page also enable the API " +
            "and add this server's public IP address. Stored encrypted.", Provider: "VoipMs"),
        new SettingDef(VoipMsDid, "VoIP.ms connection", "Sending number (DID)", SettingType.Phone, null,
            "Your SMS-enabled VoIP.ms number (DID → Manage DIDs → Edit → Message Service (SMS/MMS) must be on). US/Canada only.", Provider: "VoipMs"),
        new SettingDef(VoipMsLongAsMms, "VoIP.ms connection", "Send texts over 160 characters as one MMS", SettingType.Bool, "true",
            "VoIP.ms SMS is limited to 160 characters. Yes: longer texts go as a single MMS (up to 1,600 characters here; MMS costs a little more). " +
            "No: they're split into several numbered texts of up to 160 characters.", Provider: "VoipMs"),
        new SettingDef(VoipMsDailyLimit, "VoIP.ms connection", "Daily sending limit", SettingType.Number, "100",
            "VoIP.ms allows 100 texts a day through the API unless you ask their support for more. When the limit is reached, texts wait " +
            "in the queue until the next day (office time zone) instead of failing. 0 = no limit.", Provider: "VoipMs"),
        new SettingDef(VoipMsCallbackToken, "VoIP.ms connection", "Incoming-text callback key", SettingType.Token, null,
            "A random secret in the callback URL that proves a request came from your VoIP.ms account. Created when you save.", Provider: "VoipMs"),
        new SettingDef(VoipMsAllowedIps, "VoIP.ms connection", "Accept callbacks only from these IP ranges", SettingType.IpList, "any",
            "VoIP.ms doesn't publish the addresses its callbacks come from, so this defaults to any and the callback key protects the URL. " +
            "To lock it down, enter the addresses shown under \"Last callback from\" on the right.", Advanced: true, Provider: "VoipMs"),
        new SettingDef(VoipMsBaseUrl, "VoIP.ms connection", "VoIP.ms API URL", SettingType.Text, "https://voip.ms/api/v1/rest.php",
            null, Advanced: true, Provider: "VoipMs"),

        new SettingDef(TelnyxApiKey, "Telnyx connection", "API key", SettingType.Secret, null,
            "Telnyx Portal → API Keys. Stored encrypted; it is never shown again after saving.", Provider: "Telnyx"),
        new SettingDef(TelnyxFromNumber, "Telnyx connection", "Sending number", SettingType.Phone, null,
            "Your Telnyx number that texts come from, e.g. (312) 555-0100.", Provider: "Telnyx"),
        new SettingDef(TelnyxMessagingProfileId, "Telnyx connection", "Messaging profile ID", SettingType.Text, null,
            "Optional. The profile the number is assigned to (Portal → Messaging).", Provider: "Telnyx"),

        new SettingDef(TelnyxWebhookPublicKey, "Incoming texts (webhooks)", "Telnyx public key", SettingType.Text, null,
            "Portal → Account Settings → Keys & Credentials → Public Key. Used to prove incoming webhooks really came from Telnyx.", Provider: "Telnyx"),
        new SettingDef(TelnyxVerifySignature, "Incoming texts (webhooks)", "Verify webhook signatures", SettingType.Bool, "true",
            "Leave on. Turning it off lets anyone who knows the URL inject fake texts.", Provider: "Telnyx"),
        new SettingDef(WebhookAllowedIps, "Incoming texts (webhooks)", "Accept webhooks only from these IP ranges", SettingType.IpList, TelnyxWebhookIpDefaults,
            "One IP or CIDR range per line (# starts a comment). Defaults to Telnyx's published webhook ranges. Through Cloudflare Tunnel the caller's real IP " +
            "(CF-Connecting-IP) is checked. Requests from this server itself are always allowed (for testing). Enter just: any  to turn the check off (not recommended).", Provider: "Telnyx"),
        new SettingDef(WebhookTunnelOnly, "Incoming texts (webhooks)", "Cloudflare Tunnel traffic can only reach the webhook", SettingType.Bool, "true",
            "Anything arriving through Cloudflare other than the provider's webhook (POST /api/webhooks/telnyx or GET /api/webhooks/voipms) " +
            "gets 404, even if the tunnel or WAF is misconfigured. Turn off only if staff reach the app through Cloudflare (with Local sign-in)."),
        new SettingDef(TelnyxWebhookTolerance, "Incoming texts (webhooks)", "Webhook age limit (seconds)", SettingType.Number, "300",
            "Reject webhooks older than this (replay protection).", Advanced: true, Provider: "Telnyx"),
        new SettingDef(TelnyxWebhookUrl, "Incoming texts (webhooks)", "Per-message status URL override", SettingType.Text, null,
            "Optional. Normally leave blank and set the webhook URL on the Telnyx messaging profile instead.", Advanced: true, Provider: "Telnyx"),

        new SettingDef(TelnyxMessagesPerSecond, "Sending", "Messages per second", SettingType.Number, "1",
            "How fast queued texts go out. 1/sec is safe for one 10DLC long code; raise only if your campaign tier allows."),
        new SettingDef(TelnyxMaxSendAttempts, "Sending", "Max send attempts", SettingType.Number, "3",
            "Retries for temporary Telnyx/network errors.", Advanced: true),
        new SettingDef(TelnyxBaseUrl, "Sending", "Telnyx API base URL", SettingType.Text, "https://api.telnyx.com/v2/",
            null, Advanced: true, Provider: "Telnyx"),

        new SettingDef(AuthMode, "Sign-in", "How people sign in", SettingType.AuthMode, "Windows",
            "Windows: staff are signed in automatically with their Windows/Active Directory account, and the groups below are AD groups. " +
            "Local: staff sign in with a user name, password and authenticator-app code managed on the Users page, and the groups below are " +
            "groups on that page. Use Local if the app is hosted outside your network. Switching signs everyone out."),
        new SettingDef(AuthIdleMinutes, "Sign-in", "Sign out after inactivity (minutes)", SettingType.Number, "60",
            "Local sign-in only."),
        new SettingDef(AuthMaxSessionHours, "Sign-in", "Sign in again at least every (hours)", SettingType.Number, "12",
            "Local sign-in only. Even when active, a session ends after this long."),
        new SettingDef(AuthLockoutThreshold, "Sign-in", "Lock the account after this many wrong passwords or codes", SettingType.Number, "5",
            "Local sign-in only.", Advanced: true),
        new SettingDef(AuthLockoutMinutes, "Sign-in", "Lockout length (minutes)", SettingType.Number, "15",
            "Local sign-in only. Admins can also unlock an account on the Users page.", Advanced: true),
        new SettingDef(AuthMinPasswordLength, "Sign-in", "Minimum password length", SettingType.Number, "12",
            "Local sign-in only. At least 8.", Advanced: true),

        new SettingDef(UserGroup, "Access groups", "User group", SettingType.Group, "txtAppUser",
            "Members can use the app: text clients, manage clients and lists, auto-replies, see the message log. " +
            "An AD group with Windows sign-in, or a group on the Users page with Local sign-in."),
        new SettingDef(AdminGroup, "Access groups", "Admin group", SettingType.Group, "txtAppAdmin",
            "Members can also change these settings, see reports, manage users, edit templates and delete clients. Admins don't need to be in the user group."),

        new SettingDef(AutoReplyEnabled, "Auto-replies", "Send auto-replies", SettingType.Bool, "true",
            "Master switch for the keyword auto-replies set up on the Auto-replies page."),
        new SettingDef(AutoReplyMaxPerHour, "Auto-replies", "Max auto-replies per client per hour", SettingType.Number, "20",
            "Safety limit. Stops endless back-and-forth if a client's number is itself an automated system."),

        new SettingDef(BackupEnabled, "Scheduled backups", "Make backups automatically", SettingType.Bool, "false",
            "Saves an encrypted backup file (the same as Backup & restore → Download backup) on the schedule below."),
        new SettingDef(BackupDays, "Scheduled backups", "On these days", SettingType.Weekdays, "Mon,Tue,Wed,Thu,Fri,Sat,Sun", null),
        new SettingDef(BackupTime, "Scheduled backups", "At (office time)", SettingType.TimeOfDay, "23:00",
            "24-hour time in the office time zone, e.g. 23:00 for 11 pm. If the server was off at that time, the backup runs when it's back (within 12 hours)."),
        new SettingDef(BackupFolder, "Scheduled backups", "Save backups in this folder", SettingType.Folder, @"C:\txtappbackup",
            @"A folder on this server (C:\txtappbackup) or a network share written as \\server\share\folder. Mapped drive letters (Z:) don't work: " +
            "the website runs as a service account that doesn't see them. The website's account needs Modify permission there (see the README)."),
        new SettingDef(BackupTimestamp, "Scheduled backups", "Add the date and time to each file name", SettingType.Bool, "true",
            "Yes: <name>-backup-2026-11-02_2300.wtbackup (named after this installation), a new file each time. No: one <name>-backup.wtbackup, replaced each time."),
        new SettingDef(BackupKeep, "Scheduled backups", "Keep this many backup files", SettingType.Number, "30",
            "With dated file names, older backups beyond this number are deleted from the folder. 0 = keep them all."),
        new SettingDef(BackupIncludeHistory, "Scheduled backups", "Include the text history", SettingType.Bool, "true", null),
        new SettingDef(BackupIncludeLogs, "Scheduled backups", "Include the audit logs", SettingType.Bool, "true", null),
        new SettingDef(BackupPassword, "Scheduled backups", "Backup file password", SettingType.Secret, null,
            "At least 12 characters. Every scheduled backup is encrypted with it. Record it somewhere safe OFF this server (e.g. a password manager): " +
            "if the server is lost, you need it to restore. Stored encrypted here."),

        new SettingDef(OfficeName, "General", "Office name", SettingType.Text, "WebText",
            "Shown in the top bar."),
        new SettingDef(TimeZone, "General", "Time zone", SettingType.TimeZone, "Central Standard Time",
            "Used for dates on pages, reports and exports."),
        new SettingDef(CostPerSegment, "General", "Cost per SMS segment (USD)", SettingType.Number, "0",
            "Optional. Used only to estimate spend on the Reports page, e.g. 0.004."),
    };

    public static SettingDef? Find(string name) => All.FirstOrDefault(d => d.Name == name);
}

/// <summary>Immutable view of all settings at one moment.</summary>
public sealed class AppSettingsSnapshot
{
    private readonly IReadOnlyDictionary<string, string?> _values;

    public AppSettingsSnapshot(IReadOnlyDictionary<string, string?> values, IReadOnlyCollection<string> unreadableSecrets)
    {
        _values = values;
        UnreadableSecrets = unreadableSecrets;

        Telnyx = new TelnyxOptions
        {
            ApiKey = Get(SettingDefs.TelnyxApiKey) ?? "",
            FromNumber = Get(SettingDefs.TelnyxFromNumber) ?? "",
            MessagingProfileId = Get(SettingDefs.TelnyxMessagingProfileId),
            WebhookPublicKey = Get(SettingDefs.TelnyxWebhookPublicKey) ?? "",
            VerifyWebhookSignature = Bool(SettingDefs.TelnyxVerifySignature, true),
            WebhookToleranceSeconds = (int)Num(SettingDefs.TelnyxWebhookTolerance, 300),
            WebhookUrl = Get(SettingDefs.TelnyxWebhookUrl),
            MessagesPerSecond = (double)Num(SettingDefs.TelnyxMessagesPerSecond, 1),
            MaxSendAttempts = (int)Num(SettingDefs.TelnyxMaxSendAttempts, 3),
            BaseUrl = Get(SettingDefs.TelnyxBaseUrl) ?? "https://api.telnyx.com/v2/"
        };
        Provider = string.Equals(Get(SettingDefs.Provider), "VoipMs", StringComparison.OrdinalIgnoreCase) ? SmsProviderKind.VoipMs : SmsProviderKind.Telnyx;
        VoipMs = new VoipMsOptions
        {
            Username = Get(SettingDefs.VoipMsUsername) ?? "",
            ApiPassword = Get(SettingDefs.VoipMsPassword) ?? "",
            Did = Get(SettingDefs.VoipMsDid) ?? "",
            LongTextsAsMms = Bool(SettingDefs.VoipMsLongAsMms, true),
            DailyLimit = (int)Num(SettingDefs.VoipMsDailyLimit, 100),
            CallbackToken = Get(SettingDefs.VoipMsCallbackToken) ?? "",
            BaseUrl = Get(SettingDefs.VoipMsBaseUrl) ?? "https://voip.ms/api/v1/rest.php"
        };
        (VoipMsAllowedNetworks, _) = IpRanges.Parse(Get(SettingDefs.VoipMsAllowedIps));
        Backup = new BackupSchedule(
            Bool(SettingDefs.BackupEnabled, false),
            BackupSchedule.ParseDays(Get(SettingDefs.BackupDays)),
            BackupSchedule.ParseTime(Get(SettingDefs.BackupTime)) ?? new TimeOnly(23, 0),
            Get(SettingDefs.BackupFolder) ?? @"C:\txtappbackup",
            Bool(SettingDefs.BackupTimestamp, true),
            (int)Num(SettingDefs.BackupKeep, 30),
            Bool(SettingDefs.BackupIncludeHistory, true),
            Bool(SettingDefs.BackupIncludeLogs, true),
            Get(SettingDefs.BackupPassword) ?? "");
        LocalSignIn = string.Equals(Get(SettingDefs.AuthMode), "Local", StringComparison.OrdinalIgnoreCase);
        SessionIdle = TimeSpan.FromMinutes((double)Math.Clamp(Num(SettingDefs.AuthIdleMinutes, 60), 5, 1440));
        MaxSession = TimeSpan.FromHours((double)Math.Clamp(Num(SettingDefs.AuthMaxSessionHours, 12), 1, 168));
        LockoutThreshold = (int)Math.Clamp(Num(SettingDefs.AuthLockoutThreshold, 5), 3, 50);
        LockoutLength = TimeSpan.FromMinutes((double)Math.Clamp(Num(SettingDefs.AuthLockoutMinutes, 15), 1, 1440));
        MinPasswordLength = (int)Math.Clamp(Num(SettingDefs.AuthMinPasswordLength, 12), 8, 128);
        UserGroup = Get(SettingDefs.UserGroup) ?? "txtAppUser";
        AdminGroup = Get(SettingDefs.AdminGroup) ?? "txtAppAdmin";
        OfficeName = Get(SettingDefs.OfficeName) ?? "WebText";
        TimeZoneId = Get(SettingDefs.TimeZone) ?? "Central Standard Time";
        CostPerSegment = Num(SettingDefs.CostPerSegment, 0);
        TunnelWebhookOnly = Bool(SettingDefs.WebhookTunnelOnly, true);
        AutoRepliesEnabled = Bool(SettingDefs.AutoReplyEnabled, true);
        AutoReplyMaxPerClientPerHour = (int)Math.Max(1, Num(SettingDefs.AutoReplyMaxPerHour, 20));
        (WebhookAllowedNetworks, _) = IpRanges.Parse(Get(SettingDefs.WebhookAllowedIps));
    }

    /// <summary>Null = accept from any IP ("any").</summary>
    public IReadOnlyList<System.Net.IPNetwork>? WebhookAllowedNetworks { get; }
    public bool TunnelWebhookOnly { get; }
    public bool AutoRepliesEnabled { get; }
    public int AutoReplyMaxPerClientPerHour { get; }

    public BackupSchedule Backup { get; }

    /// <summary>True = app-hosted users with password + authenticator code; false = Windows/AD.</summary>
    public bool LocalSignIn { get; }
    public TimeSpan SessionIdle { get; }
    public TimeSpan MaxSession { get; }
    public int LockoutThreshold { get; }
    public TimeSpan LockoutLength { get; }
    public int MinPasswordLength { get; }

    public TelnyxOptions Telnyx { get; }
    public VoipMsOptions VoipMs { get; }
    public SmsProviderKind Provider { get; }
    /// <summary>Null = accept VoIP.ms callbacks from any IP (the callback key still has to match).</summary>
    public IReadOnlyList<System.Net.IPNetwork>? VoipMsAllowedNetworks { get; }

    public string ProviderName => Provider == SmsProviderKind.VoipMs ? "VoIP.ms" : "Telnyx";

    /// <summary>The number texts are sent from, for the selected provider (E.164).</summary>
    public string FromNumber => Provider == SmsProviderKind.VoipMs ? VoipMs.Did : Telnyx.FromNumber;
    public string UserGroup { get; }
    public string AdminGroup { get; }
    public string OfficeName { get; }
    public string TimeZoneId { get; }
    public decimal CostPerSegment { get; }
    public IReadOnlyCollection<string> UnreadableSecrets { get; }

    public bool TelnyxReady => !string.IsNullOrWhiteSpace(Telnyx.ApiKey) && !string.IsNullOrWhiteSpace(Telnyx.FromNumber);
    public bool VoipMsReady => !string.IsNullOrWhiteSpace(VoipMs.Username) && !string.IsNullOrWhiteSpace(VoipMs.ApiPassword)
                               && !string.IsNullOrWhiteSpace(VoipMs.Did);

    /// <summary>The selected provider has its credentials and number, so texts can be sent.</summary>
    public bool TextingReady => Provider == SmsProviderKind.VoipMs ? VoipMsReady : TelnyxReady;

    /// <summary>Stored value, or the definition's default when nothing has been saved.</summary>
    public string? Get(string name)
    {
        if (_values.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v)) return v;
        return SettingDefs.Find(name)?.Default;
    }

    private bool Bool(string name, bool fallback) => bool.TryParse(Get(name), out var b) ? b : fallback;
    private decimal Num(string name, decimal fallback) =>
        decimal.TryParse(Get(name), NumberStyles.Number, CultureInfo.InvariantCulture, out var d) && d >= 0 ? d : fallback;
}

public record SettingMeta(string Name, bool HasValue, DateTime? UpdatedAtUtc, string? UpdatedBy);

/// <summary>
/// Loads settings from SQL and caches them briefly. Every part of the app reads <see cref="Current"/>, so a change on
/// the Settings page takes effect within seconds, on every web server, without a restart.
/// </summary>
public class SettingsService
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(30);

    private readonly IServiceScopeFactory _scopes;
    private readonly IDataProtector _protector;
    private readonly ILogger<SettingsService> _log;
    private readonly object _lock = new();
    private AppSettingsSnapshot? _current;
    private DateTime _loadedAtUtc;

    public SettingsService(IServiceScopeFactory scopes, IDataProtectionProvider dp, ILogger<SettingsService> log, AppInstance instance)
    {
        _scopes = scopes;
        _protector = dp.CreateProtector(instance.Purpose("Settings.v1"));
        _log = log;
    }

    public AppSettingsSnapshot Current
    {
        get
        {
            var snap = _current;
            if (snap != null && DateTime.UtcNow - _loadedAtUtc < CacheFor) return snap;
            lock (_lock)
            {
                if (_current != null && DateTime.UtcNow - _loadedAtUtc < CacheFor) return _current;
                try
                {
                    _current = Load();
                    _loadedAtUtc = DateTime.UtcNow;
                }
                catch (Exception ex) when (_current != null)
                {
                    // Database blip: keep serving the last good settings rather than failing every request.
                    _log.LogWarning(ex, "Could not refresh settings; using cached values");
                    _loadedAtUtc = DateTime.UtcNow - CacheFor + TimeSpan.FromSeconds(5);
                }
                return _current!;
            }
        }
    }

    public void Invalidate() { lock (_lock) _loadedAtUtc = DateTime.MinValue; }

    private AppSettingsSnapshot Load()
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var rows = db.AppSettings.AsNoTracking().ToList();
        var values = new Dictionary<string, string?>();
        var unreadable = new List<string>();
        foreach (var r in rows)
        {
            if (r.IsSecret && !string.IsNullOrEmpty(r.Value))
            {
                try { values[r.Name] = _protector.Unprotect(r.Value); }
                catch (Exception ex)
                {
                    // Typically: the encryption keys were created on a different server (DPAPI is per machine).
                    _log.LogError(ex, "Setting {Name} could not be decrypted; re-enter it on the Settings page", r.Name);
                    unreadable.Add(r.Name);
                }
            }
            else values[r.Name] = r.Value;
        }
        return new AppSettingsSnapshot(values, unreadable);
    }

    public async Task<List<SettingMeta>> GetMetaAsync(CancellationToken ct = default)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var rows = await db.AppSettings.AsNoTracking().ToListAsync(ct);
        return SettingDefs.All.Select(d =>
        {
            var r = rows.FirstOrDefault(x => x.Name == d.Name);
            return new SettingMeta(d.Name, r != null && !string.IsNullOrEmpty(r.Value), r?.UpdatedAtUtc, r?.UpdatedBy);
        }).ToList();
    }

    /// <summary>Saves changed values. A null value for a secret means "leave unchanged"; an empty string clears it.</summary>
    public async Task<int> SaveAsync(IDictionary<string, string?> values, string user, CancellationToken ct = default)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var existing = await db.AppSettings.ToDictionaryAsync(x => x.Name, ct);
        var current = Current;
        var changed = 0;

        foreach (var (name, raw) in values)
        {
            var def = SettingDefs.Find(name);
            if (def == null) continue;
            var isSecret = def.IsSecret;
            if (isSecret && raw == null) continue;

            var value = string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
            var old = current.Get(name);
            existing.TryGetValue(name, out var row);
            if (!isSecret && string.Equals(value ?? def.Default, old, StringComparison.Ordinal)) continue;

            if (row == null)
            {
                row = new AppSetting { Name = name };
                db.AppSettings.Add(row);
            }
            row.IsSecret = isSecret;
            row.Value = value == null ? null : isSecret ? _protector.Protect(value) : value;
            row.UpdatedAtUtc = DateTime.UtcNow;
            row.UpdatedBy = user;

            db.AppSettingChanges.Add(new AppSettingChange
            {
                Name = name,
                NewValueDisplay = isSecret ? (value == null ? "(cleared)" : "(secret updated)") : Trunc(value ?? "(default)", 500),
                ChangedBy = user
            });
            changed++;
        }

        if (changed > 0)
        {
            await db.SaveChangesAsync(ct);
            Invalidate();
        }
        return changed;
    }

    private static string Trunc(string s, int n) => s.Length > n ? s[..n] : s;

    /// <summary>Backups: every stored setting with secrets decrypted, plus the names of secrets that can't be read here.</summary>
    public async Task<(List<(string Name, string? Value, bool IsSecret)> Rows, List<string> Unreadable)> ExportAsync(CancellationToken ct = default)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var rows = await db.AppSettings.AsNoTracking().OrderBy(r => r.Name).ToListAsync(ct);
        var result = new List<(string, string?, bool)>();
        var unreadable = new List<string>();
        foreach (var r in rows)
        {
            if (!r.IsSecret || string.IsNullOrEmpty(r.Value)) { result.Add((r.Name, r.Value, r.IsSecret)); continue; }
            try { result.Add((r.Name, _protector.Unprotect(r.Value), true)); }
            catch (Exception) { unreadable.Add(r.Name); }
        }
        return (result, unreadable);
    }

    /// <summary>Encrypts a secret setting value for storage on this server (used by restore).</summary>
    public string ProtectValue(string value) => _protector.Protect(value);
}

/// <summary>Stores the Data Protection key ring in SQL (keys themselves are DPAPI-encrypted on Windows).</summary>
public class DbXmlRepository : IXmlRepository
{
    private readonly IServiceScopeFactory _scopes;
    public DbXmlRepository(IServiceScopeFactory scopes) => _scopes = scopes;

    public IReadOnlyCollection<XElement> GetAllElements()
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return db.DataProtectionKeys.AsNoTracking().ToList().Select(k => XElement.Parse(k.Xml)).ToList();
    }

    public void StoreElement(XElement element, string friendlyName)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.DataProtectionKeys.Add(new DataProtectionKey { FriendlyName = friendlyName, Xml = element.ToString(SaveOptions.DisableFormatting) });
        db.SaveChanges();
    }
}

/// <summary>Scheduled backup settings (Settings → Scheduled backups).</summary>
public sealed record BackupSchedule(bool Enabled, IReadOnlySet<DayOfWeek> Days, TimeOnly Time, string Folder, bool Timestamp,
    int KeepCount, bool IncludeHistory, bool IncludeLogs, string Password)
{
    public static readonly string[] DayCodes = { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" };

    public static IReadOnlySet<DayOfWeek> ParseDays(string? text)
    {
        var set = new HashSet<DayOfWeek>();
        foreach (var part in (text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var i = Array.FindIndex(DayCodes, d => d.Equals(part, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) set.Add((DayOfWeek)i);
        }
        return set;
    }

    public static TimeOnly? ParseTime(string? text) =>
        TimeOnly.TryParseExact((text ?? "").Trim(), new[] { "H:mm", "HH:mm" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : null;

    /// <summary>The most recent scheduled moment at or before <paramref name="nowLocal"/> (office time), within the last 8 days.</summary>
    public DateTime? LatestAtOrBefore(DateTime nowLocal)
    {
        if (Days.Count == 0) return null;
        for (var d = 0; d <= 7; d++)
        {
            var day = nowLocal.Date.AddDays(-d);
            if (!Days.Contains(day.DayOfWeek)) continue;
            var at = day + Time.ToTimeSpan();
            if (at <= nowLocal) return at;
        }
        return null;
    }

    /// <summary>The next scheduled moment after <paramref name="nowLocal"/> (office time).</summary>
    public DateTime? NextAfter(DateTime nowLocal)
    {
        if (Days.Count == 0) return null;
        for (var d = 0; d <= 7; d++)
        {
            var day = nowLocal.Date.AddDays(d);
            if (!Days.Contains(day.DayOfWeek)) continue;
            var at = day + Time.ToTimeSpan();
            if (at > nowLocal) return at;
        }
        return null;
    }

    public string Describe()
    {
        var days = Days.Count == 7 ? "every day" : Days.Count == 5 && !Days.Contains(DayOfWeek.Saturday) && !Days.Contains(DayOfWeek.Sunday)
            ? "weekdays" : string.Join(", ", Enumerable.Range(0, 7).Where(i => Days.Contains((DayOfWeek)i)).Select(i => DayCodes[i]));
        return $"{days} at {DateTime.Today.Add(Time.ToTimeSpan()):h:mm tt}";
    }
}
