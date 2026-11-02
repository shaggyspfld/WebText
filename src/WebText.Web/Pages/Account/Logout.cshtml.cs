using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WebText.Web.Auth;

namespace WebText.Web.Pages.Account;

/// <summary>Ends a local session. (Windows sign-in has no sign-out: it follows the Windows session.)</summary>
[IgnoreAntiforgeryToken]   // signing someone out is harmless, and the access-denied page posts here without a token
public class LogoutModel : PageModel
{
    private readonly LocalAccounts _accounts;
    public LogoutModel(LocalAccounts accounts) => _accounts = accounts;

    public IActionResult OnGet() => RedirectToPage("/Account/Login");

    public async Task<IActionResult> OnPostAsync()
    {
        var name = User.IsLocalUser() ? User.Identity!.Name : null;
        await HttpContext.SignOutAsync(AuthSchemes.Local);
        if (name != null) await _accounts.LogAsync("SignOut", name, name, LocalAccounts.ClientIp(HttpContext), null);
        return Redirect("/Account/Login?signedout=1");
    }
}
