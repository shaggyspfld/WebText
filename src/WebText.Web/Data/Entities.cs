using System.ComponentModel.DataAnnotations;
using WebText.Web.Logging;

namespace WebText.Web.Data;

/// <summary>A client of the firm. One record per person; spouses may share a phone number.</summary>
public class Client
{
    public int Id { get; set; }

    [MaxLength(100)] public string FirstName { get; set; } = "";
    [MaxLength(100)] public string LastName { get; set; } = "";

    /// <summary>Mobile number in E.164 format, e.g. +13125550100.</summary>
    [Required, MaxLength(20)] public string PhoneE164 { get; set; } = "";

    [MaxLength(200)] public string? Email { get; set; }

    /// <summary>Your internal client / account number.</summary>
    [MaxLength(50)] public string? AccountNumber { get; set; }

    [MaxLength(2000)] public string? Notes { get; set; }

    /// <summary>Set when the client texts STOP (or staff marks it). Outbound texts are blocked.</summary>
    public bool OptedOut { get; set; }
    public DateTime? OptedOutAtUtc { get; set; }

    /// <summary>True when the record was auto-created from an inbound text from an unknown number.</summary>
    public bool IsAutoCreated { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    [MaxLength(256)] public string CreatedBy { get; set; } = "";
    public DateTime? UpdatedAtUtc { get; set; }
    [MaxLength(256)] public string? UpdatedBy { get; set; }

    /// <summary>Denormalised for fast conversation-list sorting.</summary>
    public DateTime? LastMessageAtUtc { get; set; }

    public List<Message> Messages { get; set; } = new();
    public List<ClientGroupMember> GroupMemberships { get; set; } = new();

    public string DisplayName =>
        string.IsNullOrWhiteSpace(FirstName + LastName) ? PhoneE164 : $"{FirstName} {LastName}".Trim();
}

public enum MessageDirection
{
    Outbound = 0,
    Inbound = 1,
    /// <summary>Internal staff note (e.g. call log). Never sent to the client.</summary>
    Note = 2
}

public static class MessageStatus
{
    public const string Pending = "pending";             // saved, waiting for the sender worker
    public const string Sending = "sending";             // claimed by the worker / submitted to Telnyx
    public const string Queued = "queued";               // accepted by Telnyx
    public const string Sent = "sent";                   // handed to carrier
    public const string Delivered = "delivered";
    public const string DeliveryUnconfirmed = "delivery_unconfirmed";
    public const string SendingFailed = "sending_failed";
    public const string DeliveryFailed = "delivery_failed";
    public const string Failed = "failed";               // our side: API error
    public const string Blocked = "blocked";             // client opted out
    public const string Received = "received";           // inbound
    public const string Note = "note";

    /// <summary>Higher rank = later in the lifecycle. Webhooks may arrive out of order.</summary>
    public static int Rank(string? s) => s switch
    {
        Pending => 0,
        Sending => 1,
        Queued => 2,
        Sent => 3,
        DeliveryUnconfirmed => 4,
        Delivered or SendingFailed or DeliveryFailed or Failed or Blocked => 5,
        _ => 0
    };

    public static bool IsFailure(string? s) => s is SendingFailed or DeliveryFailed or Failed or Blocked;
}

public class Message
{
    public long Id { get; set; }

    public int ClientId { get; set; }
    public Client Client { get; set; } = null!;

    public MessageDirection Direction { get; set; }

    [MaxLength(4000)] public string Body { get; set; } = "";

    [MaxLength(20)] public string? FromNumber { get; set; }
    [MaxLength(20)] public string? ToNumber { get; set; }

    /// <summary>Telnyx message id (data.payload.id) used to match status webhooks.</summary>
    [MaxLength(64)] public string? TelnyxMessageId { get; set; }

    [MaxLength(40)] public string Status { get; set; } = MessageStatus.Pending;
    [RedactSensitive, MaxLength(1000)] public string? ErrorDetail { get; set; }

    /// <summary>Windows account (DOMAIN\user) that sent the text or wrote the note. Null for inbound.</summary>
    [MaxLength(256)] public string? SentBy { get; set; }

    /// <summary>Groups texts created together by a bulk send.</summary>
    public Guid? BatchId { get; set; }

    public int Segments { get; set; } = 1;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? SentAtUtc { get; set; }
    public DateTime? StatusUpdatedAtUtc { get; set; }

    /// <summary>Inbound only: has a staff member opened the thread since it arrived.</summary>
    public bool IsRead { get; set; }
    [MaxLength(256)] public string? ReadBy { get; set; }
    public DateTime? ReadAtUtc { get; set; }

    public int SendAttempts { get; set; }

