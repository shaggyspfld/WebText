using System.Net;
using Microsoft.AspNetCore.Http;

namespace WebText.Web.Services;

/// <summary>Parses the "allowed IPs" setting: one IP or CIDR per line, # comments, or "any".</summary>
public static class IpRanges
{
    /// <returns>(networks, errors). networks is null when the value is "any".</returns>
    public static (IReadOnlyList<IPNetwork>? Networks, List<string> Errors) Parse(string? text)
    {
        var errors = new List<string>();
        var list = new List<IPNetwork>();
        var lines = (text ?? "").Split(new[] { '\n', '\r', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                .Select(l => { var i = l.IndexOf('#'); return (i >= 0 ? l[..i] : l).Trim(); })
                                .Where(l => l.Length > 0).ToList();
        if (lines.Count == 1 && lines[0].Equals("any", StringComparison.OrdinalIgnoreCase)) return (null, errors);
        foreach (var l in lines)
        {
            var entry = l.Contains('/') ? l : (IPAddress.TryParse(l, out var a) ? $"{l}/{(a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? 128 : 32)}" : l);
            if (IPNetwork.TryParse(entry, out var net)) list.Add(net);
            else errors.Add(l);
        }
        return (list, errors);
    }
}

/// <summary>
/// Works out who is really calling. Cloudflare Tunnel's connector (cloudflared) runs on this server, so tunnel
/// traffic arrives from 127.0.0.1 with the caller's real IP in CF-Connecting-IP (set by Cloudflare's edge).
/// </summary>
public static class CallerInfo
{
    public const string PeerIpItem = "WebText.PeerIp";
    public const string WebhookPath = "/api/webhooks/telnyx";
    public const string VoipMsWebhookPrefix = "/api/webhooks/voipms";

    /// <summary>The only requests allowed through Cloudflare Tunnel: the providers' webhooks.</summary>
    public static bool IsProviderWebhook(HttpRequest r)
    {
        var path = r.Path.Value?.TrimEnd('/') ?? "";
        if (HttpMethods.IsPost(r.Method) && string.Equals(path, WebhookPath, StringComparison.OrdinalIgnoreCase)) return true;
        return (HttpMethods.IsGet(r.Method) || HttpMethods.IsPost(r.Method)) &&
               string.Equals(path, VoipMsWebhookPrefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The TCP peer, captured before the forwarded-headers middleware rewrites RemoteIpAddress.</summary>
    public static IPAddress? Peer(HttpContext ctx) => ctx.Items[PeerIpItem] as IPAddress ?? ctx.Connection.RemoteIpAddress;

    public static bool IsLoopback(IPAddress? ip) => ip != null && IPAddress.IsLoopback(ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip);

    /// <summary>True when the request came in through Cloudflare (via the local cloudflared connector).</summary>
    public static bool ViaTunnel(HttpContext ctx) =>
        IsLoopback(Peer(ctx)) && ctx.Request.Headers.ContainsKey("CF-Connecting-IP");

    /// <summary>The real caller: CF-Connecting-IP for tunnel traffic, otherwise the TCP peer.</summary>
    public static IPAddress? ClientIp(HttpContext ctx)
    {
        if (ViaTunnel(ctx) && IPAddress.TryParse(ctx.Request.Headers["CF-Connecting-IP"].ToString().Trim(), out var cf))
            return cf;
        var p = Peer(ctx);
        return p != null && p.IsIPv4MappedToIPv6 ? p.MapToIPv4() : p;
    }
}

/// <summary>Remembers rejected webhook calls so the Settings page can show them.</summary>
public class WebhookGuard
{
    private readonly object _lock = new();
    public int RejectedSinceStart { get; private set; }
    public DateTime? LastRejectedUtc { get; private set; }
    public string? LastRejectedIp { get; private set; }
    public string? LastRejectedReason { get; private set; }

    public DateTime? LastAcceptedUtc { get; private set; }
    public string? LastAcceptedIp { get; private set; }
    public string? LastAcceptedProvider { get; private set; }

    /// <summary>Records the last good callback's source, so an admin can see which IPs to allow.</summary>
    public void Accepted(string provider, IPAddress? ip)
    {
        lock (_lock)
        {
            LastAcceptedUtc = DateTime.UtcNow;
            LastAcceptedIp = ip?.ToString() ?? "unknown";
            LastAcceptedProvider = provider;
        }
    }

    public void Rejected(IPAddress? ip, string reason)
    {
        lock (_lock)
        {
            RejectedSinceStart++;
            LastRejectedUtc = DateTime.UtcNow;
            LastRejectedIp = ip?.ToString() ?? "unknown";
            LastRejectedReason = reason;
        }
    }
}
