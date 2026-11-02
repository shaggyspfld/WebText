using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using WebText.Web.Api;
using WebText.Web.Auth;
using WebText.Web.Data;
using WebText.Web.Hubs;
using WebText.Web.Logging;
using WebText.Web.Services;

// Installer mode: build/upgrade the database and exit (see Data/DatabaseSetup.cs and tools/Install-Server.ps1).
if (args.Length >= 2 && args[0] == DatabaseSetup.Arg)
    return await DatabaseSetup.RunAsync(args[1]);
// Installer helper: reads a password on stdin and prints its salted hash (so the password itself never goes in a file).
if (args.Length >= 1 && args[0] == DatabaseSetup.HashPasswordArg)
    return DatabaseSetup.HashPasswordFromStdin();

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;
// This installation's name (e.g. AcmeText), set by the installer in appsettings.json. See Services/AppInstance.cs.
var instance = AppInstance.FromConfiguration(config);
builder.Services.AddSingleton(instance);

// ---------------------------------------------------------------- data
// The database connection is the ONLY setting read from appsettings.json. Everything else (Telnyx, AD groups,
// time zone…) lives in the AppSettings table and is edited by admins on the Settings page.
var provider = config["Database:Provider"] ?? "SqlServer";
builder.Services.AddDbContext<AppDbContext>(o =>
{
    if (provider.Equals("Sqlite", StringComparison.OrdinalIgnoreCase))
        o.UseSqlite(config.GetConnectionString("Sqlite") ?? "Data Source=webtext-dev.db");
    else
        o.UseSqlServer(config.GetConnectionString("WebText"), sql => sql.EnableRetryOnFailure());
});

// ---------------------------------------------------------------- encryption of stored secrets
// Data Protection encrypts the Telnyx API key in the database. Its key ring is stored in SQL too, and on Windows
// the key ring itself is encrypted with the server's machine DPAPI key, so a copy of the database alone can't
// reveal the API key.
var dp = builder.Services.AddDataProtection().SetApplicationName(instance.Name);
if (OperatingSystem.IsWindows()) dp.ProtectKeysWithDpapi(protectToLocalMachine: true);
builder.Services.AddOptions<KeyManagementOptions>()
    .Configure<IServiceScopeFactory>((o, scopes) => o.XmlRepository = new DbXmlRepository(scopes));

// ---------------------------------------------------------------- authentication (Windows or local)
// Settings → Sign-in chooses Windows/AD or app-hosted accounts (password + authenticator code). See Auth/AuthSetup.cs.
var useDevUser = builder.Environment.IsDevelopment() && !string.IsNullOrWhiteSpace(config["Auth:DevUser"]);
builder.Services.AddWebTextAuthentication(useDevUser, builder.Environment.IsDevelopment(), instance);

// ---------------------------------------------------------------- authorization (AD groups)
// Every page, API call and the live-update hub require the user group (default "txtAppUser") or admin group
// (default "txtAppAdmin"). Admin-only pages use the "AppAdmin" policy. Group names are set on the Settings page.
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<GroupMembership>();
builder.Services.AddSingleton<IAuthorizationHandler, GroupRequirementHandler>();
builder.Services.AddAuthorization(o =>
{
    var userPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser()
        .AddRequirements(new GroupRequirement(AccessLevel.User)).Build();
    o.FallbackPolicy = userPolicy;   // anything not marked [AllowAnonymous]
    o.DefaultPolicy = userPolicy;    // anything marked plain [Authorize], e.g. the SignalR hub
    o.AddPolicy(Policies.User, userPolicy);
    o.AddPolicy(Policies.Admin, p => p.RequireAuthenticatedUser().AddRequirements(new GroupRequirement(AccessLevel.Admin)));
    o.AddPolicy(Policies.SignedIn, p => p.RequireAuthenticatedUser());   // own profile, even without group access
});

// ---------------------------------------------------------------- app services
builder.Services.AddSingleton<SettingsService>();
builder.Services.AddSingleton<TimeDisplay>();
builder.Services.AddSingleton<TelnyxSignatureVerifier>();
builder.Services.AddSingleton<OutboundSignal>();
builder.Services.AddSingleton<WebhookGuard>();
builder.Services.AddHttpClient<TelnyxClient>();
builder.Services.AddHttpClient<VoipMsClient>();
builder.Services.AddTransient<SmsSenders>();
builder.Services.AddScoped<VoipMsDailyLimit>();
builder.Services.AddScoped<MessagingService>();
builder.Services.AddScoped<ReportService>();
builder.Services.AddScoped<BackupService>();
builder.Services.AddScoped<BackupRunner>();
builder.Services.AddSingleton<BackupHealth>();
builder.Services.AddHostedService<ScheduledBackupWorker>();
builder.Services.AddHostedService<OutboundMessageWorker>();

