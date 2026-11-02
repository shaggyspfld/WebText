using Microsoft.EntityFrameworkCore;
using WebText.Web.Data;

namespace WebText.Web.Services;

/// <summary>
/// Sends queued texts through the selected provider (Telnyx or VoIP.ms) one at a time at the configured rate. Every send — single or bulk —
/// goes through here, so the whole office shares one throttle and a slow API call never blocks a page.
/// Rows are claimed with a conditional UPDATE, so running more than one web server can't double-send.
/// </summary>
public class OutboundMessageWorker : BackgroundService
{
    private static readonly TimeSpan StuckAfter = TimeSpan.FromMinutes(10);

    private readonly IServiceScopeFactory _scopes;
    private readonly OutboundSignal _signal;
    private readonly SettingsService _settings;
    private readonly ILogger<OutboundMessageWorker> _log;

    /// <summary>Set while VoIP.ms's daily limit is holding texts in the queue (shown on the Bulk send and Settings pages).</summary>
    public static bool HeldByDailyLimit { get; private set; }

    public OutboundMessageWorker(IServiceScopeFactory scopes, OutboundSignal signal,
        SettingsService settings, ILogger<OutboundMessageWorker> log)
    {
        _scopes = scopes; _signal = signal; _settings = settings; _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        var lastStuckCheck = DateTime.MinValue;

        while (!stop.IsCancellationRequested)
        {
            try
            {
                if (DateTime.UtcNow - lastStuckCheck > TimeSpan.FromMinutes(1))
                {
                    await FailStuckAsync(stop);
                    lastStuckCheck = DateTime.UtcNow;
                }

                if (!_settings.Current.TextingReady)
                {
                    // Not configured yet (or the API key couldn't be decrypted). Leave texts queued and check again shortly.
                    await _signal.WaitAsync(TimeSpan.FromSeconds(15), stop);
                    continue;
                }

                if (await DailyLimitReachedAsync(stop))
                {
                    await _signal.WaitAsync(TimeSpan.FromMinutes(2), stop);
                    continue;
                }

                var sent = await SendNextAsync(stop);
                // Rate is re-read every loop so a change on the Settings page applies immediately.
                var interval = TimeSpan.FromSeconds(1.0 / Math.Max(0.1, _settings.Current.Telnyx.MessagesPerSecond));
                if (sent) await Task.Delay(interval, stop);
                else await _signal.WaitAsync(TimeSpan.FromSeconds(5), stop);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _log.LogError(ex, "Outbound worker error");
                await Task.Delay(TimeSpan.FromSeconds(5), stop);
            }
        }
    }

    private async Task<bool> DailyLimitReachedAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var today = await scope.ServiceProvider.GetRequiredService<VoipMsDailyLimit>().TodayAsync(ct);
        var held = today is { Remaining: 0 };
        if (held && !HeldByDailyLimit)
            _log.LogWarning("VoIP.ms daily limit of {Limit} texts reached; queued texts will go out tomorrow", today!.Value.Limit);
        HeldByDailyLimit = held;
        return held;
    }

    /// <returns>true if a message was processed.</returns>
    private async Task<bool> SendNextAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var sender = scope.ServiceProvider.GetRequiredService<SmsSenders>().Current;
        var messaging = scope.ServiceProvider.GetRequiredService<MessagingService>();

        var nextId = await db.Messages
            .Where(m => m.Direction == MessageDirection.Outbound && m.Status == MessageStatus.Pending)
            .OrderBy(m => m.Id)
            .Select(m => (long?)m.Id)
            .FirstOrDefaultAsync(ct);
        if (nextId == null) return false;

        // Atomic claim.
        var claimed = await db.Messages
            .Where(m => m.Id == nextId && m.Status == MessageStatus.Pending)
            .ExecuteUpdateAsync(s => s
                .SetProperty(m => m.Status, MessageStatus.Sending)
                .SetProperty(m => m.SendAttempts, m => m.SendAttempts + 1)
                .SetProperty(m => m.StatusUpdatedAtUtc, DateTime.UtcNow), ct);
        if (claimed == 0) return true; // another server got it; loop straight away

        var msg = await db.Messages.Include(m => m.Client).Include(m => m.Media).FirstAsync(m => m.Id == nextId, ct);

        // Re-check opt-out at send time: the client may have replied STOP after a bulk send was queued.
        if (msg.Client.OptedOut)
        {
            msg.Status = MessageStatus.Blocked;
            msg.ErrorDetail = "Client opted out before this message was sent.";
            msg.StatusUpdatedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            await messaging.BroadcastUpdatedAsync(msg);
            return true;
        }

        var result = await sender.SendAsync(msg.ToNumber ?? msg.Client.PhoneE164, msg.Body, ct);
        var now = DateTime.UtcNow;

        if (result.Success)
        {
            msg.TelnyxMessageId = result.MessageId;
            // A status webhook may have raced ahead of this response; don't overwrite a later status.
            var current = await db.Messages.AsNoTracking().Where(m => m.Id == msg.Id).Select(m => m.Status).FirstAsync(ct);
            var proposed = result.Status ?? MessageStatus.Queued;
            msg.Status = MessageStatus.Rank(current) > MessageStatus.Rank(proposed) ? current : proposed;
            msg.SentAtUtc ??= now;
            msg.ErrorDetail = null;
        }
        else if (result.Retryable && msg.SendAttempts < _settings.Current.Telnyx.MaxSendAttempts)
        {
            msg.Status = MessageStatus.Pending;
            msg.ErrorDetail = $"Attempt {msg.SendAttempts} failed, will retry: {result.Error}";
            await db.SaveChangesAsync(ct);
            await messaging.BroadcastUpdatedAsync(msg);
            await Task.Delay(TimeSpan.FromSeconds(5 * msg.SendAttempts), ct);
            return true;
        }
        else
        {
            msg.Status = MessageStatus.Failed;
            msg.ErrorDetail = Truncate(result.Error, 1000);
        }

        msg.StatusUpdatedAtUtc = now;
        await db.SaveChangesAsync(ct);
        await messaging.BroadcastUpdatedAsync(msg);
        return true;
    }

    /// <summary>
    /// A row left in "sending" (e.g. the app pool recycled mid-request) may or may not have reached Telnyx.
    /// Mark it failed rather than risk texting the client twice; staff can resend after checking.
    /// </summary>
    private async Task FailStuckAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var cutoff = DateTime.UtcNow - StuckAfter;
        var n = await db.Messages
            .Where(m => m.Status == MessageStatus.Sending && m.TelnyxMessageId == null && m.StatusUpdatedAtUtc < cutoff)
            .ExecuteUpdateAsync(s => s
                .SetProperty(m => m.Status, MessageStatus.Failed)
                .SetProperty(m => m.ErrorDetail, "Send was interrupted; it may not have gone out. Check your texting provider's message log before resending.")
                .SetProperty(m => m.StatusUpdatedAtUtc, DateTime.UtcNow), ct);
        if (n > 0) _log.LogWarning("Marked {Count} interrupted sends as failed", n);
    }

    private static string? Truncate(string? s, int max) => s == null ? null : s.Length > max ? s[..max] : s;
}
