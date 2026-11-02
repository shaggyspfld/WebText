using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WebText.Web.Services;

/// <summary>Thin wrapper over the Telnyx v2 API. Reads the API key and number from Settings on every call.</summary>
public class TelnyxClient : ISmsSender
{
    private readonly HttpClient _http;
    private readonly SettingsService _settings;
    private readonly ILogger<TelnyxClient> _log;

    public TelnyxClient(HttpClient http, SettingsService settings, ILogger<TelnyxClient> log)
    {
        _http = http;
        _settings = settings;
        _log = log;
        _http.Timeout = TimeSpan.FromSeconds(30);
    }

    private HttpRequestMessage Request(HttpMethod method, string path, TelnyxOptions opt)
    {
        var baseUrl = opt.BaseUrl.EndsWith('/') ? opt.BaseUrl : opt.BaseUrl + "/";
        var req = new HttpRequestMessage(method, new Uri(new Uri(baseUrl), path));
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", opt.ApiKey);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return req;
    }

    public string Name => "Telnyx";

    public async Task<SmsSendResult> SendAsync(string toE164, string text, CancellationToken ct)
    {
        var opt = _settings.Current.Telnyx;
        if (string.IsNullOrWhiteSpace(opt.ApiKey))
            return new(false, null, null, "The Telnyx API key hasn't been set. An admin can enter it on the Settings page.", false);
        if (string.IsNullOrWhiteSpace(opt.FromNumber) && string.IsNullOrWhiteSpace(opt.MessagingProfileId))
            return new(false, null, null, "No sending number is set. An admin can enter it on the Settings page.", false);

        var body = new SendRequest
        {
            From = string.IsNullOrWhiteSpace(opt.FromNumber) ? null : opt.FromNumber,
            To = toE164,
            Text = text,
            MessagingProfileId = string.IsNullOrWhiteSpace(opt.MessagingProfileId) ? null : opt.MessagingProfileId,
            WebhookUrl = string.IsNullOrWhiteSpace(opt.WebhookUrl) ? null : opt.WebhookUrl
        };

        HttpResponseMessage resp;
        try
        {
            using var req = Request(HttpMethod.Post, "messages", opt);
            req.Content = JsonContent.Create(body, options: JsonOpts);
            resp = await _http.SendAsync(req, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            _log.LogWarning(ex, "Telnyx send to {To} failed (network)", toE164);
            return new(false, null, null, "Network error contacting Telnyx: " + ex.Message, true);
        }

        using (resp)
        {
            var json = await resp.Content.ReadAsStringAsync(ct);
            if (resp.IsSuccessStatusCode)
            {
                try
                {
                    using var doc = JsonDocument.Parse(json);
                    var data = doc.RootElement.GetProperty("data");
                    var id = data.GetProperty("id").GetString();
                    string? status = null;
                    if (data.TryGetProperty("to", out var to) && to.ValueKind == JsonValueKind.Array && to.GetArrayLength() > 0
                        && to[0].TryGetProperty("status", out var st))
                        status = st.GetString();
                    return new(true, id, status ?? "queued", null, false);
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Unexpected Telnyx response: {Body}", json);
                    return new(true, null, "queued", null, false);
                }
            }

            var err = ParseError(json) ?? $"HTTP {(int)resp.StatusCode}";
            var retryable = (int)resp.StatusCode == 429 || (int)resp.StatusCode >= 500;
            _log.LogWarning("Telnyx send to {To} failed: {Status} {Error}", toE164, (int)resp.StatusCode, err);
            return new(false, null, null, err, retryable);
        }
    }

    /// <summary>Checks the API key by reading the account balance (no text is sent).</summary>
    public async Task<ProviderTestResult> TestConnectionAsync(CancellationToken ct)
    {
        var opt = _settings.Current.Telnyx;
        if (string.IsNullOrWhiteSpace(opt.ApiKey)) return new(false, "No API key is saved yet.");
        try
        {
            using var req = Request(HttpMethod.Get, "balance", opt);
            using var resp = await _http.SendAsync(req, ct);
            var json = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                return new(false, $"Telnyx rejected the request ({(int)resp.StatusCode}): {ParseError(json)}");

            using var doc = JsonDocument.Parse(json);
            var data = doc.RootElement.GetProperty("data");
            var balance = data.TryGetProperty("balance", out var b) ? b.ToString() : "?";
            var currency = data.TryGetProperty("currency", out var c) ? c.GetString() : "";
            return new(true, $"Connected to Telnyx. Account balance: {balance} {currency}.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException)
        {
            return new(false, "Could not reach Telnyx: " + ex.Message);
        }
    }

    private static string? ParseError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("errors", out var errs) && errs.ValueKind == JsonValueKind.Array)
            {
                var parts = new List<string>();
                foreach (var e in errs.EnumerateArray())
                {
                    var code = e.TryGetProperty("code", out var c) ? c.ToString() : "";
                    var title = e.TryGetProperty("title", out var t) ? t.GetString() : "";
                    var detail = e.TryGetProperty("detail", out var d) ? d.GetString() : "";
                    parts.Add($"{code} {title}{(string.IsNullOrEmpty(detail) ? "" : ": " + detail)}".Trim());
                }
                return string.Join("; ", parts);
            }
        }
        catch { /* not JSON */ }
        return body.Length > 300 ? body[..300] : body;
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private sealed class SendRequest
    {
        [JsonPropertyName("from")] public string? From { get; set; }
        [JsonPropertyName("to")] public string To { get; set; } = "";
        [JsonPropertyName("text")] public string Text { get; set; } = "";
        [JsonPropertyName("messaging_profile_id")] public string? MessagingProfileId { get; set; }
        [JsonPropertyName("webhook_url")] public string? WebhookUrl { get; set; }
        [JsonPropertyName("type")] public string Type { get; set; } = "SMS";
    }
}
