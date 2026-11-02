using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WebText.Web.Auth;
using WebText.Web.Data;

namespace WebText.Web.Services;

/// <summary>
/// Backup and restore of the whole configuration: settings (including API keys), local users and groups, clients,
/// recipient lists, templates and auto-replies; optionally the text history and the audit logs.
///
/// The file is encrypted with a password chosen when it's made (AES-256-GCM, key from PBKDF2-SHA256 with 600,000
/// rounds), because it holds client details, API keys, password hashes and authenticator secrets. Secrets are
/// decrypted from this server's Data Protection keys into the (encrypted) file and re-encrypted with the new server's
/// keys on restore, so a backup moves between servers, unlike the database alone.
/// </summary>
public class BackupService
{
    public const string FileExtension = ".wtbackup";
    private static readonly byte[] Magic = "WTBACKUP"u8.ToArray();
    private const byte FormatVersion = 1;
    private const int Iterations = 600_000;
    public const int MinPasswordLength = 12;
    private const int BatchSize = 1000;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly AppDbContext _db;
    private readonly SettingsService _settings;
    private readonly LocalAccounts _accounts;
    private readonly ILogger<BackupService> _log;

    public BackupService(AppDbContext db, SettingsService settings, LocalAccounts accounts, ILogger<BackupService> log)
    {
        _db = db; _settings = settings; _accounts = accounts; _log = log;
    }

    // ================================================================= file contents (version 1)

    public sealed class BackupFile
    {
        public string Format { get; set; } = "WebText backup";
        public int Version { get; set; } = 1;
        public DateTime CreatedAtUtc { get; set; }
        public string CreatedBy { get; set; } = "";
        public string SourceServer { get; set; } = "";
        public bool IncludesHistory { get; set; }
        public bool IncludesLogs { get; set; }
        public List<string> SecretsNotIncluded { get; set; } = new();

        public List<SettingRow> Settings { get; set; } = new();
        public List<LocalGroupRow> LocalGroups { get; set; } = new();
        public List<LocalUserRow> LocalUsers { get; set; } = new();
        public List<ClientRow> Clients { get; set; } = new();
        public List<ListRow> RecipientLists { get; set; } = new();
        public List<TemplateRow> Templates { get; set; } = new();
        public List<AutoReplyRow> AutoReplies { get; set; } = new();

        public List<MessageRow>? Messages { get; set; }
        public List<AppSettingChange>? SettingChanges { get; set; }
        public List<AutoResponderChange>? AutoReplyChanges { get; set; }
        public List<SecurityEvent>? SecurityLog { get; set; }
    }

    public sealed record SettingRow(string Name, string? Value, bool IsSecret);
    public sealed record LocalGroupRow(int Id, string Name, string? Description, DateTime CreatedAtUtc, string CreatedBy);
    public sealed record LocalUserRow(int Id, string UserName, string? DisplayName, string? Email, string PasswordHash,
        bool MustChangePassword, DateTime? PasswordChangedAtUtc, string? TotpSecret, long TotpLastStep, bool IsActive,
        DateTime? LastSignInAtUtc, DateTime CreatedAtUtc, string CreatedBy, DateTime? UpdatedAtUtc, string? UpdatedBy, List<int> GroupIds);
    public sealed record ClientRow(int Id, string FirstName, string LastName, string PhoneE164, string? Email, string? AccountNumber,
        string? Notes, bool OptedOut, DateTime? OptedOutAtUtc, bool IsAutoCreated, DateTime CreatedAtUtc, string CreatedBy,
        DateTime? UpdatedAtUtc, string? UpdatedBy, DateTime? LastMessageAtUtc);
    public sealed record ListMemberRow(int ClientId, DateTime AddedAtUtc, string AddedBy);
    public sealed record ListRow(int Id, string Name, string? Description, DateTime CreatedAtUtc, string CreatedBy, List<ListMemberRow> Members);
    public sealed record TemplateRow(string Name, string Body, bool IsActive, DateTime CreatedAtUtc, string CreatedBy);
    public sealed record AutoReplyRow(int Id, string Keyword, KeywordMatch Match, string Reply, bool IsActive, bool MarkHandled,
        int CooldownMinutes, string? Notes, int TimesUsed, DateTime? LastUsedAtUtc, DateTime CreatedAtUtc, string CreatedBy,
        DateTime? UpdatedAtUtc, string? UpdatedBy);
    public sealed record MediaRow(string Url, string? ContentType, long? SizeBytes);
    public sealed record MessageRow(int ClientId, MessageDirection Direction, string Body, string? FromNumber, string? ToNumber,
        string? ProviderMessageId, string Status, string? ErrorDetail, string? SentBy, Guid? BatchId, int Segments,
        DateTime CreatedAtUtc, DateTime? SentAtUtc, DateTime? StatusUpdatedAtUtc, bool IsRead, string? ReadBy, DateTime? ReadAtUtc,
        int SendAttempts, List<MediaRow>? Media);