    public List<MessageMedia> Media { get; set; } = new();
}

public class MessageMedia
{
    public long Id { get; set; }
    public long MessageId { get; set; }
    public Message Message { get; set; } = null!;
    [MaxLength(2000)] public string Url { get; set; } = "";
    [MaxLength(100)] public string? ContentType { get; set; }
    public long? SizeBytes { get; set; }
}

/// <summary>Saved, reusable message text (e.g. "Your order is ready").</summary>
public class MessageTemplate
{
    public int Id { get; set; }
    [Required, MaxLength(100)] public string Name { get; set; } = "";
    [Required, MaxLength(1600)] public string Body { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    [MaxLength(256)] public string CreatedBy { get; set; } = "";
}

/// <summary>Raw copy of every webhook Telnyx sends. Used for de-duplication and troubleshooting.</summary>
public class WebhookEvent
{
    public long Id { get; set; }
    [Required, MaxLength(64)] public string EventId { get; set; } = "";
    [MaxLength(64)] public string EventType { get; set; } = "";
    public DateTime? OccurredAtUtc { get; set; }
    public DateTime ReceivedAtUtc { get; set; } = DateTime.UtcNow;
    /// <summary>Copy of what the provider sent, for troubleshooting. SSN-like numbers are masked before saving.</summary>
    [RedactSensitive] public string RawJson { get; set; } = "";
    public bool Processed { get; set; }
    [RedactSensitive, MaxLength(1000)] public string? ProcessingError { get; set; }
}

/// <summary>A saved recipient list, e.g. "2026 extensions" or "Business clients", used for bulk sends.</summary>
public class ClientGroup
{
    public int Id { get; set; }
    [Required, MaxLength(100)] public string Name { get; set; } = "";
    [MaxLength(500)] public string? Description { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    [MaxLength(256)] public string CreatedBy { get; set; } = "";
    public List<ClientGroupMember> Members { get; set; } = new();
}

public class ClientGroupMember
{
    public int GroupId { get; set; }
    public ClientGroup Group { get; set; } = null!;
    public int ClientId { get; set; }
    public Client Client { get; set; } = null!;
    public DateTime AddedAtUtc { get; set; } = DateTime.UtcNow;
    [MaxLength(256)] public string AddedBy { get; set; } = "";
}

/// <summary>
/// One application setting (Telnyx connection, AD groups, time zone…), edited on the admin Settings page.
/// Secret values (API key) are encrypted with ASP.NET Core Data Protection before they are stored.
/// </summary>
public class AppSetting
{
    [System.ComponentModel.DataAnnotations.Key, MaxLength(100)] public string Name { get; set; } = "";
    public string? Value { get; set; }
    public bool IsSecret { get; set; }
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    [MaxLength(256)] public string UpdatedBy { get; set; } = "";
}

/// <summary>Audit trail of settings changes. Secret values are never recorded.</summary>
public class AppSettingChange
{
    public long Id { get; set; }
    [Required, MaxLength(100)] public string Name { get; set; } = "";
    [RedactSensitive, MaxLength(500)] public string? NewValueDisplay { get; set; }
    [MaxLength(256)] public string ChangedBy { get; set; } = "";
    public DateTime ChangedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>ASP.NET Core Data Protection key ring, kept in SQL so encrypted settings survive restarts and redeploys.</summary>
public class DataProtectionKey
{
    public int Id { get; set; }
    [MaxLength(200)] public string? FriendlyName { get; set; }
    public string Xml { get; set; } = "";
}

public enum KeywordMatch
{
    /// <summary>The whole text is the keyword (ignoring case, spaces and trailing punctuation). Recommended.</summary>
    Exact = 0,
    /// <summary>The text starts with the keyword, e.g. "JOKE please".</summary>
    FirstWord = 1,
    /// <summary>The keyword appears anywhere in the text as a whole word.</summary>
    Anywhere = 2
}

/// <summary>An automatic reply sent when a client texts a keyword, e.g. HOURS → "We're open 9–5, Mon–Fri."</summary>
public class AutoResponder
{
    public int Id { get; set; }

    /// <summary>Stored upper-case, e.g. "JOKE". One word or a short phrase.</summary>
    [Required, MaxLength(50)] public string Keyword { get; set; } = "";
    public KeywordMatch Match { get; set; } = KeywordMatch.Exact;

    /// <summary>The reply text. Merge fields {FirstName}, {LastName}, {FullName} are filled in.</summary>
    [Required, MaxLength(1600)] public string Reply { get; set; } = "";

    public bool IsActive { get; set; } = true;

    /// <summary>Mark the client's text as read, so it doesn't sit in everyone's unread count.</summary>
    public bool MarkHandled { get; set; } = true;

    /// <summary>Don't send this keyword's reply to the same client again within this many minutes (0 = no limit).</summary>
    public int CooldownMinutes { get; set; } = 5;

    [MaxLength(500)] public string? Notes { get; set; }

