using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using WebText.Web.Api;
using WebText.Web.Data;
using WebText.Web.Services;

namespace WebText.Web.Pages.Clients;

public class ImportModel : PageModel
{
    private readonly AppDbContext _db;
    public ImportModel(AppDbContext db) => _db = db;

    [BindProperty] public IFormFile? Upload { get; set; }

    public int Added { get; private set; }
    public int Updated { get; private set; }
    public List<string> Problems { get; } = new();
    public bool Done { get; private set; }

    private static readonly string[] FirstCols = { "firstname", "first", "fname", "givenname" };
    private static readonly string[] LastCols = { "lastname", "last", "lname", "surname", "familyname" };
    private static readonly string[] NameCols = { "name", "fullname", "clientname" };
    private static readonly string[] PhoneCols = { "mobile", "mobilephone", "cell", "cellphone", "phone", "phonenumber", "sms" };
    private static readonly string[] EmailCols = { "email", "emailaddress" };
    private static readonly string[] AcctCols = { "account", "accountnumber", "accountno", "acct", "clientid", "clientnumber", "clientno", "id" };

    public void OnGet() { }

    public async Task<IActionResult> OnPostAsync()
    {
        Done = true;
        if (Upload == null || Upload.Length == 0) { Problems.Add("Choose a CSV file."); return Page(); }
        if (Upload.Length > 10 * 1024 * 1024) { Problems.Add("File is larger than 10 MB."); return Page(); }

        string text;
        using (var reader = new StreamReader(Upload.OpenReadStream(), detectEncodingFromByteOrderMarks: true))
            text = await reader.ReadToEndAsync();

        var rows = Csv.Parse(text);
        if (rows.Count < 2) { Problems.Add("The file needs a header row and at least one client."); return Page(); }

        var header = rows[0].Select(h => new string(h.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray())).ToArray();
        int Col(string[] names) { foreach (var n in names) { var i = Array.IndexOf(header, n); if (i >= 0) return i; } return -1; }

        int first = Col(FirstCols), last = Col(LastCols), name = Col(NameCols), phone = Col(PhoneCols),
            email = Col(EmailCols), acct = Col(AcctCols);

        if (phone < 0) { Problems.Add("No phone column found. Name it Mobile, Cell or Phone."); return Page(); }
        if (first < 0 && name < 0) { Problems.Add("No name column found. Use FirstName/LastName or Name."); return Page(); }

        var user = User.UserName();
        var existing = await _db.Clients.ToListAsync();
        var byAcct = existing.Where(c => c.AccountNumber != null)
                             .GroupBy(c => c.AccountNumber!, StringComparer.OrdinalIgnoreCase)
                             .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        string Get(string[] r, int i) => i >= 0 && i < r.Length ? r[i].Trim() : "";

        for (var rowNo = 1; rowNo < rows.Count; rowNo++)
        {
            var r = rows[rowNo];
            string fn = Get(r, first), ln = Get(r, last);
            if (first < 0)
            {
                var full = Get(r, name);
                if (full.Contains(','))
                {   // "Last, First"
                    var parts = full.Split(',', 2);
                    ln = parts[0].Trim(); fn = parts[1].Trim();
                }
                else
                {
                    var sp = full.LastIndexOf(' ');
                    fn = sp > 0 ? full[..sp] : full; ln = sp > 0 ? full[(sp + 1)..] : "";
                }
            }

            var e164 = PhoneNumber.ToE164(Get(r, phone));
            if (e164 == null) { Problems.Add($"Row {rowNo + 1}: skipped, invalid phone \"{Get(r, phone)}\"."); continue; }
            if (string.IsNullOrWhiteSpace(fn) && string.IsNullOrWhiteSpace(ln)) { Problems.Add($"Row {rowNo + 1}: skipped, no name."); continue; }

            var account = Get(r, acct);
            var mail = Get(r, email);

            Client? c = null;
            if (account.Length > 0) byAcct.TryGetValue(account, out c);
            c ??= existing.FirstOrDefault(x => x.PhoneE164 == e164 &&
                    string.Equals(x.FirstName, fn, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(x.LastName, ln, StringComparison.OrdinalIgnoreCase));
            // An auto-created "Unknown" record for this number gets the real name.
            c ??= existing.FirstOrDefault(x => x.PhoneE164 == e164 && x.IsAutoCreated);

            if (c == null)
            {
                c = new Client { CreatedBy = user };
                _db.Clients.Add(c);
                existing.Add(c);
                Added++;
            }
            else
            {
                c.UpdatedAtUtc = DateTime.UtcNow;
                c.UpdatedBy = user;
                Updated++;
            }

            c.FirstName = Trunc(fn, 100);
            c.LastName = Trunc(ln, 100);
            c.PhoneE164 = e164;
            c.IsAutoCreated = false;
            if (mail.Length > 0) c.Email = Trunc(mail, 200);
            if (account.Length > 0) { c.AccountNumber = Trunc(account, 50); byAcct[account] = c; }
        }

        await _db.SaveChangesAsync();
        return Page();
    }

    private static string Trunc(string s, int n) => s.Length > n ? s[..n] : s;
}