    /// <summary>What a backup contains, for the confirmation shown before restoring.</summary>
    public sealed record Summary(DateTime CreatedAtUtc, string CreatedBy, string SourceServer, int Settings, int LocalUsers,
        int LocalGroups, int Clients, int Lists, int Templates, int AutoReplies, int? Messages, bool IncludesLogs,
        IReadOnlyList<string> SecretsNotIncluded, string? SignInMethod, string? Provider);

    public static Summary Summarize(BackupFile f) => new(f.CreatedAtUtc, f.CreatedBy, f.SourceServer, f.Settings.Count,
        f.LocalUsers.Count, f.LocalGroups.Count, f.Clients.Count, f.RecipientLists.Count, f.Templates.Count, f.AutoReplies.Count,
        f.Messages?.Count, f.IncludesLogs, f.SecretsNotIncluded,
        f.Settings.FirstOrDefault(s => s.Name == SettingDefs.AuthMode)?.Value ?? "Windows",
        f.Settings.FirstOrDefault(s => s.Name == SettingDefs.Provider)?.Value ?? "Telnyx");

    // ================================================================= create

    public async Task<(byte[] File, Summary Summary)> CreateAsync(string password, bool includeHistory, bool includeLogs,
        string user, CancellationToken ct = default)
    {
        var f = new BackupFile
        {
            CreatedAtUtc = DateTime.UtcNow,
            CreatedBy = user,
            SourceServer = Environment.MachineName,
            IncludesHistory = includeHistory,
            IncludesLogs = includeLogs
        };

        var (settings, unreadable) = await _settings.ExportAsync(ct);
        f.Settings = settings.Select(s => new SettingRow(s.Name, s.Value, s.IsSecret)).ToList();
        f.SecretsNotIncluded = unreadable;

        f.LocalGroups = await _db.LocalGroups.AsNoTracking().OrderBy(g => g.Id)
            .Select(g => new LocalGroupRow(g.Id, g.Name, g.Description, g.CreatedAtUtc, g.CreatedBy)).ToListAsync(ct);
        var users = await _db.LocalUsers.AsNoTracking().Include(u => u.Groups).OrderBy(u => u.Id).ToListAsync(ct);
        foreach (var u in users)
        {
            var secret = _accounts.ReadSecretForBackup(u);
            if (u.TotpSecretProtected != null && secret == null) f.SecretsNotIncluded.Add($"authenticator for {u.UserName}");
            f.LocalUsers.Add(new LocalUserRow(u.Id, u.UserName, u.DisplayName, u.Email, u.PasswordHash, u.MustChangePassword,
                u.PasswordChangedAtUtc, secret == null ? null : Convert.ToBase64String(secret), u.TotpLastStep, u.IsActive,
                u.LastSignInAtUtc, u.CreatedAtUtc, u.CreatedBy, u.UpdatedAtUtc, u.UpdatedBy, u.Groups.Select(m => m.GroupId).ToList()));
        }

        f.Clients = await _db.Clients.AsNoTracking().OrderBy(c => c.Id)
            .Select(c => new ClientRow(c.Id, c.FirstName, c.LastName, c.PhoneE164, c.Email, c.AccountNumber, c.Notes, c.OptedOut,
                c.OptedOutAtUtc, c.IsAutoCreated, c.CreatedAtUtc, c.CreatedBy, c.UpdatedAtUtc, c.UpdatedBy, c.LastMessageAtUtc))
            .ToListAsync(ct);
        var lists = await _db.ClientGroups.AsNoTracking().Include(g => g.Members).OrderBy(g => g.Id).ToListAsync(ct);
        f.RecipientLists = lists.Select(g => new ListRow(g.Id, g.Name, g.Description, g.CreatedAtUtc, g.CreatedBy,
            g.Members.Select(m => new ListMemberRow(m.ClientId, m.AddedAtUtc, m.AddedBy)).ToList())).ToList();
        f.Templates = await _db.MessageTemplates.AsNoTracking().OrderBy(t => t.Id)
            .Select(t => new TemplateRow(t.Name, t.Body, t.IsActive, t.CreatedAtUtc, t.CreatedBy)).ToListAsync(ct);
        f.AutoReplies = await _db.AutoResponders.AsNoTracking().OrderBy(r => r.Id)
            .Select(r => new AutoReplyRow(r.Id, r.Keyword, r.Match, r.Reply, r.IsActive, r.MarkHandled, r.CooldownMinutes, r.Notes,
                r.TimesUsed, r.LastUsedAtUtc, r.CreatedAtUtc, r.CreatedBy, r.UpdatedAtUtc, r.UpdatedBy)).ToListAsync(ct);

        if (includeHistory)
        {
            f.Messages = new List<MessageRow>();
            long after = 0;
            while (true)
            {
                var page = await _db.Messages.AsNoTracking().Include(m => m.Media).Where(m => m.Id > after)
                    .OrderBy(m => m.Id).Take(5000).ToListAsync(ct);
                if (page.Count == 0) break;
                f.Messages.AddRange(page.Select(m => new MessageRow(m.ClientId, m.Direction, m.Body, m.FromNumber, m.ToNumber,
                    m.TelnyxMessageId, m.Status, m.ErrorDetail, m.SentBy, m.BatchId, m.Segments, m.CreatedAtUtc, m.SentAtUtc,
                    m.StatusUpdatedAtUtc, m.IsRead, m.ReadBy, m.ReadAtUtc, m.SendAttempts,
                    m.Media.Count == 0 ? null : m.Media.Select(x => new MediaRow(x.Url, x.ContentType, x.SizeBytes)).ToList())));
                after = page[^1].Id;
            }
        }
        if (includeLogs)
        {
            f.SettingChanges = await _db.AppSettingChanges.AsNoTracking().OrderBy(x => x.Id).ToListAsync(ct);
            f.AutoReplyChanges = await _db.AutoResponderChanges.AsNoTracking().OrderBy(x => x.Id).ToListAsync(ct);
            f.SecurityLog = await _db.SecurityEvents.AsNoTracking().OrderBy(x => x.Id).ToListAsync(ct);
        }

        var bytes = Encrypt(JsonSerializer.SerializeToUtf8Bytes(f, Json), password);
        return (bytes, Summarize(f));
    }

