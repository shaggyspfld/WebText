using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using WebText.Web.Auth;
using WebText.Web.Data;

namespace WebText.Web.Services;

/// <summary>
/// Writes a backup file to the folder on the Settings page (Settings → Scheduled backups), for scheduled runs and
/// "Back up now". Each run is recorded in BackupRuns, so failures are visible on the Backup &amp; restore page.
/// </summary>
public class BackupRunner
{

    private readonly AppDbContext _db;
    private readonly BackupService _backup;
    private readonly SettingsService _settings;
    private readonly LocalAccounts _accounts;
    private readonly TimeDisplay _time;
    private readonly IMemoryCache _cache;
    private readonly ILogger<BackupRunner> _log;
    private readonly AppInstance _instance;

    public BackupRunner(AppDbContext db, BackupService backup, SettingsService settings, LocalAccounts accounts, TimeDisplay time,
        IMemoryCache cache, ILogger<BackupRunner> log, AppInstance instance)
    {
        _instance = instance;
        _db = db; _backup = backup; _settings = settings; _accounts = accounts; _time = time; _cache = cache; _log = log;
    }

    /// <summary>Backup files are named after the instance, e.g. acmetext-backup-2026-11-02_2300.wtbackup.</summary>
    private string FilePrefix => _instance.Slug + "-backup";

    private Regex DatedFile() =>
        new("^" + Regex.Escape(FilePrefix) + @"-\d{4}-\d{2}-\d{2}_\d{4}(-\d+)?" + Regex.Escape(BackupService.FileExtension) + "$", RegexOptions.IgnoreCase);

    /// <summary>
    /// Claims a schedule slot (so only one web server runs it) and runs the backup. Returns false if another server
    /// already has the slot.
    /// </summary>
    public async Task<bool> TryRunScheduledAsync(DateTime slotUtc, CancellationToken ct)
    {
        var run = new BackupRun { ScheduledForUtc = slotUtc, Server = Environment.MachineName, StartedBy = "schedule" };
        _db.BackupRuns.Add(run);
        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { _db.ChangeTracker.Clear(); return false; }   // already claimed
        await ExecuteAsync(run, ct);
        return true;
    }

    /// <summary>"Back up now" on the Backup &amp; restore page, with the saved settings.</summary>
    public async Task<BackupRun> RunNowAsync(string user, CancellationToken ct)
    {
        var run = new BackupRun { Server = Environment.MachineName, StartedBy = user };
        _db.BackupRuns.Add(run);
        await _db.SaveChangesAsync(ct);
        await ExecuteAsync(run, ct);
        return run;
    }

    private async Task ExecuteAsync(BackupRun run, CancellationToken ct)
    {
        var s = _settings.Current.Backup;
        string? tempPath = null;
        try
        {
            if (string.IsNullOrEmpty(s.Password)) throw new InvalidOperationException(
                _settings.Current.UnreadableSecrets.Contains(SettingDefs.BackupPassword)
                    ? "The saved backup password can't be decrypted on this server. Enter it again on the Settings page."
                    : "No backup password is set on the Settings page.");
            var problem = Pages.Admin.SettingsModel.FolderProblem(s.Folder);
            if (problem != null) throw new InvalidOperationException(problem);

            Directory.CreateDirectory(s.Folder);
            var local = _time.ToLocal(DateTime.UtcNow);
            var name = s.Timestamp ? $"{FilePrefix}-{local:yyyy-MM-dd_HHmm}{BackupService.FileExtension}" : FilePrefix + BackupService.FileExtension;
            var path = Path.Combine(s.Folder, name);
            for (var n = 2; s.Timestamp && File.Exists(path); n++)   // two backups in the same minute
                path = Path.Combine(s.Folder, $"{FilePrefix}-{local:yyyy-MM-dd_HHmm}-{n}{BackupService.FileExtension}");

            var (bytes, summary) = await _backup.CreateAsync(s.Password, s.IncludeHistory, s.IncludeLogs,
                run.StartedBy == "schedule" ? "scheduled backup" : run.StartedBy, ct);

            // Write to a temporary name first, so a half-written file never replaces a good backup.
            tempPath = path + ".partial";
            await File.WriteAllBytesAsync(tempPath, bytes, ct);
            File.Move(tempPath, path, overwrite: true);
            tempPath = null;

            run.Success = true;
            run.FilePath = path;
            run.SizeBytes = bytes.LongLength;
            var deleted = DeleteOldBackups(s);
            await _accounts.LogAsync("BackupCreated", null, run.StartedBy == "schedule" ? "scheduled backup" : run.StartedBy, null,
                $"Saved to {path} ({bytes.LongLength / 1024:N0} KB): {summary.Clients} clients, {summary.LocalUsers} local users" +
                (summary.Messages is { } m ? $", {m} texts" : "") + (deleted > 0 ? $"\nDeleted {deleted} older backup file(s)" : ""));
            _log.LogInformation("Backup saved to {Path} ({Bytes} bytes)", path, bytes.LongLength);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            run.Success = false;
            run.Error = Explain(ex, s.Folder);
            _log.LogError(ex, "Backup to {Folder} failed", s.Folder);
            if (tempPath != null) try { File.Delete(tempPath); } catch { /* best effort */ }
        }
        run.FinishedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(CancellationToken.None);
        _cache.Remove(BackupHealth.CacheKey);
    }