builder.Services.AddRazorPages(o =>
{
    o.Conventions.AuthorizeFolder("/Admin", Policies.Admin);
    o.Conventions.AuthorizePage("/Reports", Policies.Admin);
    o.Conventions.AllowAnonymousToPage("/Account/Login");
    o.Conventions.AllowAnonymousToPage("/Account/Logout");
    o.Conventions.AuthorizePage("/Account/Profile", Policies.SignedIn);
});
builder.Services.AddSignalR();
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
});

// Every log message and exception goes through SSN redaction before any provider writes it (must come after
// all logging providers are registered).
builder.Services.AddLogRedaction();

var app = builder.Build();

// ---------------------------------------------------------------- database init
if (config.GetValue("Database:CreateIfMissing", true))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    // Normally the installer has already built the tables (with admin rights), and this is a no-op.
    // On a fresh dev machine (SQLite) it creates them.
    db.Database.EnsureCreated();
    if (!db.MessageTemplates.Any())
    {
        db.MessageTemplates.AddRange(SeedData.Templates());
        db.SaveChanges();
    }
}
// The local groups named on the Settings page must exist for Local sign-in (harmless with Windows sign-in).
try
{
    using var scope = app.Services.CreateScope();
    await SeedData.EnsureLocalAccessGroupsAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>(),
        scope.ServiceProvider.GetRequiredService<SettingsService>().Current);
}
catch (Exception ex)
{
    app.Logger.LogWarning(ex, "Couldn't check the local access groups (run the installer's database step to add the new tables)");
}

// ---------------------------------------------------------------- pipeline
// Remember the real TCP peer before UseForwardedHeaders rewrites RemoteIpAddress (used to recognise tunnel traffic).
app.Use((ctx, next) =>
{
    ctx.Items[CallerInfo.PeerIpItem] = ctx.Connection.RemoteIpAddress;
    return next(ctx);
});

// Cloudflare Tunnel: traffic from the internet may only reach the texting provider's webhook. Everything else gets a plain 404,
// so the staff app is never exposed publicly even if the tunnel or Cloudflare rules are misconfigured.
app.Use(async (ctx, next) =>
{
    if (CallerInfo.ViaTunnel(ctx) &&
        ctx.RequestServices.GetRequiredService<SettingsService>().Current.TunnelWebhookOnly &&
        !CallerInfo.IsProviderWebhook(ctx.Request))
    {
        ctx.RequestServices.GetRequiredService<WebhookGuard>()
            .Rejected(CallerInfo.ClientIp(ctx), $"Tunnel request to {ctx.Request.Method} {ctx.Request.Path} blocked");
        ctx.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }
    await next(ctx);
});

app.UseForwardedHeaders();
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

// Friendly page for signed-in Windows users who aren't in the AD group.
app.UseStatusCodePages(async ctx =>
{
    var http = ctx.HttpContext;
    if (http.Response.StatusCode != StatusCodes.Status403Forbidden) return;
    if (http.Request.Path.StartsWithSegments("/api") || http.Request.Path.StartsWithSegments("/hubs")) return;

    var s = http.RequestServices.GetRequiredService<SettingsService>().Current;
    var enc = HtmlEncoder.Default;
    var isAdminArea = http.Request.Path.StartsWithSegments("/Admin") || http.Request.Path.StartsWithSegments("/Reports");
    var needed = isAdminArea ? s.AdminGroup : s.UserGroup;
    if (s.LocalSignIn) needed = GroupMembership.LocalGroupName(needed);
    http.Response.ContentType = "text/html; charset=utf-8";
    await http.Response.WriteAsync($$"""
        <!DOCTYPE html><html lang="en"><head><meta charset="utf-8"><title>Access denied</title>
        <link rel="stylesheet" href="/css/site.css"></head>
        <body><main class="page"><div class="card" style="max-width:560px;margin:60px auto">
        <h1>Access denied</h1>
        <p>You're signed in as <b>{{enc.Encode(http.User.Identity?.Name ?? "unknown")}}</b>, which isn't a member of the
        <b>{{enc.Encode(needed)}}</b> {{(s.LocalSignIn ? "group" : "Active Directory group")}}{{(isAdminArea ? " needed for this admin page" : "")}}.</p>
        {{(s.LocalSignIn
            ? "<p>Ask an administrator to add you to the group on the Users page.</p><form method=\"post\" action=\"/Account/Logout\"><button class=\"btn\">Sign out</button></form>"
            : "<p>Ask IT to add you to the group, then sign out of Windows and back in.</p>")}}
        {{(isAdminArea ? "<p><a href=\"/\">Back to the inbox</a></p>" : "")}}
        </div></main></body></html>
        """);
});

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseLocalSignInOnly();
app.UseAuthorization();

app.MapRazorPages();
app.MapHub<MessagingHub>("/hubs/messaging");
app.MapApi();
app.MapTelnyxWebhook();   // anonymous, signature-verified
app.MapVoipMsWebhook();   // anonymous, secret key in the URL

app.Run();
return 0;