    // ================================================================= read

    /// <summary>Decrypts and parses a backup. Throws <see cref="BackupException"/> with a plain-English reason.</summary>
    public static BackupFile Read(byte[] file, string password)
    {
        var json = Decrypt(file, password);
        BackupFile? f;
        try { f = JsonSerializer.Deserialize<BackupFile>(json, Json); }
        catch (JsonException) { throw new BackupException("The backup file is damaged (its contents couldn't be read)."); }
        if (f == null || f.Format != "WebText backup") throw new BackupException("This isn't a WebText backup.");
        if (f.Version > 1) throw new BackupException("This backup was made by a newer version of the app. Update this server first.");
        return f;
    }

    // ================================================================= restore

    public sealed record RestoreOptions(bool RestoreHistory, bool RestoreLogs, bool KeepSignInMethod);

    public sealed record RestoreResult(int Settings, int Users, int Groups, int Clients, int Lists, int Templates, int AutoReplies,
        int Messages, IReadOnlyList<string> Warnings);

    /// <summary>
    /// Replaces this server's configuration and data with the backup, in one transaction (all or nothing).
    /// Kept: this server's encryption keys, incoming-webhook records, and (optionally) its sign-in method.
    /// </summary>
    public async Task<RestoreResult> RestoreAsync(BackupFile f, RestoreOptions o, string user, CancellationToken ct = default)
    {
        var warnings = new List<string>();
        if (f.SecretsNotIncluded.Count > 0)
            warnings.Add("These couldn't be read on the old server and weren't in the backup, so re-enter them: " +
                         string.Join(", ", f.SecretsNotIncluded) + ".");

        // Work out the sign-in method the server will have afterwards, and refuse a restore nobody could sign in to.
        var currentMode = _settings.Current.LocalSignIn ? "Local" : "Windows";
        var backupMode = f.Settings.FirstOrDefault(s => s.Name == SettingDefs.AuthMode)?.Value ?? "Windows";
        var finalMode = o.KeepSignInMethod ? currentMode : backupMode;
        if (string.Equals(finalMode, "Local", StringComparison.OrdinalIgnoreCase))
        {
            var adminSetting = f.Settings.FirstOrDefault(s => s.Name == SettingDefs.AdminGroup)?.Value ?? "txtAppAdmin";
            var adminGroup = GroupMembership.LocalGroupName(adminSetting);
            var adminGroupIds = f.LocalGroups.Where(g => string.Equals(g.Name, adminGroup, StringComparison.OrdinalIgnoreCase)).Select(g => g.Id).ToHashSet();
            if (!f.LocalUsers.Any(u => u.IsActive && u.GroupIds.Any(adminGroupIds.Contains)))
                throw new BackupException($"After this restore the server would use Local sign-in, but the backup has no active user in " +
                    $"{adminGroup}, so nobody could administer the app. Untick \"Keep this server's sign-in method\" only if the backup has a local admin.");
        }

        var counts = new int[8];
        var strategy = _db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            _db.ChangeTracker.Clear();
            await using var tx = await _db.Database.BeginTransactionAsync(ct);

            // ---- clear (children first)
            await _db.MessageMedia.ExecuteDeleteAsync(ct);
            await _db.Messages.ExecuteDeleteAsync(ct);
            await _db.ClientGroupMembers.ExecuteDeleteAsync(ct);
            await _db.ClientGroups.ExecuteDeleteAsync(ct);
            await _db.Clients.ExecuteDeleteAsync(ct);
            await _db.MessageTemplates.ExecuteDeleteAsync(ct);
            await _db.AutoResponders.ExecuteDeleteAsync(ct);
            await _db.LocalGroupMembers.ExecuteDeleteAsync(ct);
            await _db.LocalUsers.ExecuteDeleteAsync(ct);
            await _db.LocalGroups.ExecuteDeleteAsync(ct);
            if (o.KeepSignInMethod) await _db.AppSettings.Where(s => s.Name != SettingDefs.AuthMode).ExecuteDeleteAsync(ct);
            else await _db.AppSettings.ExecuteDeleteAsync(ct);
            if (o.RestoreLogs && f.IncludesLogs)
            {
                await _db.AppSettingChanges.ExecuteDeleteAsync(ct);
                await _db.AutoResponderChanges.ExecuteDeleteAsync(ct);
                // The security log isn't cleared: it keeps this server's own record, and gets the old entries added.
            }

            // ---- settings
            var now = DateTime.UtcNow;
            foreach (var s in f.Settings)
            {
                if (o.KeepSignInMethod && s.Name == SettingDefs.AuthMode) continue;
                var def = SettingDefs.Find(s.Name);
                if (def == null) { warnings.Add($"Skipped the setting {s.Name}, which this version doesn't use."); continue; }
                var secret = def.IsSecret;
                _db.AppSettings.Add(new AppSetting
                {
                    Name = s.Name,
                    IsSecret = secret,
                    Value = s.Value == null ? null : secret ? _settings.ProtectValue(s.Value) : s.Value,
                    UpdatedAtUtc = now,
                    UpdatedBy = $"{user} (restored from backup)"
                });
                counts[0]++;
            }
            await _db.SaveChangesAsync(ct);

            // ---- local groups and users
            var groupMap = new Dictionary<int, LocalGroup>();
            foreach (var g in f.LocalGroups)
            {
                var e = new LocalGroup { Name = g.Name, Description = g.Description, CreatedAtUtc = g.CreatedAtUtc, CreatedBy = g.CreatedBy };
                _db.LocalGroups.Add(e);
                groupMap[g.Id] = e;
            }
            foreach (var u in f.LocalUsers)
            {
                var e = new LocalUser
                {
                    UserName = u.UserName, DisplayName = u.DisplayName, Email = u.Email, PasswordHash = u.PasswordHash,
                    MustChangePassword = u.MustChangePassword, PasswordChangedAtUtc = u.PasswordChangedAtUtc,
                    TotpSecretProtected = u.TotpSecret == null ? null : _accounts.ProtectSecret(Convert.FromBase64String(u.TotpSecret)),
                    TotpLastStep = u.TotpLastStep, IsActive = u.IsActive, LastSignInAtUtc = u.LastSignInAtUtc,
                    CreatedAtUtc = u.CreatedAtUtc, CreatedBy = u.CreatedBy, UpdatedAtUtc = u.UpdatedAtUtc, UpdatedBy = u.UpdatedBy
                    // New SecurityStamp: any session from before the restore is ended.
                };
                foreach (var gid in u.GroupIds.Distinct())
                    if (groupMap.TryGetValue(gid, out var g)) e.Groups.Add(new LocalGroupMember { Group = g });
                _db.LocalUsers.Add(e);
                counts[1]++;
            }
            counts[2] = f.LocalGroups.Count;
            await _db.SaveChangesAsync(ct);
            _db.ChangeTracker.Clear();

            // ---- clients (ids change, so keep a map for lists and history)
            var clientMap = new Dictionary<int, int>();
            foreach (var chunk in f.Clients.Chunk(BatchSize))
            {
                var added = chunk.Select(c => (c.Id, Entity: new Client
                {
                    FirstName = c.FirstName, LastName = c.LastName, PhoneE164 = c.PhoneE164, Email = c.Email, AccountNumber = c.AccountNumber,
                    Notes = c.Notes, OptedOut = c.OptedOut, OptedOutAtUtc = c.OptedOutAtUtc, IsAutoCreated = c.IsAutoCreated,
                    CreatedAtUtc = c.CreatedAtUtc, CreatedBy = c.CreatedBy, UpdatedAtUtc = c.UpdatedAtUtc, UpdatedBy = c.UpdatedBy,
                    LastMessageAtUtc = c.LastMessageAtUtc
                })).ToList();
                _db.Clients.AddRange(added.Select(a => a.Entity));
                await _db.SaveChangesAsync(ct);
                foreach (var a in added) clientMap[a.Id] = a.Entity.Id;
                _db.ChangeTracker.Clear();
                counts[3] += added.Count;
            }

            foreach (var l in f.RecipientLists)
            {
                var g = new ClientGroup { Name = l.Name, Description = l.Description, CreatedAtUtc = l.CreatedAtUtc, CreatedBy = l.CreatedBy };
                foreach (var m in l.Members.DistinctBy(m => m.ClientId))
                    if (clientMap.TryGetValue(m.ClientId, out var cid))
                        g.Members.Add(new ClientGroupMember { ClientId = cid, AddedAtUtc = m.AddedAtUtc, AddedBy = m.AddedBy });
                _db.ClientGroups.Add(g);
                counts[4]++;
            }
            foreach (var t in f.Templates)
            {
                _db.MessageTemplates.Add(new MessageTemplate { Name = t.Name, Body = t.Body, IsActive = t.IsActive, CreatedAtUtc = t.CreatedAtUtc, CreatedBy = t.CreatedBy });
                counts[5]++;
            }
            var replyMap = new Dictionary<int, AutoResponder>();
            foreach (var r in f.AutoReplies)
            {
                var e = new AutoResponder
                {
                    Keyword = r.Keyword, Match = r.Match, Reply = r.Reply, IsActive = r.IsActive, MarkHandled = r.MarkHandled,
                    CooldownMinutes = r.CooldownMinutes, Notes = r.Notes, TimesUsed = r.TimesUsed, LastUsedAtUtc = r.LastUsedAtUtc,
                    CreatedAtUtc = r.CreatedAtUtc, CreatedBy = r.CreatedBy, UpdatedAtUtc = r.UpdatedAtUtc, UpdatedBy = r.UpdatedBy
                };
                _db.AutoResponders.Add(e);
                replyMap[r.Id] = e;
                counts[6]++;
            }
            await _db.SaveChangesAsync(ct);
            var replyIdMap = replyMap.ToDictionary(kv => kv.Key, kv => kv.Value.Id);
            _db.ChangeTracker.Clear();

            // ---- text history
            if (o.RestoreHistory && f.Messages != null)
            {
                foreach (var chunk in f.Messages.Chunk(BatchSize))
                {
                    foreach (var m in chunk)
                    {
                        if (!clientMap.TryGetValue(m.ClientId, out var cid)) continue;
                        var e = new Message
                        {
                            ClientId = cid, Direction = m.Direction, Body = m.Body, FromNumber = m.FromNumber, ToNumber = m.ToNumber,
                            TelnyxMessageId = m.ProviderMessageId,
                            // Anything still waiting to send on the old server is NOT sent from here.
                            Status = m.Status is MessageStatus.Pending or MessageStatus.Sending ? MessageStatus.Failed : m.Status,
                            ErrorDetail = m.Status is MessageStatus.Pending or MessageStatus.Sending
                                ? "Not sent: it was still queued when the backup was made. Resend if needed." : m.ErrorDetail,
                            SentBy = m.SentBy, BatchId = m.BatchId, Segments = m.Segments, CreatedAtUtc = m.CreatedAtUtc,
                            SentAtUtc = m.SentAtUtc, StatusUpdatedAtUtc = m.StatusUpdatedAtUtc, IsRead = m.IsRead, ReadBy = m.ReadBy,
                            ReadAtUtc = m.ReadAtUtc, SendAttempts = m.SendAttempts
                        };
                        if (m.Media != null)
                            foreach (var x in m.Media) e.Media.Add(new MessageMedia { Url = x.Url, ContentType = x.ContentType, SizeBytes = x.SizeBytes });
                        _db.Messages.Add(e);
                        counts[7]++;
                    }
                    await _db.SaveChangesAsync(ct);
                    _db.ChangeTracker.Clear();
                }
            }

            // ---- audit logs
            if (o.RestoreLogs && f.IncludesLogs)
            {
                foreach (var c in f.SettingChanges ?? new()) { c.Id = 0; _db.AppSettingChanges.Add(c); }
                foreach (var c in f.AutoReplyChanges ?? new())
                {
                    c.Id = 0;
                    if (replyIdMap.TryGetValue(c.AutoResponderId, out var nid)) c.AutoResponderId = nid;
                    _db.AutoResponderChanges.Add(c);
                }
                foreach (var e in f.SecurityLog ?? new()) { e.Id = 0; _db.SecurityEvents.Add(e); }
                await _db.SaveChangesAsync(ct);
                _db.ChangeTracker.Clear();
            }

            _db.AppSettingChanges.Add(new AppSettingChange
            {
                Name = "(restore)", ChangedBy = user, ChangedAtUtc = DateTime.UtcNow,
                NewValueDisplay = $"Restored from a backup made {f.CreatedAtUtc:yyyy-MM-dd HH:mm} UTC on {f.SourceServer} by {f.CreatedBy}"
            });
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        });

        _settings.Invalidate();
        await SeedData.EnsureLocalAccessGroupsAsync(_db, _settings.Current, "restore");
        if (f.Messages == null && o.RestoreHistory) warnings.Add("The backup didn't include text history.");
        if (f.LocalUsers.Any(u => u.TotpSecret == null))
            warnings.Add("Local users without an authenticator in the backup will set one up at their next sign-in.");
        _log.LogWarning("Configuration restored from a backup made {Created} on {Server} (by {User})", f.CreatedAtUtc, f.SourceServer, user);
        return new RestoreResult(counts[0], counts[1], counts[2], counts[3], counts[4], counts[5], counts[6], counts[7], warnings);
    }

    // ================================================================= encryption

    /// <summary>Header (magic, version, rounds, salt, nonce) + GCM tag + ciphertext of the gzipped JSON.</summary>
    public static byte[] Encrypt(byte[] plain, string password)
    {
        byte[] compressed;
        using (var ms = new MemoryStream())
        {
            using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true)) gz.Write(plain);
            compressed = ms.ToArray();
        }
        var salt = RandomNumberGenerator.GetBytes(16);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var key = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256, 32);

        using var header = new MemoryStream();
        header.Write(Magic);
        header.WriteByte(FormatVersion);
        header.Write(BitConverter.GetBytes(Iterations));
        header.Write(salt);
        header.Write(nonce);
        var aad = header.ToArray();

        var cipher = new byte[compressed.Length];
        var tag = new byte[16];
        using (var gcm = new AesGcm(key, 16)) gcm.Encrypt(nonce, compressed, cipher, tag, aad);
        CryptographicOperations.ZeroMemory(key);

        var result = new byte[aad.Length + tag.Length + cipher.Length];
        aad.CopyTo(result, 0);
        tag.CopyTo(result, aad.Length);
        cipher.CopyTo(result, aad.Length + tag.Length);
        return result;
    }

    public static byte[] Decrypt(byte[] file, string password)
    {
        const int headerLen = 8 + 1 + 4 + 16 + 12;
        if (file.Length < headerLen + 16 || !file.AsSpan(0, 8).SequenceEqual(Magic))
            throw new BackupException("This isn't a WebText backup file.");
        if (file[8] != FormatVersion) throw new BackupException("This backup was made by a different version of the app.");
        var iterations = BitConverter.ToInt32(file, 9);
        if (iterations < 100_000 || iterations > 10_000_000) throw new BackupException("The backup file is damaged.");
        var salt = file.AsSpan(13, 16).ToArray();
        var nonce = file.AsSpan(29, 12).ToArray();
        var aad = file.AsSpan(0, headerLen).ToArray();
        var tag = file.AsSpan(headerLen, 16).ToArray();
        var cipher = file.AsSpan(headerLen + 16).ToArray();

        var key = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password ?? ""), salt, iterations, HashAlgorithmName.SHA256, 32);
        var compressed = new byte[cipher.Length];
        try
        {
            using var gcm = new AesGcm(key, 16);
            gcm.Decrypt(nonce, cipher, tag, compressed, aad);
        }
        catch (AuthenticationTagMismatchException)
        {
            throw new BackupException("The password is wrong, or the file has been changed or damaged.");
        }
        finally { CryptographicOperations.ZeroMemory(key); }

        using var input = new GZipStream(new MemoryStream(compressed), CompressionMode.Decompress);
        using var output = new MemoryStream();
        input.CopyTo(output);
        return output.ToArray();
    }
}

public class BackupException : Exception
{
    public BackupException(string message) : base(message) { }
}
