namespace WebText.Web.Services;

/// <summary>Telnyx settings, populated from the database by <see cref="SettingsService"/>.</summary>
public class TelnyxOptions
{
    /// <summary>Telnyx API v2 key (Portal → API Keys). Keep it out of source control.</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>Your Telnyx long code / toll-free number in E.164, e.g. +13125550100.</summary>
    public string FromNumber { get; set; } = "";

    /// <summary>Optional. Messaging profile to send through.</summary>
    public string? MessagingProfileId { get; set; }

    /// <summary>Optional per-message webhook URL. Normally the messaging profile's webhook URL is used.</summary>
    public string? WebhookUrl { get; set; }

    /// <summary>Public key from Portal → Account Settings → Keys &amp; Credentials → Public Key (base64).</summary>
    public string WebhookPublicKey { get; set; } = "";

    /// <summary>Reject webhooks with a missing or bad signature. Only turn off for local testing.</summary>
    public bool VerifyWebhookSignature { get; set; } = true;

    /// <summary>Webhooks older than this are rejected (replay protection).</summary>
    public int WebhookToleranceSeconds { get; set; } = 300;

    public string BaseUrl { get; set; } = "https://api.telnyx.com/v2/";

    /// <summary>
    /// Outbound throughput. US 10DLC long codes are typically limited by your campaign's carrier tier;
    /// 1 msg/sec is a safe default for a single long code.
    /// </summary>
    public double MessagesPerSecond { get; set; } = 1.0;

    public int MaxSendAttempts { get; set; } = 3;
}

/// <summary>VoIP.ms settings, populated from the database by <see cref="SettingsService"/>.</summary>
public class VoipMsOptions
{
    /// <summary>The voip.ms account email (api_username).</summary>
    public string Username { get; set; } = "";
    /// <summary>The API password set under SOAP and REST/JSON API (not the login password).</summary>
    public string ApiPassword { get; set; } = "";
    /// <summary>SMS-enabled DID in E.164.</summary>
    public string Did { get; set; } = "";
    public bool LongTextsAsMms { get; set; } = true;
    public int DailyLimit { get; set; } = 100;
    /// <summary>Secret path segment of the inbound callback URL.</summary>
    public string CallbackToken { get; set; } = "";
    public string BaseUrl { get; set; } = "https://voip.ms/api/v1/rest.php";
}
