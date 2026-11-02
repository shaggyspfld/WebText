using WebText.Web.Data;
using System.Text.Json;

namespace WebText.Web.Services;

/// <summary>
/// VoIP.ms REST/JSON API (https://voip.ms/api/v1/rest.php): sendSMS (up to 160 characters), sendMMS (longer texts),
/// getBalance (connection test). Needs the API enabled for the account and this server's public IP allowed, both under
/// voip.ms → Main Menu → SOAP and REST/JSON API. VoIP.ms gives no delivery receipts, so a text it accepts is "sent".
/// </summary>
public class VoipMsClient : ISmsSender
{
    public const int SmsMaxChars = 160;
    public const int MmsMaxChars = 2048;

    private readonly HttpClient _http;
    private readonly SettingsService _settings;
    private readonly ILogger<VoipMsClient> _log;

    public VoipMsClient(HttpClient http, SettingsService settings, ILogger<VoipMsClient> log)
    {
        _http = http; _settings = settings; _log = log;
        _http.Timeout = TimeSpan.FromSeconds(30);
    }

    public string Name => "VoIP.ms";

    public async Task<SmsSendResult> SendAsync(string toE164, string text, CancellationToken ct)
    {
        var o = _settings.Current.VoipMs;
        if (string.IsNullOrWhiteSpace(o.Username) || string.IsNullOrWhiteSpace(o.ApiPassword))
            return new(false, null, null, "The VoIP.ms account email or API password hasn't been set. An admin can enter them on the Settings page.", false);
        var did = Nanp(o.Did);
        if (did == null) return new(false, null, null, "No VoIP.ms sending number (DID) is set. An admin can enter it on the Settings page.", false);
        var dst = Nanp(toE164);
        if (dst == null) return new(false, null, null, "VoIP.ms can only text US and Canadian numbers.", false);

        text = text.Trim();
        if (text.Length <= SmsMaxChars)
            return await CallSendAsync("sendSMS", "sms", o, did, dst, text, ct);
        if (o.LongTextsAsMms)
            return await CallSendAsync("sendMMS", "mms", o, did, dst, text.Length > MmsMaxChars ? text[..MmsMaxChars] : text, ct);

        // Split into numbered parts of up to 160 characters.
        var parts = Split(text);
        var ids = new List<string>();
        for (var i = 0; i < parts.Count; i++)
        {
            var r = await CallSendAsync("sendSMS", "sms", o, did, dst, parts[i], ct);
            if (!r.Success)
            {
                // Once part 1 has gone out, don't let the queue retry the whole text (the client would get duplicates).
                if (i == 0) return r;
                return new(false, string.Join(",", ids), null,
                    $"Only {i} of {parts.Count} parts were sent; part {i + 1} failed: {r.Error}", false);
            }
            if (r.MessageId != null) ids.Add(r.MessageId);
        }
        return new(true, string.Join(",", ids), MessageStatus.Sent, null, false);
    }

