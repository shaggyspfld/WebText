using System.Security.Cryptography;
using System.Text;

namespace WebText.Web.Auth;

/// <summary>
/// Time-based one-time passwords (RFC 6238): 6 digits, 30-second steps, HMAC-SHA1. This is what Microsoft
/// Authenticator, Google Authenticator, Authy, 1Password, Bitwarden etc. use. No I/O here, so it's testable on its own.
/// </summary>
public static class Totp
{
    public const int Digits = 6;
    public const int StepSeconds = 30;
    /// <summary>Accept the previous and next code too, to allow for clock drift between the phone and the server.</summary>
    public const int AllowedDrift = 1;

    /// <summary>A new random 160-bit secret (the size RFC 4226 recommends).</summary>
    public static byte[] NewSecret() => RandomNumberGenerator.GetBytes(20);

    public static long StepAt(DateTimeOffset time) => time.ToUnixTimeSeconds() / StepSeconds;

    public static string Code(byte[] secret, long step)
    {
        Span<byte> counter = stackalloc byte[8];
        for (var i = 7; i >= 0; i--) { counter[i] = (byte)(step & 0xFF); step >>= 8; }
        Span<byte> hash = stackalloc byte[20];
        HMACSHA1.HashData(secret, counter, hash);
        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (binary % 1_000_000).ToString("D6");
    }

    /// <summary>
    /// Checks a code typed by the user. Returns the matching time step (so the caller can refuse to accept the same
    /// code again), or null if it's wrong. <paramref name="lastUsedStep"/> and anything before it are rejected.
    /// </summary>
    public static long? Verify(byte[] secret, string? code, long lastUsedStep, DateTimeOffset now)
    {
        code = new string((code ?? "").Where(char.IsDigit).ToArray());   // allow "123 456"
        if (code.Length != Digits) return null;
        var current = StepAt(now);
        long? match = null;
        // Check every candidate (no early exit) and compare in constant time.
        for (var s = current - AllowedDrift; s <= current + AllowedDrift; s++)
        {
            var expected = Encoding.ASCII.GetBytes(Code(secret, s));
            if (CryptographicOperations.FixedTimeEquals(expected, Encoding.ASCII.GetBytes(code)) && s > lastUsedStep)
                match = s;
        }
        return match;
    }

    /// <summary>The otpauth:// link that authenticator apps read from the QR code.</summary>
    public static string ProvisioningUri(string issuer, string account, byte[] secret)
    {
        var label = Uri.EscapeDataString(issuer) + ":" + Uri.EscapeDataString(account);
        return $"otpauth://totp/{label}?secret={Base32Encode(secret)}&issuer={Uri.EscapeDataString(issuer)}" +
               $"&algorithm=SHA1&digits={Digits}&period={StepSeconds}";
    }

    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string Base32Encode(byte[] data)
    {
        var sb = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b; bits += 8;
            while (bits >= 5) { sb.Append(Alphabet[(buffer >> (bits - 5)) & 31]); bits -= 5; }
        }
        if (bits > 0) sb.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        return sb.ToString();
    }

    public static byte[] Base32Decode(string text)
    {
        text = text.Trim().TrimEnd('=').Replace(" ", "").ToUpperInvariant();
        var result = new List<byte>(text.Length * 5 / 8);
        int buffer = 0, bits = 0;
        foreach (var c in text)
        {
            var v = Alphabet.IndexOf(c);
            if (v < 0) throw new FormatException("Not base32");
            buffer = (buffer << 5) | v; bits += 5;
            if (bits >= 8) { result.Add((byte)((buffer >> (bits - 8)) & 0xFF)); bits -= 8; }
        }
        return result.ToArray();
    }

    /// <summary>Base32 key in groups of four, for typing into an app by hand.</summary>
    public static string FormatKey(byte[] secret) =>
        string.Join(" ", Base32Encode(secret).Chunk(4).Select(c => new string(c)));
}
