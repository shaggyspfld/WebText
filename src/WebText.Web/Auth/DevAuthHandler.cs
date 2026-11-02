using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace WebText.Web.Auth;

/// <summary>
/// DEVELOPMENT ONLY. Signs every request in as a fixed user so the app can be run on a machine that
/// isn't domain-joined. Only registered when ASPNETCORE_ENVIRONMENT=Development and Auth:DevUser is set.
/// </summary>
public class DevAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "DevUser";
    private readonly IConfiguration _config;

    public DevAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger,
        UrlEncoder encoder, IConfiguration config) : base(options, logger, encoder)
    {
        _config = config;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var name = _config["Auth:DevUser"] ?? "DEV\\developer";
        // The dev user counts as a member of every group (user and admin) so all pages can be tried.
        var identity = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.Name, name),
            new Claim(GroupMembership.DevAllGroupsClaim, "true")
        }, SchemeName);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}
