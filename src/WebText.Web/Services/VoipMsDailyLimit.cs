using Microsoft.EntityFrameworkCore;
using WebText.Web.Data;

namespace WebText.Web.Services;

/// <summary>
/// VoIP.ms caps API texts per day (100 unless their support raises it). The sender stops for the day when the limit
/// on the Settings page is reached, leaving texts queued, rather than letting VoIP.ms reject them.
/// </summary>
public class VoipMsDailyLimit
{
    private readonly AppDbContext _db;
    private readonly SettingsService _settings;
    private readonly TimeDisplay _time;

    public VoipMsDailyLimit(AppDbContext db, SettingsService settings, TimeDisplay time)
    {
        _db = db; _settings = settings; _time = time;
    }

    /// <summary>Null when there's no limit (Telnyx, or a limit of 0).</summary>
    public async Task<(int Used, int Limit, int Remaining)?> TodayAsync(CancellationToken ct = default)
    {
        var s = _settings.Current;
        if (s.Provider != SmsProviderKind.VoipMs || s.VoipMs.DailyLimit <= 0) return null;
        var localMidnight = _time.ToLocal(DateTime.UtcNow).Date;
        var sinceUtc = _time.ToUtc(localMidnight);
        var from = s.VoipMs.Did;
        var asMms = s.VoipMs.LongTextsAsMms;
        // Each API call counts: one per text, or one per part when long texts are split.
        var used = await _db.Messages
            .Where(m => m.Direction == MessageDirection.Outbound && m.SentAtUtc >= sinceUtc && m.FromNumber == from)
            .SumAsync(m => m.Body.Length <= VoipMsClient.SmsMaxChars || asMms ? 1 : m.Segments, ct);
        var limit = s.VoipMs.DailyLimit;
        return (used, limit, Math.Max(0, limit - used));
    }
}
