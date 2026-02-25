namespace Lineup.Web.Services;

/// <summary>
/// Provides timezone conversion using the app-configured timezone.
/// Falls back to TZ environment variable, then UTC.
/// </summary>
public interface ITimeZoneService
{
    /// <summary>
    /// The currently configured timezone.
    /// </summary>
    TimeZoneInfo TimeZone { get; }

    /// <summary>
    /// Converts a UTC DateTime to the configured timezone.
    /// </summary>
    DateTime ConvertFromUtc(DateTime utcDateTime);

    /// <summary>
    /// Converts a DateTime in the configured timezone to UTC.
    /// </summary>
    DateTime ConvertToUtc(DateTime dateTime);

    /// <summary>
    /// Gets the current date/time in the configured timezone.
    /// </summary>
    DateTime Now { get; }

    /// <summary>
    /// Gets today's date in the configured timezone.
    /// </summary>
    DateTime Today { get; }
}

/// <summary>
/// Resolves timezone from: AppSettings ? TZ environment variable ? UTC.
/// </summary>
public class TimeZoneService : ITimeZoneService
{
    private readonly IAppSettingsService _settingsService;
    private TimeZoneInfo _cached;
    private string _cachedId;

    public TimeZoneService(IAppSettingsService settingsService)
    {
        _settingsService = settingsService;
        _cachedId = "";
        _cached = TimeZoneInfo.Utc;
        Refresh();
        _settingsService.OnSettingsChanged += Refresh;
    }

    public TimeZoneInfo TimeZone
    {
        get
        {
            var currentId = _settingsService.Settings.TimeZoneId;
            if (currentId != _cachedId)
            {
                Refresh();
            }
            return _cached;
        }
    }

    public DateTime ConvertFromUtc(DateTime utcDateTime) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utcDateTime, DateTimeKind.Utc), TimeZone);

    public DateTime ConvertToUtc(DateTime dateTime) =>
        TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(dateTime, DateTimeKind.Unspecified), TimeZone);

    public DateTime Now => TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZone);

    public DateTime Today => Now.Date;

    private void Refresh()
    {
        var id = _settingsService.Settings.TimeZoneId;

        // 1. Try the configured setting
        if (!string.IsNullOrEmpty(id))
        {
            try
            {
                _cached = TimeZoneInfo.FindSystemTimeZoneById(id);
                _cachedId = id;
                return;
            }
            catch (TimeZoneNotFoundException) { }
        }

        // 2. Fall back to TZ environment variable
        var tz = Environment.GetEnvironmentVariable("TZ");
        if (!string.IsNullOrEmpty(tz))
        {
            try
            {
                _cached = TimeZoneInfo.FindSystemTimeZoneById(tz);
                _cachedId = id ?? "";
                return;
            }
            catch (TimeZoneNotFoundException) { }
        }

        // 3. Fall back to system local (UTC in containers)
        _cached = TimeZoneInfo.Local;
        _cachedId = id ?? "";
    }
}