    public int TimesUsed { get; set; }
    public DateTime? LastUsedAtUtc { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    [MaxLength(256)] public string CreatedBy { get; set; } = "";
    public DateTime? UpdatedAtUtc { get; set; }
    [MaxLength(256)] public string? UpdatedBy { get; set; }
}

public enum AutoResponderAction { Created = 0, Changed = 1, TurnedOn = 2, TurnedOff = 3, Deleted = 4 }

/// <summary>
/// Audit trail for auto-replies: who added, changed, turned on/off or deleted a keyword, and what changed.
/// No foreign key, so the history survives after a keyword is deleted.
/// </summary>
public class AutoResponderChange
{
    public long Id { get; set; }
    public int AutoResponderId { get; set; }
    [Required, MaxLength(50)] public string Keyword { get; set; } = "";
    public AutoResponderAction Action { get; set; }
    /// <summary>Human-readable list of what changed, e.g. "Reply: 'old' → 'new'". Full settings for Created/Deleted.</summary>
    [RedactSensitive] public string? Details { get; set; }
    [Required, MaxLength(256)] public string ChangedBy { get; set; } = "";
    public DateTime ChangedAtUtc { get; set; } = DateTime.UtcNow;
}

// ------------------------------------------------------------------ app-hosted sign-in (Settings → Sign-in → "Local")

/// <summary>
/// A user account managed inside the app, used when sign-in is set to "Local" instead of Windows/Active Directory.
/// Signing in needs the password AND a 6-digit code from an authenticator app (TOTP).
/// </summary>
public class LocalUser
{
    public int Id { get; set; }

    /// <summary>What they type to sign in, and what's recorded as the sender of their texts.</summary>
    [Required, MaxLength(64)] public string UserName { get; set; } = "";
    [MaxLength(200)] public string? DisplayName { get; set; }
    [MaxLength(256)] public string? Email { get; set; }

    /// <summary>ASP.NET Core Identity password hash (PBKDF2-HMAC-SHA512, salted). Never the password itself.</summary>
    [Required, MaxLength(500)] public string PasswordHash { get; set; } = "";
    /// <summary>Set for new accounts and admin resets: they must choose their own password at next sign-in.</summary>
    public bool MustChangePassword { get; set; } = true;
    public DateTime? PasswordChangedAtUtc { get; set; }

    /// <summary>Authenticator secret, encrypted with Data Protection. Null until they've set up their authenticator.</summary>
    [MaxLength(1000)] public string? TotpSecretProtected { get; set; }
    /// <summary>Last accepted 30-second time step, so a code can't be used twice.</summary>
    public long TotpLastStep { get; set; }

    public bool IsActive { get; set; } = true;
    public int FailedAttempts { get; set; }
    public DateTime? LockedUntilUtc { get; set; }

    /// <summary>Changes on password change/reset, authenticator reset or disable, which signs out existing sessions.</summary>
    [Required, MaxLength(64)] public string SecurityStamp { get; set; } = Guid.NewGuid().ToString("N");

    public DateTime? LastSignInAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    [MaxLength(256)] public string CreatedBy { get; set; } = "";
    public DateTime? UpdatedAtUtc { get; set; }
    [MaxLength(256)] public string? UpdatedBy { get; set; }

    public List<LocalGroupMember> Groups { get; set; } = new();
}

/// <summary>A group of local users. The groups named on the Settings page (txtAppUser / txtAppAdmin) grant access.</summary>
public class LocalGroup
{
    public int Id { get; set; }
    [Required, MaxLength(128)] public string Name { get; set; } = "";
    [MaxLength(500)] public string? Description { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    [MaxLength(256)] public string CreatedBy { get; set; } = "";
    public List<LocalGroupMember> Members { get; set; } = new();
}

public class LocalGroupMember
{
    public int GroupId { get; set; }
    public LocalGroup Group { get; set; } = null!;
    public int UserId { get; set; }
    public LocalUser User { get; set; } = null!;
}

/// <summary>
/// Security audit log: sign-ins (good and bad), lockouts, password and authenticator changes, and every change to
/// local users and groups, with who did it and from where. Kept even after a user is deleted.
/// </summary>
public class SecurityEvent
{
    public long Id { get; set; }
    public DateTime AtUtc { get; set; } = DateTime.UtcNow;
    /// <summary>Short code, e.g. SignIn, SignInFailed, LockedOut, PasswordChanged, UserCreated, GroupsChanged.</summary>
    [Required, MaxLength(50)] public string Event { get; set; } = "";
    /// <summary>The account it's about (may be a name that doesn't exist, for failed sign-ins).</summary>
    [RedactSensitive, MaxLength(256)] public string? UserName { get; set; }
    /// <summary>Who did it (the same as UserName for things people do to themselves).</summary>
    [MaxLength(256)] public string? By { get; set; }
    [MaxLength(64)] public string? IpAddress { get; set; }
    [RedactSensitive] public string? Details { get; set; }
}

/// <summary>One backup run (scheduled or "Back up now"), shown on the Backup &amp; restore page.</summary>
public class BackupRun
{
    public long Id { get; set; }
    /// <summary>The schedule slot this run is for (UTC). Unique, so with several web servers only one runs each slot.
    /// Null for manual runs.</summary>
    public DateTime? ScheduledForUtc { get; set; }
    public DateTime StartedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? FinishedAtUtc { get; set; }
    [MaxLength(100)] public string Server { get; set; } = "";
    [MaxLength(256)] public string StartedBy { get; set; } = "";
    public bool? Success { get; set; }
    [MaxLength(500)] public string? FilePath { get; set; }
    public long? SizeBytes { get; set; }
    [RedactSensitive, MaxLength(2000)] public string? Error { get; set; }
}
