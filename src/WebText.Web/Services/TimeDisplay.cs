namespace WebText.Web.Services;

/// <summary>Converts stored UTC times to the office's time zone (set on the Settings page).</summary>
public class TimeDisplay
{
    private readonly SettingsService _settings;
    private (string Id, TimeZoneInfo Zone) _cached = ("", TimeZoneInfo.Local);

    public TimeDisplay(SettingsService settings) => _settings = settings;

    public TimeZoneInfo Zone
    {
        get
        {
            var id = _settings.Current.TimeZoneId;
            var c = _cached;
            if (c.Id == id) return c.Zone;
            TimeZoneInfo tz;
            // Windows ("Central Standard Time") and IANA ("America/Chicago") ids both work on .NET 6+.
            try { tz = TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { tz = TimeZoneInfo.Local; }
            _cached = (id, tz);
            return tz;
        }
    }

    public DateTime ToLocal(DateTime utc) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Zone);

    public DateTime ToUtc(DateTime local) =>
        TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), Zone);

    public string Local(DateTime? utc, string format = "M/d/yyyy h:mm tt") =>
        utc is { } u ? ToLocal(u).ToString(format) : "";
}
