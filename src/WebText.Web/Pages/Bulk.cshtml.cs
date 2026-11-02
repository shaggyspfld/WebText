using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using WebText.Web.Api;
using WebText.Web.Data;
using WebText.Web.Hubs;
using WebText.Web.Services;

namespace WebText.Web.Pages;

public class BulkModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly MessagingService _messaging;
    private readonly IHubContext<MessagingHub> _hub;
    private readonly SettingsService _settings;

    private readonly VoipMsDailyLimit _dailyLimit;
    public (int Used, int Limit, int Remaining)? DailyLimit { get; private set; }

    public BulkModel(AppDbContext db, MessagingService messaging, IHubContext<MessagingHub> hub, SettingsService settings,
        VoipMsDailyLimit dailyLimit)
    {
        _dailyLimit = dailyLimit;
        _db = db; _messaging = messaging; _hub = hub; _settings = settings;
    }

    public const int MaxRecipients = 5000;

    /// <summary>"group" = a saved recipient list; "list" = pasted account/phone numbers; "all" = every client.</summary>
    [BindProperty] public string Mode { get; set; } = "list";
    [BindProperty] public int? GroupId { get; set; }
    public List<ClientGroup> Groups { get; private set; } = new();
    [BindProperty] public string? Identifiers { get; set; }
    [BindProperty] public string? Body { get; set; }

    public List<MessageTemplate> Templates { get; private set; } = new();
    public List<Client> Recipients { get; private set; } = new();
    public List<string> Unmatched { get; } = new();
    public int OptedOutSkipped { get; private set; }
    public bool Previewed { get; private set; }
    public string? Error { get; private set; }

    public string Merge(string body, Client c) => body
        .Replace("{FirstName}", c.FirstName, StringComparison.OrdinalIgnoreCase)
        .Replace("{LastName}", c.LastName, StringComparison.OrdinalIgnoreCase)
        .Replace("{FullName}", c.DisplayName, StringComparison.OrdinalIgnoreCase);

    public TimeSpan EstimatedDuration =>
        TimeSpan.FromSeconds(Recipients.Count / Math.Max(0.1, _settings.Current.Telnyx.MessagesPerSecond));

    public async Task OnGetAsync(int? group)
    {
        await LoadTemplatesAsync();
        if (group is { } g && Groups.Any(x => x.Id == g)) { Mode = "group"; GroupId = g; }
        else if (Groups.Any()) Mode = "group";
    }

    public async Task<IActionResult> OnPostPreviewAsync()
    {
        await LoadTemplatesAsync();
        if (!Validate()) return Page();
        await ResolveAsync();
        Previewed = true;
        DailyLimit = await _dailyLimit.TodayAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostSendAsync()
    {
        await LoadTemplatesAsync();
        if (!Validate()) return Page();
        await ResolveAsync();
        if (Recipients.Count == 0) { Error = "No recipients to send to."; return Page(); }

        var batch = Guid.NewGuid();
        var user = User.UserName();
        var queued = 0;
        foreach (var c in Recipients)
        {
            var (msg, _) = await _messaging.QueueOutboundAsync(c.Id, Merge(Body!, c), user, batch, broadcast: false);
            if (msg != null) queued++;
        }
        await _hub.Clients.All.SendAsync("conversationsChanged");

        TempData["Flash"] = $"Queued {queued} text{(queued == 1 ? "" : "s")}. They'll go out over about {Humanize(EstimatedDuration)}; " +
                            "delivery status appears in each client's thread and in the message log.";
        return Redirect($"/Log?batch={batch}");
    }

    private bool Validate()
    {
        Body = Body?.Trim();
        if (string.IsNullOrEmpty(Body)) { Error = "Enter a message."; return false; }
        if (Body.Length > 1600) { Error = "Message is longer than 1600 characters."; return false; }
        if (Mode == "list" && string.IsNullOrWhiteSpace(Identifiers)) { Error = "Paste at least one account number or phone number."; return false; }
        if (Mode == "group" && GroupId == null) { Error = "Choose a recipient list."; return false; }
        return true;
    }

    private async Task ResolveAsync()
    {
        List<Client> found;
        if (Mode == "all")
        {
            found = await _db.Clients.AsNoTracking().Where(c => !c.IsAutoCreated)
                .OrderBy(c => c.LastName).ThenBy(c => c.FirstName).ToListAsync();
        }
        else if (Mode == "group")
        {
            found = await _db.Clients.AsNoTracking()
                .Where(c => c.GroupMemberships.Any(m => m.GroupId == GroupId))
                .OrderBy(c => c.LastName).ThenBy(c => c.FirstName).ToListAsync();
        }
        else
        {
            var (f, unmatched) = await ClientLookup.ResolveIdentifiersAsync(_db, Identifiers);
            found = f;
            Unmatched.AddRange(unmatched);
        }

        OptedOutSkipped = found.Count(c => c.OptedOut);
        Recipients = found.Where(c => !c.OptedOut).Take(MaxRecipients).ToList();
        if (found.Count(c => !c.OptedOut) > MaxRecipients)
            Error = $"Only the first {MaxRecipients} recipients will be used. Split larger sends into batches.";
    }

    private async Task LoadTemplatesAsync()
    {
        Templates = await _db.MessageTemplates.AsNoTracking().Where(t => t.IsActive).OrderBy(t => t.Name).ToListAsync();
        Groups = await _db.ClientGroups.AsNoTracking().OrderBy(g => g.Name).ToListAsync();
    }

    public static string Humanize(TimeSpan t) =>
        t.TotalMinutes < 1 ? $"{Math.Max(1, (int)t.TotalSeconds)} seconds"
        : t.TotalHours < 1 ? $"{(int)Math.Ceiling(t.TotalMinutes)} minutes"
        : $"{t.TotalHours:0.#} hours";
}
