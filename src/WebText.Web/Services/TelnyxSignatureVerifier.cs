using System.Text;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace WebText.Web.Services;

/// <summary>
/// Verifies Telnyx API v2 webhook signatures: Ed25519 over "{telnyx-timestamp}|{raw body}".
/// The public key is read from Settings, so changing it takes effect without a restart.
/// </summary>
public class TelnyxSignatureVerifier
{
    private readonly SettingsService _settings;
    private (string Source, Ed25519PublicKeyParameters? Key) _cached = ("", null);

    public TelnyxSignatureVerifier(SettingsService settings) => _settings = settings;

    /// <summary>Parses a base64 public key; returns null if it isn't a valid Ed25519 key.</summary>
    public static Ed25519PublicKeyParameters? ParseKey(string? base64)
    {
        if (string.IsNullOrWhiteSpace(base64)) return null;
        try
        {
            var raw = Convert.FromBase64String(base64.Trim());
            // The portal key is a raw 32-byte key; accept an X.509 SubjectPublicKeyInfo (44 bytes) too.
            if (raw.Length > 32) raw = raw[^32..];
            return raw.Length == 32 ? new Ed25519PublicKeyParameters(raw, 0) : null;
        }
        catch (FormatException) { return null; }
    }

    public (bool ok, string? reason) Verify(string? signatureB64, string? timestamp, byte[] rawBody)
    {
        var opt = _settings.Current.Telnyx;
        if (!opt.VerifyWebhookSignature) return (true, null);

        var cached = _cached;
        if (cached.Source != opt.WebhookPublicKey)
        {
            cached = (opt.WebhookPublicKey, ParseKey(opt.WebhookPublicKey));
            _cached = cached;
        }
        if (cached.Key == null) return (false, "Telnyx public key is missing or invalid on the Settings page");
        if (string.IsNullOrEmpty(signatureB64) || string.IsNullOrEmpty(timestamp))
            return (false, "missing signature headers");

        if (!long.TryParse(timestamp, out var ts)) return (false, "bad timestamp");
        var age = Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - ts);
        if (age > opt.WebhookToleranceSeconds) return (false, $"timestamp outside tolerance ({age}s)");

        byte[] sig;
        try { sig = Convert.FromBase64String(signatureB64); }
        catch (FormatException) { return (false, "signature not base64"); }

        var prefix = Encoding.UTF8.GetBytes(timestamp + "|");
        var signer = new Ed25519Signer();
        signer.Init(false, cached.Key);
        signer.BlockUpdate(prefix, 0, prefix.Length);
        signer.BlockUpdate(rawBody, 0, rawBody.Length);
        return signer.VerifySignature(sig) ? (true, null) : (false, "signature mismatch");
    }
}
