using System.Globalization;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.Extensions.Caching.Memory;
using WebText.Web.Api;
using WebText.Web.Services;

namespace WebText.Web.Auth;

/// <summary>
/// Two ways to sign in, chosen on the Settings page (no restart):
///  - Windows: Active Directory via IIS Windows Authentication / Negotiate (Kerberos/NTLM).
///  - Local:   user name + password + authenticator code, with accounts on the Users page (cookie session).
/// The default scheme is a "policy" scheme that forwards each request to whichever one is selected.
/// </summary>
public static class AuthSetup
{
    public const string SignInRateLimit = "sign-in";

    public static IServiceCollection AddWebTextAuthentication(this IServiceCollection services, bool useDevUser, bool isDevelopment,
        AppInstance instance)
    {
        var windowsScheme = useDevUser ? DevAuthHandler.SchemeName : NegotiateDefaults.AuthenticationScheme;

        var auth = services.AddAuthentication(AuthSchemes.Selector)
            .AddPolicyScheme(AuthSchemes.Selector, "Windows or local sign-in", o =>
            {
                o.ForwardDefaultSelector = ctx =>
                    ctx.RequestServices.GetRequiredService<SettingsService>().Current.LocalSignIn ? AuthSchemes.Local : windowsScheme;
            })
            .AddCookie(AuthSchemes.Local, o =>
            {
                o.Cookie.Name = instance.Name + ".Session";
                o.Cookie.HttpOnly = true;
                o.Cookie.SameSite = SameSiteMode.Lax;
                o.Cookie.SecurePolicy = isDevelopment ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
                o.LoginPath = "/Account/Login";
                o.LogoutPath = "/Account/Logout";
                o.SlidingExpiration = true;          // each session's idle limit is set at sign-in (Settings → Sign-in)
                o.ExpireTimeSpan = TimeSpan.FromHours(1);
                o.Events = new CookieAuthenticationEvents
                {
                    OnRedirectToLogin = ctx =>
                    {
                        if (IsApi(ctx.Request))
                        {
                            // Not 401: under IIS a 401 gets a Windows "Negotiate" challenge added, and the browser could pop
                            // up a Windows password box. The page script sees this header and goes to the sign-in page.
                            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                            ctx.Response.Headers["X-WebText-SignIn"] = "required";
                        }
                        else ctx.Response.Redirect(ctx.RedirectUri);
                        return Task.CompletedTask;
                    },
                    // Use the app's own "access denied" page (UseStatusCodePages in Program.cs).
                    OnRedirectToAccessDenied = ctx =>
                    {
                        ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                        return Task.CompletedTask;
                    },
                    OnValidatePrincipal = ValidateSessionAsync
                };
            });

        if (useDevUser)
            auth.AddScheme<AuthenticationSchemeOptions, DevAuthHandler>(DevAuthHandler.SchemeName, null);
        else
            auth.AddNegotiate();   // under IIS this defers to IIS's Windows Authentication

        services.AddScoped<LocalAccounts>();

        // Slow down password guessing from one address (per-account lockout is the main protection).
        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.AddPolicy(SignInRateLimit, ctx => RateLimitPartition.GetFixedWindowLimiter(
                CallerInfo.ClientIp(ctx)?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        });
        return services;
    }

    private static bool IsApi(HttpRequest r) => r.Path.StartsWithSegments("/api") || r.Path.StartsWithSegments("/hubs");

    /// <summary>
    /// Runs on every request with a local session: ends it if the account was disabled, deleted, its password or
    /// authenticator changed, or it has passed the maximum session length.
    /// </summary>
    private static Task ValidateSessionAsync(CookieValidatePrincipalContext ctx)
    {
        var services = ctx.HttpContext.RequestServices;
        var settings = services.GetRequiredService<SettingsService>().Current;
        var principal = ctx.Principal;
        var ok = principal?.LocalUserId() is { } id;
        if (ok)
        {
            var state = LocalSessionCache.Get(services.GetRequiredService<IMemoryCache>(),
                services.GetRequiredService<IServiceScopeFactory>(), principal!.LocalUserId()!.Value);
            ok = state is { Active: true } && state.Stamp == principal!.FindFirstValue(LocalClaims.Stamp);
            if (ok && DateTime.TryParse(principal!.FindFirstValue(LocalClaims.SignedInAt), CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var at))
                ok = DateTime.UtcNow - at.ToUniversalTime() <= settings.MaxSession;
        }
        if (!ok)
        {
            ctx.RejectPrincipal();
            return ctx.HttpContext.SignOutAsync(AuthSchemes.Local);
        }
        return Task.CompletedTask;
    }

    /// <summary>Signs a local user in with the idle limit from Settings.</summary>
    public static Task SignInLocalAsync(this HttpContext http, ClaimsPrincipal principal)
    {
        var idle = http.RequestServices.GetRequiredService<SettingsService>().Current.SessionIdle;
        var now = DateTimeOffset.UtcNow;
        return http.SignInAsync(AuthSchemes.Local, principal, new AuthenticationProperties
        {
            IsPersistent = false,       // browser-session cookie: closing the browser signs out
            IssuedUtc = now,
            ExpiresUtc = now + idle,    // sliding: renewed while they keep using the app
            AllowRefresh = true
        });
    }

    /// <summary>
    /// With Local sign-in selected, ignore any Windows identity IIS attached on its own (a browser can send Windows
    /// credentials unprompted on an intranet). Only a local session counts.
    /// </summary>
    public static IApplicationBuilder UseLocalSignInOnly(this IApplicationBuilder app) => app.Use((ctx, next) =>
    {
        if (ctx.User.Identity?.IsAuthenticated == true &&
            ctx.User.Identity.AuthenticationType != AuthSchemes.Local &&
            ctx.RequestServices.GetRequiredService<SettingsService>().Current.LocalSignIn)
        {
            ctx.User = new ClaimsPrincipal(new ClaimsIdentity());
        }
        return next(ctx);
    });
}
