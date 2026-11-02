using System.Text;
using System.Text.Json;
using WebText.Web.Services;

namespace WebText.Web.Api;

public static class TelnyxWebhookEndpoint
{
    /// <summary>
    /// POST /api/webhooks/telnyx — set this as the webhook URL on your Telnyx Messaging Profile.
    /// Must be reachable from the internet over HTTPS and allow anonymous access (no Windows login);
    /// authenticity is enforced with Telnyx's Ed25519 signature instead.
    /// </summary>
    public static void MapTelnyxWebhook(this IEndpointRouteBuilder app)
    {
        app.MapPost(CallerInfo.WebhookPath, async (HttpContext ctx, TelnyxSignatureVerifier verifier,
                MessagingService messaging, SettingsService settings, WebhookGuard guard, ILoggerFactory lf) =>
        {
            var log = lf.CreateLogger("TelnyxWebhook");

            // 1) Source IP: only Telnyx's webhook ranges (checked again here even though Cloudflare's WAF filters first).
            var caller = CallerInfo.ClientIp(ctx);
            var allowed = settings.Current.WebhookAllowedNetworks;
            var direct = CallerInfo.IsLoopback(CallerInfo.Peer(ctx)) && !CallerInfo.ViaTunnel(ctx);   // from this server itself
            if (allowed != null && !direct && (caller == null || !allowed.Any(n => n.Contains(caller))))
            {
                log.LogWarning("Rejected webhook from {Ip}: not in the allowed IP ranges", caller);
                guard.Rejected(caller, "IP address not in the allowed ranges");
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            byte[] raw;
            using (var ms = new MemoryStream())
            {
                await ctx.Request.Body.CopyToAsync(ms, ctx.RequestAborted);
                raw = ms.ToArray();
            }

            var (ok, reason) = verifier.Verify(
                ctx.Request.Headers["telnyx-signature-ed25519"].FirstOrDefault(),
                ctx.Request.Headers["telnyx-timestamp"].FirstOrDefault(),
                raw);
            if (!ok)
            {
                log.LogWarning("Rejected Telnyx webhook from {Ip}: {Reason}", caller, reason);
                guard.Rejected(caller, "Signature check failed: " + reason);
                return Results.Unauthorized();
            }

            guard.Accepted("Telnyx", caller);
            var json = Encoding.UTF8.GetString(raw);
            try
            {
                // Processing is a couple of small DB writes, well inside Telnyx's 2-second window.
                // Processing errors are recorded on the WebhookEvent row; we still return 200 so
                // Telnyx doesn't retry something that will fail the same way again.
                await messaging.HandleWebhookAsync(json, CancellationToken.None);
            }
            catch (JsonException ex)
            {
                log.LogWarning(ex, "Malformed Telnyx webhook");
                return Results.BadRequest();
            }
            catch (KeyNotFoundException ex)
            {
                log.LogWarning(ex, "Unexpected Telnyx webhook shape");
                return Results.BadRequest();
            }
            return Results.Ok();
        })
        .AllowAnonymous();
    }
}
