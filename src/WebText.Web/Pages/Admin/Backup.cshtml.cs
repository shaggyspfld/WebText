using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using WebText.Web.Api;
using WebText.Web.Auth;
using WebText.Web.Data;
using WebText.Web.Services;

namespace WebText.Web.Pages.Admin;

/// <summary>Backup and restore (admin-only by the /Admin folder convention). Admins signed in locally confirm with a code.</summary>
[RequestSizeLimit(MaxUpload)]
[RequestFormLimits(MultipartBodyLengthLimit = MaxUpload)]
public class BackupModel : PageModel
{
    public const long MaxUpload = 500L * 1024 * 1024;

    private readonly BackupService _backup;
    private readonly LocalAccounts _accounts;
    private readonly AppDbContext _db;
    private readonly SettingsService _settings;
    private readonly IMemoryCache _cache;

    private readonly BackupRunner _runner;
    private readonly AppInstance _instance;
    private readonly TimeDisplay _time;

    public BackupSchedule Schedule => _settings.Current.Backup;
    public DateTime? NextRunLocal { get; private set; }
    public List<BackupRun> Runs { get; private set; } = new();

    public BackupModel(BackupService backup, LocalAccounts accounts, AppDbContext db, SettingsService settings, IMemoryCache cache,
        BackupRunner runner, TimeDisplay time, AppInstance instance)
    {
        _instance = instance;
        _runner = runner; _time = time;
        _backup = backup; _accounts = accounts; _db = db; _settings = settings; _cache = cache;
    }

    public bool NeedCode { get; private set; }
    public Dictionary<string, string> Errors { get; } = new();
    public int ClientCount { get; private set; }
    public int MessageCount { get; private set; }
    public int LocalUserCount { get; private set; }
    public string CurrentSignIn => _settings.Current.LocalSignIn ? "Local" : "Windows";
    public BackupService.RestoreResult? Restored { get; private set; }

    [BindProperty] public string? BackupPassword { get; set; }
    [BindProperty] public string? ConfirmPassword { get; set; }
    [BindProperty] public bool IncludeHistory { get; set; }
    [BindProperty] public bool IncludeLogs { get; set; }
    [BindProperty] public string? AdminCode { get; set; }

    [BindProperty] public IFormFile? BackupFile { get; set; }
    [BindProperty] public string? RestorePassword { get; set; }
    [BindProperty] public bool RestoreHistory { get; set; } = true;
    [BindProperty] public bool RestoreLogs { get; set; } = true;
    [BindProperty] public bool KeepSignInMethod { get; set; } = true;
    [BindProperty] public string? Confirm { get; set; }

    private string? Ip => LocalAccounts.ClientIp(HttpContext);

    public async Task OnGetAsync()
    {
        await LoadAsync();
        if (TempData["RestoreResult"] is string json) Restored = JsonSerializer.Deserialize<BackupService.RestoreResult>(json);
    }

    public async Task<IActionResult> OnPostCreateAsync()
    {
        if (string.IsNullOrEmpty(BackupPassword) || BackupPassword.Length < BackupService.MinPasswordLength)
            Errors["create"] = $"Choose a backup password of at least {BackupService.MinPasswordLength} characters.";
        else if (BackupPassword != ConfirmPassword)
            Errors["create"] = "The two passwords don't match.";
        else if (!await StepUpAsync()) Errors["create"] = StepUpError!;
        if (Errors.Count > 0) { await LoadAsync(); return Page(); }

        var (file, summary) = await _backup.CreateAsync(BackupPassword!, IncludeHistory, IncludeLogs, User.UserName(), HttpContext.RequestAborted);
        await _accounts.LogAsync("BackupCreated", null, User.UserName(), Ip,
            $"{summary.Clients} clients, {summary.LocalUsers} local users, {summary.Templates} templates, {summary.AutoReplies} auto-replies" +
            (summary.Messages is { } m ? $", {m} texts" : ", no text history") + (summary.IncludesLogs ? ", with logs" : "") +
            (summary.SecretsNotIncluded.Count > 0 ? $"\nNot included (unreadable here): {string.Join(", ", summary.SecretsNotIncluded)}" : ""));
        Response.Headers.CacheControl = "no-store";
        return File(file, "application/octet-stream", $"{_instance.Slug}-backup-{DateTime.Now:yyyyMMdd-HHmm}{BackupService.FileExtension}");
    }

