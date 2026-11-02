using Microsoft.EntityFrameworkCore;

namespace WebText.Web.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Client> Clients => Set<Client>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<MessageMedia> MessageMedia => Set<MessageMedia>();
    public DbSet<MessageTemplate> MessageTemplates => Set<MessageTemplate>();
    public DbSet<WebhookEvent> WebhookEvents => Set<WebhookEvent>();
    public DbSet<ClientGroup> ClientGroups => Set<ClientGroup>();
    public DbSet<ClientGroupMember> ClientGroupMembers => Set<ClientGroupMember>();
    public DbSet<AppSetting> AppSettings => Set<AppSetting>();
    public DbSet<AppSettingChange> AppSettingChanges => Set<AppSettingChange>();
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();
    public DbSet<AutoResponder> AutoResponders => Set<AutoResponder>();
    public DbSet<AutoResponderChange> AutoResponderChanges => Set<AutoResponderChange>();
    public DbSet<LocalUser> LocalUsers => Set<LocalUser>();
    public DbSet<LocalGroup> LocalGroups => Set<LocalGroup>();
    public DbSet<LocalGroupMember> LocalGroupMembers => Set<LocalGroupMember>();
    public DbSet<SecurityEvent> SecurityEvents => Set<SecurityEvent>();
    public DbSet<BackupRun> BackupRuns => Set<BackupRun>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Client>(e =>
        {
            // Not unique: spouses / household members can share one mobile number.
            e.HasIndex(x => x.PhoneE164);
            e.HasIndex(x => x.LastMessageAtUtc);
            e.HasIndex(x => new { x.LastName, x.FirstName });
            e.HasIndex(x => x.AccountNumber);
            e.Ignore(x => x.DisplayName);
        });

        b.Entity<Message>(e =>
        {
            e.HasOne(x => x.Client).WithMany(c => c.Messages).HasForeignKey(x => x.ClientId)
             .OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.Direction).HasConversion<int>();
            e.HasIndex(x => new { x.ClientId, x.CreatedAtUtc });
            e.HasIndex(x => x.TelnyxMessageId);
            e.HasIndex(x => x.Status);
            e.HasIndex(x => x.SentBy);
            e.HasIndex(x => x.CreatedAtUtc);
            e.HasIndex(x => new { x.Direction, x.IsRead });
        });

        b.Entity<MessageMedia>(e =>
        {
            e.HasOne(x => x.Message).WithMany(m => m.Media).HasForeignKey(x => x.MessageId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<WebhookEvent>(e =>
        {
            e.HasIndex(x => x.EventId).IsUnique();
            e.HasIndex(x => x.ReceivedAtUtc);
        });

        b.Entity<MessageTemplate>(e => e.HasIndex(x => x.Name));

        b.Entity<ClientGroup>(e => e.HasIndex(x => x.Name).IsUnique());

        b.Entity<ClientGroupMember>(e =>
        {
            e.HasKey(x => new { x.GroupId, x.ClientId });
            e.HasOne(x => x.Group).WithMany(g => g.Members).HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Client).WithMany(c => c.GroupMemberships).HasForeignKey(x => x.ClientId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.ClientId);
        });

        b.Entity<AppSettingChange>(e => e.HasIndex(x => x.ChangedAtUtc));

        b.Entity<AutoResponder>(e =>
        {
            e.HasIndex(x => x.Keyword).IsUnique();
            e.Property(x => x.Match).HasConversion<int>();
        });
        b.Entity<AutoResponderChange>(e =>
        {
            e.HasIndex(x => x.ChangedAtUtc);
            e.HasIndex(x => x.AutoResponderId);
            e.Property(x => x.Action).HasConversion<int>();
        });

        b.Entity<LocalUser>(e => e.HasIndex(x => x.UserName).IsUnique());
        b.Entity<LocalGroup>(e => e.HasIndex(x => x.Name).IsUnique());
        b.Entity<LocalGroupMember>(e =>
        {
            e.HasKey(x => new { x.GroupId, x.UserId });
            e.HasOne(x => x.Group).WithMany(g => g.Members).HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.User).WithMany(u => u.Groups).HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.UserId);
        });
        b.Entity<SecurityEvent>(e =>
        {
            e.HasIndex(x => x.AtUtc);
            e.HasIndex(x => x.UserName);
        });
        b.Entity<BackupRun>(e =>
        {
            e.HasIndex(x => x.ScheduledForUtc).IsUnique().HasFilter("[ScheduledForUtc] IS NOT NULL");
            e.HasIndex(x => x.StartedAtUtc);
        });
    }

    // ------------------------------------------------------------------ SSN redaction for log-type columns

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, System.Reflection.PropertyInfo[]> RedactedProps = new();

    /// <summary>Masks SSN-like numbers in every [RedactSensitive] column (webhook copies, error details, audit logs).</summary>
    private void RedactSensitiveColumns()
    {
        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified)) continue;
            var props = RedactedProps.GetOrAdd(entry.Entity.GetType(), t => t.GetProperties()
                .Where(p => p.PropertyType == typeof(string) && p.IsDefined(typeof(WebText.Web.Logging.RedactSensitiveAttribute), true))
                .ToArray());
            foreach (var p in props)
            {
                if (p.GetValue(entry.Entity) is string v && WebText.Web.Logging.SensitiveData.NeedsRedaction(v))
                    p.SetValue(entry.Entity, WebText.Web.Logging.SensitiveData.Redact(v));
            }
        }
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        RedactSensitiveColumns();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        RedactSensitiveColumns();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }
}
