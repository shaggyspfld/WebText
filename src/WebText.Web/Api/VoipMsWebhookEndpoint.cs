using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WebText.Web.Services;

namespace WebText.Web.Api;

public static class VoipMsWebhookEndpoint
{
    /// <summary>
    /// GET /api/webhooks/voipms?key={secret}&amp;to={TO}&amp;from={FROM}&amp;message={MESSAGE}&amp;id={ID}&amp;date={TIMESTAMP}&amp;media={MEDIA}
    /// Set as the DID's "SMS URL Callback" in the VoIP.ms portal (the Settings page shows the exact URL). VoIP.ms doesn't
    /// sign its callbacks, so the random key in the query string proves the request came from your account. Must answer "ok",
    /// or VoIP.ms re-sends the text every 30 minutes (if "URL Callback Retry" is on).
    /// </summary>
    public static void MapVoipMsWebhook(this IEndpointRouteBuilder app)
    {
        // The key is in the query string, not the path, because IIS logs the path but the installer turns off
        // query-string logging (the query also holds the client's message text).
        app.MapMethods(CallerInfo.VoipMsWebhookPrefix, new[] { "GET", "POST" }, async (HttpContext ctx,
                MessagingService messaging, SettingsService settings, WebhookGuard guard, ILoggerFactory lf) =>
        {
            var log = lf.CreateLogger("VoipMsWebhook");
            var s = settings.Current;
            var caller = CallerInfo.ClientIp(ctx);

            // 1) The secret key (compared in constant time). A wrong key gets a plain 404, like any unknown URL.
            var key = ctx.Request.Query["key"].FirstOrDefault() ?? "";
            var expected = s.VoipMs.CallbackToken;
            if (string.IsNullOrEmpty(expected) ||
                !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(expected)))
            {
                guard.Rejected(caller, "VoIP.ms callback with a wrong or missing key");
                return Results.NotFound();
            }

            // 2) Optional source-IP allowlist (Settings → VoIP.ms → Advanced).
            var allowed = s.VoipMsAllowedNetworks;
            var direct = CallerInfo.IsLoopback(CallerInfo.Peer(ctx)) && !CallerInfo.ViaTunnel(ctx);
            if (allowed != null && !direct && (caller == null || !allowed.Any(n => n.Contains(caller))))
            {
                log.LogWarning("Rejected VoIP.ms callback from {Ip}: not in the allowed IP ranges", caller);
                guard.Rejected(caller, "VoIP.ms callback from an IP address not in the allowed ranges");
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }
            guard.Accepted("VoIP.ms", caller);

            // VoIP.ms fills the placeholders into the query string; accept a form POST too.
            string? Get(string name)
            {
                var v = ctx.Request.Query[name].FirstOrDefault();
                if (v == null && ctx.Request.HasFormContentType) v = ctx.Request.Form[name].FirstOrDefault();
                return string.IsNullOrWhiteSpace(v) || v.StartsWith('{') && v.EndsWith('}') ? null : v;   // unreplaced {PLACEHOLDER}
            }
            var from = Get("from");
            if (string.IsNullOrWhiteSpace(from))
            {
                // e.g. someone opening the URL in a browser to test it.
                return Results.Text("ok - the callback URL works, but no text was included", "text/plain");
            }
            var media = (Get("media") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(u => Uri.TryCreate(u, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http")
                .Select(u => new InboundMedia(u, null, null)).ToList();
            var inbound = new InboundText(from, Get("to"), Get("message") ?? "", Get("id"), DateTime.UtcNow, media);

            // What's kept for troubleshooting (Settings → recent processing errors): the values, never the key.
            var raw = JsonSerializer.Serialize(new
            {
                provider = "voipms", from, to = Get("to"), id = Get("id"), date = Get("date"),
                message = inbound.Text, media = Get("media"), ip = caller?.ToString()
            });
            await messaging.HandleVoipMsCallbackAsync(inbound, raw, CancellationToken.None);
            return Results.Text("ok", "text/plain");
        })
        .AllowAnonymous()
        .DisableAntiforgery();
    }
}
