using System.Text;
using System.Text.RegularExpressions;
using WebText.Web.Data;

namespace WebText.Web.Services;

/// <summary>Keyword matching for auto-replies. No database or I/O here, so it can be tested on its own.</summary>
public static class AutoReplies
{
    /// <summary>
    /// Words the app and carriers already handle. They can't be used as auto-reply keywords: STOP/START change a
    /// client's opt-out status, and Telnyx answers HELP/STOP itself, so a second reply would confuse clients.
    /// </summary>
    public static readonly IReadOnlySet<string> ReservedKeywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "STOP", "STOPALL", "UNSUBSCRIBE", "CANCEL", "END", "QUIT", "OPTOUT", "OPT OUT", "REVOKE",
        "START", "UNSTOP", "YES", "OPTIN", "OPT IN", "HELP", "INFO"
    };

    /// <summary>
    /// Upper-case, single-spaced keyword with punctuation removed (the same form incoming texts are compared in),
    /// or null if it isn't usable (empty, too long, symbols only).
    /// </summary>
    public static string? NormalizeKeyword(string? keyword)
    {
        var k = NormalizeText(keyword);
        if (k.Length == 0 || k.Length > 50) return null;
        return k;
    }

    /// <summary>Turns an incoming text into comparable form: upper-case words without punctuation or emoji.</summary>
    public static string NormalizeText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text.Normalize(NormalizationForm.FormC))
            sb.Append(char.IsLetterOrDigit(ch) ? char.ToUpperInvariant(ch) : ' ');
        return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    }

    private static string NormalizeKeywordForCompare(string keyword) => NormalizeText(keyword);

    /// <summary>
    /// Finds the auto-reply for an incoming text. Exact matches win over first-word matches, which win over
    /// anywhere matches; within a kind, the longest keyword wins ("STORE HOURS" beats "STORE").
    /// </summary>
    public static AutoResponder? FindMatch(IEnumerable<AutoResponder> responders, string? incomingText)
    {
        var text = NormalizeText(incomingText);
        if (text.Length == 0) return null;
        var padded = " " + text + " ";

        return responders
            .Where(r => r.IsActive && !string.IsNullOrWhiteSpace(r.Keyword) && !ReservedKeywords.Contains(r.Keyword.Trim()))
            .Select(r => (r, k: NormalizeKeywordForCompare(r.Keyword)))
            .Where(x => x.k.Length > 0 && x.r.Match switch
            {
                KeywordMatch.Exact => text == x.k,
                KeywordMatch.FirstWord => text == x.k || text.StartsWith(x.k + " ", StringComparison.Ordinal),
                KeywordMatch.Anywhere => padded.Contains(" " + x.k + " ", StringComparison.Ordinal),
                _ => false
            })
            .OrderBy(x => x.r.Match)
            .ThenByDescending(x => x.k.Length)
            .Select(x => x.r)
            .FirstOrDefault();
    }

    /// <summary>Fills in {FirstName}, {LastName}, {FullName}. Unknown numbers get blanks instead of "Unknown".</summary>
    public static string Merge(string reply, Client client)
    {
        var unknown = client.IsAutoCreated;
        var first = unknown ? "" : client.FirstName;
        var last = unknown ? "" : client.LastName;
        var full = unknown ? "" : client.DisplayName;
        var merged = reply
            .Replace("{FirstName}", first, StringComparison.OrdinalIgnoreCase)
            .Replace("{LastName}", last, StringComparison.OrdinalIgnoreCase)
            .Replace("{FullName}", full, StringComparison.OrdinalIgnoreCase);
        // "Hi , thanks" -> "Hi, thanks" when a name was blank.
        return Regex.Replace(merged, @"[ \t]+([,.!?])", "$1").Replace("  ", " ").Trim();
    }

    /// <summary>The name recorded as the sender of an auto-reply (shown in threads, the log and reports).</summary>
    public static string SenderName(string keyword) => $"Auto-reply ({keyword})";

    public const string SenderPrefix = "Auto-reply (";

    public static bool IsAutoReplySender(string? sentBy) =>
        sentBy != null && sentBy.StartsWith(SenderPrefix, StringComparison.Ordinal);
}
