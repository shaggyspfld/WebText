namespace WebText.Web.Data;

public static class SeedData
{
    /// <summary>Creates the local groups named on the Settings page (user + admin) if they don't exist yet.</summary>
    public static async Task EnsureLocalAccessGroupsAsync(AppDbContext db, WebText.Web.Services.AppSettingsSnapshot s, string by = "system")
    {
        var wanted = new[]
        {
            (Name: WebText.Web.Auth.GroupMembership.LocalGroupName(s.UserGroup), Desc: "Can use the app (Settings → Access groups → User group)"),
            (Name: WebText.Web.Auth.GroupMembership.LocalGroupName(s.AdminGroup), Desc: "Can administer the app (Settings → Access groups → Admin group)")
        };
        var existing = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(db.LocalGroups.Select(g => g.Name));
        var added = false;
        foreach (var w in wanted.DistinctBy(w => w.Name.ToLowerInvariant()))
        {
            if (existing.Any(e => string.Equals(e, w.Name, StringComparison.OrdinalIgnoreCase))) continue;
            db.LocalGroups.Add(new LocalGroup { Name = w.Name, Description = w.Desc, CreatedBy = by });
            added = true;
        }
        if (added) await db.SaveChangesAsync();
    }

    public static IEnumerable<MessageTemplate> Templates() => new[]
    {
        // Starter templates that suit any business. Admins edit or replace them on the Templates page.
        T("Information needed",
          "Hi {FirstName}, we still need a few things from you to finish up. Please reply to this text or call our office."),
        T("Ready",
          "Hi {FirstName}, good news: it's ready. Reply here or call us to arrange pickup or a time to go over it."),
        T("Status update",
          "Hi {FirstName}, a quick update: we're working on it and will let you know as soon as there's news. Reply here with any questions."),
        T("Appointment reminder",
          "Hi {FirstName}, a reminder of your upcoming appointment with us. Reply here or call if you need to reschedule."),
        T("Please call us",
          "Hi {FirstName}, please give our office a call when you have a moment. Thank you!"),
        T("Thank you",
          "Hi {FirstName}, thank you for your business! Reply here anytime if you need anything."),
    };

    private static MessageTemplate T(string name, string body) =>
        new() { Name = name, Body = body, CreatedBy = "system" };
}
