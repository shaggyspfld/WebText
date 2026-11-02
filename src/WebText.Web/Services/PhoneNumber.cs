using System.Text;

namespace WebText.Web.Services;

public static class PhoneNumber
{
    /// <summary>
    /// Normalises user-entered numbers to E.164. Ten-digit numbers are assumed to be US/Canada (+1).
    /// Returns null when the input can't be a valid number.
    /// </summary>
    public static string? ToE164(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var trimmed = input.Trim().TrimStart('\'').Trim();   // our CSV export prefixes "+..." with ' for Excel
        var hasPlus = trimmed.StartsWith('+');

        var digits = new StringBuilder();
        foreach (var ch in trimmed)
            if (char.IsDigit(ch)) digits.Append(ch);
        var d = digits.ToString();

        if (hasPlus) return d.Length is >= 8 and <= 15 ? "+" + d : null;
        if (d.Length == 10) return "+1" + d;
        if (d.Length == 11 && d[0] == '1') return "+" + d;
        return null;
    }

    /// <summary>+13125550100 → (312) 555-0100. Non-NANP numbers are returned unchanged.</summary>
    public static string Format(string? e164)
    {
        if (string.IsNullOrEmpty(e164)) return "";
        if (e164.Length == 12 && e164.StartsWith("+1"))
            return $"({e164.Substring(2, 3)}) {e164.Substring(5, 3)}-{e164.Substring(8)}";
        return e164;
    }
}

public static class SmsSegments
{
    // GSM 03.38 basic + extension characters.
    private const string Gsm7 =
        "@£$¥èéùìòÇ\nØø\rÅåΔ_ΦΓΛΩΠΨΣΘΞÆæßÉ !\"#¤%&'()*+,-./0123456789:;<=>?" +
        "¡ABCDEFGHIJKLMNOPQRSTUVWXYZÄÖÑÜ§¿abcdefghijklmnopqrstuvwxyzäöñüà";
    private const string Gsm7Ext = "^{}\\[~]|€";

    public static int Count(string text)
    {
        if (string.IsNullOrEmpty(text)) return 1;
        var isGsm = true;
        var units = 0;
        foreach (var ch in text)
        {
            if (Gsm7.IndexOf(ch) >= 0) units++;
            else if (Gsm7Ext.IndexOf(ch) >= 0) units += 2;
            else { isGsm = false; break; }
        }
        if (isGsm) return units <= 160 ? 1 : (int)Math.Ceiling(units / 153.0);

        // UCS-2: count UTF-16 code units
        var len = text.Length;
        return len <= 70 ? 1 : (int)Math.Ceiling(len / 67.0);
    }
}
