using System.Globalization;

namespace NetpulseGUI.Core.Time;

/// <summary>
/// Округление времени до бакетов агрегации.
/// Секунды/минуты/часы хранятся в UTC (unix-время), дни — как локальная календарная дата «yyyy-MM-dd»
/// в часовом поясе системы на момент записи (так считают провайдеры и пользователь).
/// </summary>
public static class TimeBuckets
{
    public const string DayFormat = "yyyy-MM-dd";

    public static long ToUnix(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds();

    public static DateTime FromUnix(long seconds) => DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;

    public static long FloorSecond(long unix) => unix;
    public static long FloorMinute(long unix) => unix - Mod(unix, 60);
    public static long FloorHour(long unix) => unix - Mod(unix, 3600);

    public static string DayKey(DateTime utc, TimeZoneInfo? zone = null)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone ?? TimeZoneInfo.Local);
        return local.ToString(DayFormat, CultureInfo.InvariantCulture);
    }

    public static string DayKey(DateOnly date) => date.ToString(DayFormat, CultureInfo.InvariantCulture);

    public static DateOnly ParseDay(string key) => DateOnly.ParseExact(key, DayFormat, CultureInfo.InvariantCulture);

    public static DateOnly Today(TimeZoneInfo? zone = null) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zone ?? TimeZoneInfo.Local));

    /// <summary>Начало локальных суток в UTC.</summary>
    public static DateTime LocalDayStartUtc(DateOnly day, TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Local;
        var local = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        // Если полночь попадает в «дыру» перехода на летнее время — сдвигаемся на час.
        while (zone.IsInvalidTime(local)) local = local.AddHours(1);
        return TimeZoneInfo.ConvertTimeToUtc(local, zone);
    }

    private static long Mod(long a, long m)
    {
        var r = a % m;
        return r < 0 ? r + m : r;
    }
}