    /// <summary>AJAX: decrypt the chosen file and say what's in it, without changing anything.</summary>
    public async Task<IActionResult> OnPostInspectAsync()
    {
        try
        {
            var f = BackupService.Read(await ReadUploadAsync(), RestorePassword ?? "");
            var s = BackupService.Summarize(f);
            return new JsonResult(new
            {
                ok = true,
                created = s.CreatedAtUtc, s.CreatedBy, s.SourceServer, s.Settings, s.LocalUsers, s.LocalGroups, s.Clients, s.Lists,
                s.Templates, s.AutoReplies, s.Messages, s.IncludesLogs, s.SecretsNotIncluded, s.SignInMethod, s.Provider
            });
        }
        catch (BackupException ex) { return new JsonResult(new { ok = false, error = ex.Message }); }
    }

    public async Task<IActionResult> OnPostRestoreAsync()
    {
        if (!string.Equals(Confirm?.Trim(), "REPLACE", StringComparison.Ordinal))
            Errors["restore"] = "Type REPLACE (in capitals) to confirm that everything on this server will be replaced.";
        else if (!await StepUpAsync()) Errors["restore"] = StepUpError!;
        if (Errors.Count > 0) { await LoadAsync(); return Page(); }

        BackupService.RestoreResult result;
        BackupService.BackupFile file;
        try
        {
            file = BackupService.Read(await ReadUploadAsync(), RestorePassword ?? "");
            result = await _backup.RestoreAsync(file, new BackupService.RestoreOptions(RestoreHistory, RestoreLogs, KeepSignInMethod),
                User.UserName(), HttpContext.RequestAborted);
        }
        catch (BackupException ex)
        {
            Errors["restore"] = ex.Message;
            await LoadAsync();
            return Page();
        }

        LocalSessionCache.ForgetAll(_cache, _db);
        await _accounts.LogAsync("BackupRestored", null, User.UserName(), Ip,
            $"Backup made {file.CreatedAtUtc:yyyy-MM-dd HH:mm} UTC on {file.SourceServer} by {file.CreatedBy}\n" +
            $"{result.Clients} clients, {result.Users} local users, {result.Messages} texts restored" +
            (KeepSignInMethod ? "; kept this server's sign-in method" : "; sign-in method taken from the backup"));

        // A local admin's own account was just replaced by the one in the backup, so they sign in again.
        if (User.IsLocalUser() || _settings.Current.LocalSignIn)
        {
            await HttpContext.SignOutAsync(AuthSchemes.Local);
            return Redirect("/Account/Login?restored=1");
        }
        TempData["RestoreResult"] = JsonSerializer.Serialize(result);
        return RedirectToPage();
    }

    /// <summary>Runs a backup to the folder on the Settings page now (a good test of the folder and permissions).</summary>
    public async Task<IActionResult> OnPostRunNowAsync()
    {
        if (!await StepUpAsync()) { Errors["schedule"] = StepUpError!; await LoadAsync(); return Page(); }
        var run = await _runner.RunNowAsync(User.UserName(), HttpContext.RequestAborted);
        if (run.Success == true) TempData["Flash"] = $"Backup saved to {run.FilePath} ({(run.SizeBytes ?? 0) / 1024:N0} KB).";
        else TempData["FlashError"] = $"Backup failed: {run.Error}";
        return RedirectToPage();
    }

    // ---------------------------------------------------------------- helpers

    private async Task<byte[]> ReadUploadAsync()
    {
        if (BackupFile == null || BackupFile.Length == 0) throw new BackupException("Choose a backup file.");
        if (BackupFile.Length > MaxUpload) throw new BackupException("That file is too large.");
        using var ms = new MemoryStream((int)BackupFile.Length);
        await BackupFile.CopyToAsync(ms, HttpContext.RequestAborted);
        return ms.ToArray();
    }

    private string? StepUpError;

    private async Task<bool> StepUpAsync()
    {
        var me = await _accounts.CurrentAsync(User);
        if (me == null || _accounts.RecentlyVerified(me)) return true;
        if (await _accounts.VerifyCodeAsync(me, AdminCode, Ip))
        {
            _accounts.MarkRecentlyVerified(me);
            return true;
        }
        StepUpError = _accounts.IsLocked(me) ? "Too many failed attempts: your account is now locked."
            : "Enter the current code from your authenticator app.";
        return false;
    }

    private async Task LoadAsync()
    {
        ClientCount = await _db.Clients.CountAsync();
        MessageCount = await _db.Messages.CountAsync();
        LocalUserCount = await _db.LocalUsers.CountAsync();
        Runs = await _db.BackupRuns.AsNoTracking().OrderByDescending(r => r.StartedAtUtc).Take(20).ToListAsync();
        NextRunLocal = Schedule.Enabled ? Schedule.NextAfter(_time.ToLocal(DateTime.UtcNow)) : null;
        var me = await _accounts.CurrentAsync(User);
        NeedCode = me != null && !_accounts.RecentlyVerified(me);
    }
}
