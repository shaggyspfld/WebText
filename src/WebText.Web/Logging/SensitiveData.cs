using System.Text.RegularExpressions;

namespace WebText.Web.Logging;

/// <summary>
/// Removes things that must never be written to a log: anything that looks like a Social Security number (or ITIN),
/// and the secret key in the VoIP.ms callback URL. Used by the application log (every ILogger message and exception)
/// and by the log-type tables in the database (webhook copies, error details, the security and change logs).
/// Deliberately errs on the side of redacting: any 9-digit number standing on its own is treated as an SSN.
/// </summary>
public static partial class SensitiveData
{
    public const string SsnMask = "***-**-****";

    // 123-45-6789, 123 45 6789, 123.45.6789, 123–45–6789 (en/em dash), or 123456789, as long as it isn't part of a
    // longer number. Both separators or neither, so phone numbers, ZIP+4 (60601-1234), dates and long IDs are left alone.
    [GeneratedRegex(@"(?<![\d\-–—])(?:\d{3}[\-–—. ]\d{2}[\-–—. ]\d{4}|\d{9})(?![\d])", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 250)]
    private static partial Regex SsnPattern();

    [GeneratedRegex(@"(/api/webhooks/voipms[^\s""']*?[?&]key=)[^&\s""'#]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 250)]
    private static partial Regex VoipMsKeyPattern();

    /// <summary>True if the text contains anything that would be redacted.</summary>
    public static bool NeedsRedaction(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        if (text.Contains("/api/webhooks/voipms", StringComparison.OrdinalIgnoreCase) && Safe(() => VoipMsKeyPattern().IsMatch(text))) return true;
        return HasDigitRun(text) && Safe(() => SsnPattern().IsMatch(text));
    }

    public static string Redact(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var result = text;
        if (HasDigitRun(result))
        {
            try { result = SsnPattern().Replace(result, SsnMask); }
            catch (RegexMatchTimeoutException) { return "[log entry removed: too long to check for SSNs]"; }
        }
        if (result.Contains("/api/webhooks/voipms", StringComparison.OrdinalIgnoreCase))
        {
            try { result = VoipMsKeyPattern().Replace(result, "$1[key]"); }
            catch (RegexMatchTimeoutException) { return "[log entry removed: too long to check]"; }
        }
        return result;
    }

    public static string? RedactOrNull(string? text) => text == null ? null : Redact(text);

    /// <summary>Quick pre-check: an SSN needs at least 3 digits in a row, so most text skips the regex entirely.</summary>
    private static bool HasDigitRun(string text)
    {
        var run = 0;
        foreach (var c in text)
        {
            if (c is >= '0' and <= '9') { if (++run >= 3) return true; }
            else run = 0;
        }
        return false;
    }

    private static bool Safe(Func<bool> f)
    {
        try { return f(); } catch (RegexMatchTimeoutException) { return true; }
    }
}

/// <summary>Marks a string column whose value is redacted by <see cref="SensitiveData"/> whenever it's saved.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class RedactSensitiveAttribute : Attribute { }