    /// <summary>With dated names, keeps only the newest N backup files (only files this app named; nothing else is touched).</summary>
    private int DeleteOldBackups(BackupSchedule s)
    {
        if (!s.Timestamp || s.KeepCount <= 0) return 0;
        var old = new DirectoryInfo(s.Folder).GetFiles(FilePrefix + "-*" + BackupService.FileExtension)
            .Where(f => DatedFile().IsMatch(f.Name))
            .OrderByDescending(f => f.Name, StringComparer.OrdinalIgnoreCase)   // names sort by date
            .Skip(s.KeepCount).ToList();
        var deleted = 0;
        foreach (var f in old)
        {
            try { f.Delete(); deleted++; }
            catch (Exception ex) { _log.LogWarning(ex, "Couldn't delete old backup {File}", f.FullName); }
        }
        return deleted;
    }

    private static string Explain(Exception ex, string folder) => ex switch
    {
        UnauthorizedAccessException =>
            $"The website's account isn't allowed to write to {folder}. Give it Modify permission on the folder" +
            (folder.StartsWith(@"\\") ? " and the share (for the built-in app pool account, that's this server's computer account, DOMAIN\\SERVERNAME$)." : " (the installer's -BackupFolder option does this)."),
        DirectoryNotFoundException or IOException when folder.StartsWith(@"\\") =>
            $"Couldn't reach {folder}: {ex.Message} Check the server and share are online and the name is right.",
        IOException io => $"Couldn't write the backup: {io.Message}",
        _ => ex.Message
    };
}

/// <summary>Runs the scheduled backups. Checks once a minute; a missed slot (server off or recycling) runs within 12 hours.</summary>
public class ScheduledBackupWorker : BackgroundService
{
    private static readonly TimeSpan CatchUpWindow = TimeSpan.FromHours(12);

    private readonly IServiceScopeFactory _scopes;
    private readonly SettingsService _settings;
    private readonly TimeDisplay _time;
    private readonly ILogger<ScheduledBackupWorker> _log;

    public ScheduledBackupWorker(IServiceScopeFactory scopes, SettingsService settings, TimeDisplay time, ILogger<ScheduledBackupWorker> log)
    {
        _scopes = scopes; _settings = settings; _time = time; _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        await Task.Delay(TimeSpan.FromSeconds(30), stop);   // let the app finish starting
        while (!stop.IsCancellationRequested)
        {
            try
            {
                var s = _settings.Current.Backup;
                if (s.Enabled)
                {
                    var nowUtc = DateTime.UtcNow;
                    var slotLocal = s.LatestAtOrBefore(_time.ToLocal(nowUtc));
                    if (slotLocal is { } sl)
                    {
                        var slotUtc = _time.ToUtc(sl);
                        if (nowUtc - slotUtc <= CatchUpWindow)
                        {
                            using var scope = _scopes.CreateScope();
                            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                            if (!await db.BackupRuns.AnyAsync(r => r.ScheduledForUtc == slotUtc, stop))
                                await scope.ServiceProvider.GetRequiredService<BackupRunner>().TryRunScheduledAsync(slotUtc, stop);
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            catch (Exception ex) { _log.LogError(ex, "Scheduled backup check failed"); }
            await Task.Delay(TimeSpan.FromMinutes(1), stop);
        }
    }
}

/// <summary>Whether scheduled backups are healthy, for the warning shown to admins at the top of every page.</summary>
public class BackupHealth
{
    public const string CacheKey = "backup-health";
    private readonly IMemoryCache _cache;
    private readonly IServiceScopeFactory _scopes;
    private readonly SettingsService _settings;
    private readonly TimeDisplay _time;

    public BackupHealth(IMemoryCache cache, IServiceScopeFactory scopes, SettingsService settings, TimeDisplay time)
    {
        _cache = cache; _scopes = scopes; _settings = settings; _time = time;
    }

    /// <summary>A problem to show admins, or null if all is well (or scheduled backups are off).</summary>
    public string? Problem() => _cache.GetOrCreate(CacheKey, e =>
    {
        e.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(2);
        var s = _settings.Current.Backup;
        if (!s.Enabled) return null;
        try
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var last = db.BackupRuns.AsNoTracking().Where(r => r.ScheduledForUtc != null)
                .OrderByDescending(r => r.ScheduledForUtc).FirstOrDefault();
            if (last?.Success == false) return $"The last scheduled backup failed: {last.Error}";
            // Expected a run in the last 2 days of slots but none happened (e.g. the worker isn't running).
            var nowLocal = _time.ToLocal(DateTime.UtcNow);
            var expected = s.LatestAtOrBefore(nowLocal.AddHours(-1));
            if (expected is { } ex && (last?.ScheduledForUtc == null || last.ScheduledForUtc < _time.ToUtc(ex))
                && nowLocal - ex < TimeSpan.FromDays(8) && last?.ScheduledForUtc != null)
                return $"No scheduled backup has run since {_time.Local(last.ScheduledForUtc, "M/d h:mm tt")}.";
            return null;
        }
        catch (Exception) { return null; }
    });
}
