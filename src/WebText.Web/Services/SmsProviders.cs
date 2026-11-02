namespace WebText.Web.Services;

public record SmsSendResult(bool Success, string? MessageId, string? Status, string? Error, bool Retryable);
public record ProviderTestResult(bool Success, string Message);

/// <summary>A company that sends texts for us (Telnyx, VoIP.ms). Credentials come from Settings on every call.</summary>
public interface ISmsSender
{
    string Name { get; }
    Task<SmsSendResult> SendAsync(string toE164, string text, CancellationToken ct);
    /// <summary>Checks the credentials without sending anything.</summary>
    Task<ProviderTestResult> TestConnectionAsync(CancellationToken ct);
}

/// <summary>Picks the provider selected on the Settings page.</summary>
public class SmsSenders
{
    private readonly TelnyxClient _telnyx;
    private readonly VoipMsClient _voipms;
    private readonly SettingsService _settings;

    public SmsSenders(TelnyxClient telnyx, VoipMsClient voipms, SettingsService settings)
    {
        _telnyx = telnyx; _voipms = voipms; _settings = settings;
    }

    public ISmsSender Current => _settings.Current.Provider == SmsProviderKind.VoipMs ? _voipms : _telnyx;
}
