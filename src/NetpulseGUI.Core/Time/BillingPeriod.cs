namespace NetpulseGUI.Core.Time;

/// <summary>
/// Расчётный период «как у провайдера»: начинается в настраиваемый день месяца.
/// Если в месяце нет такого дня (например, 31 в феврале), период начинается в последний день месяца.
/// </summary>
public readonly record struct BillingPeriod(DateOnly Start, DateOnly EndExclusive)
{
    public int TotalDays => EndExclusive.DayNumber - Start.DayNumber;

    public DateOnly LastDay => EndExclusive.AddDays(-1);

    public bool Contains(DateOnly day) => day >= Start && day < EndExclusive;

    /// <summary>Сколько дней прошло, включая текущий.</summary>
    public int ElapsedDays(DateOnly today) => Math.Clamp(today.DayNumber - Start.DayNumber + 1, 0, TotalDays);

    public static BillingPeriod For(DateOnly day, int startDay)
    {
        startDay = Math.Clamp(startDay, 1, 31);
        var thisMonthStart = StartInMonth(day.Year, day.Month, startDay);
        DateOnly start;
        if (day >= thisMonthStart)
        {
            start = thisMonthStart;
        }
        else
        {
            var prev = new DateOnly(day.Year, day.Month, 1).AddMonths(-1);
            start = StartInMonth(prev.Year, prev.Month, startDay);
        }
        var next = new DateOnly(start.Year, start.Month, 1).AddMonths(1);
        var end = StartInMonth(next.Year, next.Month, startDay);
        return new BillingPeriod(start, end);
    }

    public static BillingPeriod Current(int startDay, TimeZoneInfo? zone = null) => For(TimeBuckets.Today(zone), startDay);

    public BillingPeriod Previous(int startDay) => For(Start.AddDays(-1), startDay);

    private static DateOnly StartInMonth(int year, int month, int startDay)
    {
        var dim = DateTime.DaysInMonth(year, month);
        return new DateOnly(year, month, Math.Min(startDay, dim));
    }

    /// <summary>Линейный прогноз расхода до конца периода по среднему за прошедшие дни.</summary>
    public long Forecast(long usedSoFar, DateTime nowLocal)
    {
        var startLocal = Start.ToDateTime(TimeOnly.MinValue);
        var endLocal = EndExclusive.ToDateTime(TimeOnly.MinValue);
        var elapsed = (nowLocal - startLocal).TotalHours;
        var total = (endLocal - startLocal).TotalHours;
        if (elapsed <= 1 || total <= 0) return usedSoFar;
        return (long)(usedSoFar / elapsed * total);
    }
}