    public async Task<ProviderTestResult> TestConnectionAsync(CancellationToken ct)
    {
        var o = _settings.Current.VoipMs;
        if (string.IsNullOrWhiteSpace(o.Username) || string.IsNullOrWhiteSpace(o.ApiPassword))
            return new(false, "Enter the VoIP.ms account email and API password, and save, first.");
        try
        {
            using var doc = await CallAsync(o, new() { ["method"] = "getBalance" }, ct);
            var status = Status(doc);
            if (status != "success") return new(false, "VoIP.ms: " + Explain(status));
            var balance = doc.RootElement.TryGetProperty("balance", out var b) && b.ValueKind == JsonValueKind.Object
                          && b.TryGetProperty("current_balance", out var cb) ? cb.ToString() : "?";
            var didNote = Nanp(o.Did) == null ? " No sending number (DID) is set yet." : "";
            return new(true, $"Connected to VoIP.ms. Account balance: ${balance}.{didNote}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new(false, "Could not reach VoIP.ms: " + ex.Message);
        }
    }

    // ---------------------------------------------------------------- helpers

    private async Task<SmsSendResult> CallSendAsync(string method, string idField, VoipMsOptions o, string did, string dst,
        string message, CancellationToken ct)
    {
        try
        {
            using var doc = await CallAsync(o, new()
            {
                ["method"] = method, ["did"] = did, ["dst"] = dst, ["message"] = message
            }, ct);
            var status = Status(doc);
            if (status == "success")
            {
                var id = doc.RootElement.TryGetProperty(idField, out var idEl) ? idEl.ToString() : null;
                return new(true, id, MessageStatus.Sent, null, false);
            }
            var retryable = status is "method_maintenance" or "api_limit_exceeded" or "sms_wait_message";
            _log.LogWarning("VoIP.ms {Method} to {Dst} failed: {Status}", method, dst, status);
            return new(false, null, null, Explain(status), retryable);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            _log.LogWarning(ex, "VoIP.ms {Method} to {Dst} failed (network)", method, dst);
            return new(false, null, null, "Network error contacting VoIP.ms: " + ex.Message, true);
        }
        catch (JsonException ex)
        {
            _log.LogError(ex, "Unexpected VoIP.ms response");
            return new(false, null, null, "VoIP.ms sent an unexpected response.", false);
        }
    }

    private async Task<JsonDocument> CallAsync(VoipMsOptions o, Dictionary<string, string> fields, CancellationToken ct)
    {
        fields["api_username"] = o.Username;
        fields["api_password"] = o.ApiPassword;
        fields["content_type"] = "json";
        // POST, so the API password and message text aren't in a URL (and so aren't written to any proxy logs).
        using var resp = await _http.PostAsync(o.BaseUrl, new FormUrlEncodedContent(fields), ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode && body.TrimStart().StartsWith('{') == false)
            throw new HttpRequestException($"HTTP {(int)resp.StatusCode}");
        return JsonDocument.Parse(body);
    }

    private static string Status(JsonDocument doc) =>
        doc.RootElement.TryGetProperty("status", out var s) ? s.GetString() ?? "" : "";

    /// <summary>Plain-English versions of VoIP.ms's error codes.</summary>
    public static string Explain(string status) => status switch
    {
        "invalid_credentials" or "missing_credentials" =>
            "the account email or API password is wrong (the API password is set under SOAP and REST/JSON API; it isn't your login password).",
        "ip_not_enabled" =>
            "this server's public IP address isn't allowed to use the API. Add it under voip.ms → Main Menu → SOAP and REST/JSON API.",
        "api_not_enabled" => "the API isn't enabled for this account (voip.ms → Main Menu → SOAP and REST/JSON API → Enable API).",
        "api_limit_exceeded" => "too many API requests; it will be retried.",
        "limit_reached" =>
            "the account's daily texting limit has been reached (100 a day unless VoIP.ms support has raised it).",
        "invalid_did" or "missing_did" => "the sending number isn't an SMS-enabled DID on this account.",
        "invalid_dst" => "the client's number isn't a valid US or Canadian number for texting.",
        "sms_toolong" => "the text is too long for VoIP.ms.",
        "message_empty" => "the text is empty.",
        "sms_apply_regulations" => "VoIP.ms needs this number's messaging registration (e.g. 10DLC) completed before it can send.",
        "sms_failed" => "VoIP.ms couldn't send the text (sms_failed). Check the DID's SMS settings and account balance.",
        "method_maintenance" => "VoIP.ms texting is down for maintenance; it will be retried.",
        "" => "no response status.",
        _ => $"error '{status}'."
    };

    /// <summary>E.164 US/Canada number → the 10 digits VoIP.ms wants; null if it isn't one.</summary>
    public static string? Nanp(string? e164)
    {
        var digits = new string((e164 ?? "").Where(char.IsDigit).ToArray());
        if (digits.Length == 11 && digits[0] == '1') digits = digits[1..];
        return digits.Length == 10 && digits[0] is >= '2' and <= '9' ? digits : null;
    }

    /// <summary>Splits a long text at spaces into parts of up to 160 characters, each ending " (1/3)".</summary>
    public static List<string> Split(string text)
    {
        for (var suffixLen = 6; ; suffixLen++)   // " (1/9)" = 6; grows if there are 10+ parts
        {
            var max = SmsMaxChars - suffixLen;
            var parts = new List<string>();
            var rest = text.Trim();
            while (rest.Length > 0)
            {
                if (rest.Length <= max) { parts.Add(rest); break; }
                var cut = rest.LastIndexOf(' ', max);
                if (cut < max / 2) cut = max;   // no good space: hard cut
                parts.Add(rest[..cut].TrimEnd());
                rest = rest[cut..].TrimStart();
            }
            var suffix = $" ({parts.Count}/{parts.Count})".Length;
            if (suffix <= suffixLen)
                return parts.Select((p, i) => $"{p} ({i + 1}/{parts.Count})").ToList();
        }
    }
}
