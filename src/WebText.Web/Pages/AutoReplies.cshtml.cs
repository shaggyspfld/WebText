using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using WebText.Web.Api;
using WebText.Web.Auth;
using WebText.Web.Data;
using WebText.Web.Services;

namespace WebText.Web.Pages;

/// <summary>Keyword auto-replies. Anyone who can use the app (txtAppUser or txtAppAdmin) can view and change them.</summary>
public class AutoRepliesModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly GroupMembership _groups;
    private readonly SettingsService _settings;
    private readonly TimeDisplay _time;

    public AutoRepliesModel(AppDbContext db, GroupMembership groups, SettingsService settings, TimeDisplay time)
    {
        _db = db; _groups = groups; _settings = settings; _time = time;
    }

    public bool CanEdit => _groups.IsUser(User);   // IsUser includes admins
    public bool Enabled => _settings.Current.AutoRepliesEnabled;
    public int MaxPerHour => _settings.Current.AutoReplyMaxPerClientPerHour;

    public List<AutoResponder> Items { get; private set; } = new();
    public List<AutoResponderChange> History { get; private set; } = new();

    /// <summary>Per keyword: how many auto-replies were sent, and to how many different phone numbers.</summary>
    public Dictionary<string, (int Times, int Numbers)> Usage { get; private set; } = new();
    public (int Times, int Numbers) UsageFor(string keyword) =>
        Usage.TryGetValue(AutoReplies.SenderName(keyword), out var u) ? u : (0, 0);
    [BindProperty(SupportsGet = true)] public string? HistoryKeyword { get; set; }
    [BindProperty(SupportsGet = true)] public bool AllHistory { get; set; }
    public Dictionary<string, string> Errors { get; } = new();

    // form
    [BindProperty] public int? EditId { get; set; }
    [BindProperty] public string? Keyword { get; set; }
    [BindProperty] public KeywordMatch Match { get; set; } = KeywordMatch.Exact;
    [BindProperty] public string? Reply { get; set; }
    [BindProperty] public bool IsActive { get; set; } = true;
    [BindProperty] public bool MarkHandled { get; set; } = true;
    [BindProperty] public int CooldownMinutes { get; set; } = 5;
    [BindProperty] public string? Notes { get; set; }

    // tester
    [BindProperty(SupportsGet = true)] public string? TestText { get; set; }
    public AutoResponder? TestMatch { get; private set; }
    public bool Tested { get; private set; }

    public async Task OnGetAsync(int? edit)
    {
        await LoadAsync();
        if (edit is { } id && Items.FirstOrDefault(x => x.Id == id) is { } r)
        {
            EditId = r.Id; Keyword = r.Keyword; Match = r.Match; Reply = r.Reply; IsActive = r.IsActive;
            MarkHandled = r.MarkHandled; CooldownMinutes = r.CooldownMinutes; Notes = r.Notes;
        }
        if (!string.IsNullOrWhiteSpace(TestText))
        {
            Tested = true;
            TestMatch = AutoReplies.FindMatch(Items, TestText);
        }
    }

    public async Task<IActionResult> OnPostSaveAsync()
    {
        if (!CanEdit) return Forbid();

        var keyword = AutoReplies.NormalizeKeyword(Keyword);
        if (keyword == null) Errors[nameof(Keyword)] = "Enter a keyword (letters or numbers, up to 50 characters).";
        else if (AutoReplies.ReservedKeywords.Contains(keyword))
            Errors[nameof(Keyword)] = $"{keyword} is reserved: STOP/START change a client's opt-out status and HELP is a standard carrier keyword.";
        else if (await _db.AutoResponders.AnyAsync(r => r.Keyword == keyword && r.Id != (EditId ?? 0)))
            Errors[nameof(Keyword)] = $"There's already an auto-reply for {keyword}.";

        Reply = Reply?.Trim();
        if (string.IsNullOrEmpty(Reply)) Errors[nameof(Reply)] = "Enter the reply text.";
        else if (Reply.Length > 1600) Errors[nameof(Reply)] = "The reply is longer than 1600 characters.";
        if (CooldownMinutes < 0 || CooldownMinutes > 10080) Errors[nameof(CooldownMinutes)] = "Enter 0 to 10080 minutes (one week).";
        if (!Enum.IsDefined(Match)) Errors[nameof(Match)] = "Choose how to match.";

        if (Errors.Count > 0) { await LoadAsync(); return Page(); }

        AutoResponder? r = null;
        if (EditId is { } id) r = await _db.AutoResponders.FindAsync(id);
        var user = User.UserName();
        var before = r == null ? null : Describe(r);
        if (r == null)
        {
            r = new AutoResponder { CreatedBy = user };
            _db.AutoResponders.Add(r);
        }
        else
        {
            r.UpdatedAtUtc = DateTime.UtcNow;
            r.UpdatedBy = user;
        }
        r.Keyword = keyword!;
        r.Match = Match;
        r.Reply = Reply!;
        r.IsActive = IsActive;
        r.MarkHandled = MarkHandled;
        r.CooldownMinutes = CooldownMinutes;
        r.Notes = string.IsNullOrWhiteSpace(Notes) ? null : Notes.Trim()[..Math.Min(500, Notes.Trim().Length)];

        var after = Describe(r);
        if (before == null)
        {
            await _db.SaveChangesAsync();   // assigns r.Id
            _db.AutoResponderChanges.Add(Audit(r, AutoResponderAction.Created, Format(after), user));
        }
        else
        {
            var diff = Diff(before, after);
            if (diff == null) { TempData["Flash"] = $"No changes to {r.Keyword}."; return RedirectToPage(); }
            _db.AutoResponderChanges.Add(Audit(r, AutoResponderAction.Changed, diff, user));
        }
        await _db.SaveChangesAsync();   // the change and its audit row are saved together

        TempData["Flash"] = $"Saved the auto-reply for {r.Keyword}.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostToggleAsync(int id)
    {
        if (!CanEdit) return Forbid();
        var r = await _db.AutoResponders.FindAsync(id);
        if (r != null)
        {
            r.IsActive = !r.IsActive;
            r.UpdatedAtUtc = DateTime.UtcNow;
            r.UpdatedBy = User.UserName();
            _db.AutoResponderChanges.Add(Audit(r, r.IsActive ? AutoResponderAction.TurnedOn : AutoResponderAction.TurnedOff, null, r.UpdatedBy));
            await _db.SaveChangesAsync();
            TempData["Flash"] = $"{r.Keyword} auto-reply {(r.IsActive ? "turned on" : "turned off")}.";
        }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        if (!CanEdit) return Forbid();
        var r = await _db.AutoResponders.FindAsync(id);
        if (r != null)
        {
            _db.AutoResponders.Remove(r);   // texts already sent stay in each client's history
            // Keep a full copy of what was deleted, so it can be recreated if it was a mistake.
            _db.AutoResponderChanges.Add(Audit(r, AutoResponderAction.Deleted,
                Format(Describe(r)) + $"\nUsed {r.TimesUsed} time(s)", User.UserName()));
            await _db.SaveChangesAsync();
            TempData["Flash"] = $"Deleted the auto-reply for {r.Keyword}.";
        }
        return RedirectToPage();
    }

    private async Task LoadAsync()
    {
        Items = await _db.AutoResponders.AsNoTracking()
            .OrderByDescending(r => r.IsActive).ThenBy(r => r.Keyword).ToListAsync();

        // Counted from the message history, so it matches what's in clients' threads and the message log.
        // One row per (keyword, number) pair keeps this small even with thousands of replies.
        var pairs = await _db.Messages.AsNoTracking()
            .Where(m => m.Direction == MessageDirection.Outbound && m.SentBy != null && m.SentBy.StartsWith(AutoReplies.SenderPrefix))
            .GroupBy(m => new { m.SentBy, Number = m.ToNumber ?? m.Client.PhoneE164 })
            .Select(g => new { g.Key.SentBy, g.Key.Number, Count = g.Count() })
            .ToListAsync();
        Usage = pairs.GroupBy(p => p.SentBy!)
            .ToDictionary(g => g.Key, g => (g.Sum(p => p.Count), g.Select(p => p.Number).Distinct().Count()));

        var q = _db.AutoResponderChanges.AsNoTracking();
        var kw = AutoReplies.NormalizeKeyword(HistoryKeyword);
        if (kw != null) q = q.Where(c => c.Keyword == kw);
        History = await q.OrderByDescending(c => c.ChangedAtUtc).ThenByDescending(c => c.Id)
            .Take(AllHistory ? 1000 : 25).ToListAsync();
    }

    /// <summary>CSV of the whole audit trail (or one keyword's), for records.</summary>
    public async Task<IActionResult> OnGetHistoryCsvAsync()
    {
        var q = _db.AutoResponderChanges.AsNoTracking();
        var kw = AutoReplies.NormalizeKeyword(HistoryKeyword);
        if (kw != null) q = q.Where(c => c.Keyword == kw);
        var rows = await q.OrderByDescending(c => c.ChangedAtUtc).ThenByDescending(c => c.Id).ToListAsync();
        var sb = new System.Text.StringBuilder("When,Who,Keyword,Action,Details\r\n");
        static string Csv(string? v)
        {
            v ??= "";
            if (v.Length > 0 && "=+-@\t\r".Contains(v[0])) v = "'" + v;   // keep Excel from running it as a formula
            return "\"" + v.Replace("\"", "\"\"") + "\"";
        }
        foreach (var c in rows)
            sb.Append(Csv(_time.Local(c.ChangedAtUtc, "yyyy-MM-dd HH:mm:ss"))).Append(',').Append(Csv(c.ChangedBy)).Append(',')
              .Append(Csv(c.Keyword)).Append(',').Append(Csv(ActionLabel(c.Action))).Append(',').Append(Csv(c.Details)).Append("\r\n");
        var name = kw == null ? "auto-reply-changes" : $"auto-reply-changes-{kw.Replace(' ', '-')}";
        return File(System.Text.Encoding.UTF8.GetPreamble().Concat(System.Text.Encoding.UTF8.GetBytes(sb.ToString())).ToArray(),
            "text/csv", $"{name}-{DateTime.Now:yyyyMMdd}.csv");
    }

    // ---- audit helpers ----

    private static AutoResponderChange Audit(AutoResponder r, AutoResponderAction action, string? details, string user) => new()
    {
        AutoResponderId = r.Id, Keyword = r.Keyword, Action = action, Details = details, ChangedBy = user, ChangedAtUtc = DateTime.UtcNow
    };

    /// <summary>The settings that matter, as label → display value, in a fixed order.</summary>
    private static List<(string Label, string Value)> Describe(AutoResponder r) => new()
    {
        ("Keyword", r.Keyword),
        ("Matches", MatchLabel(r.Match)),
        ("Reply", r.Reply),
        ("On", r.IsActive ? "yes" : "no"),
        ("Mark handled", r.MarkHandled ? "yes" : "no"),
        ("Cooldown", $"{r.CooldownMinutes} min"),
        ("Notes", r.Notes ?? ""),
    };

    private static string Format(List<(string Label, string Value)> d) =>
        string.Join("\n", d.Select(x => $"{x.Label}: {Show(x.Value)}"));

    /// <summary>Only the settings that changed, as "Label: old → new". Null if nothing changed.</summary>
    internal static string? Diff(List<(string Label, string Value)> before, List<(string Label, string Value)> after)
    {
        var lines = before.Zip(after)
            .Where(p => !string.Equals(p.First.Value, p.Second.Value, StringComparison.Ordinal))
            .Select(p => $"{p.First.Label}: {Show(p.First.Value)} → {Show(p.Second.Value)}")
            .ToList();
        return lines.Count == 0 ? null : string.Join("\n", lines);
    }

    private static string Show(string v) => v.Length == 0 ? "(blank)" : $"\"{v}\"";

    public static string ActionLabel(AutoResponderAction a) => a switch
    {
        AutoResponderAction.Created => "Added",
        AutoResponderAction.Changed => "Changed",
        AutoResponderAction.TurnedOn => "Turned on",
        AutoResponderAction.TurnedOff => "Turned off",
        AutoResponderAction.Deleted => "Deleted",
        _ => a.ToString()
    };

    public static string MatchLabel(KeywordMatch m) => m switch
    {
        KeywordMatch.Exact => "Whole text",
        KeywordMatch.FirstWord => "Starts with",
        KeywordMatch.Anywhere => "Anywhere",
        _ => m.ToString()
    };
}
