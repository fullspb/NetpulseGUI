using NetpulseGUI.Core.Formatting;
using NetpulseGUI.Core.Time;

namespace NetpulseGUI.Tests;

public class TimeTests
{
    [Fact]
    public void FloorMinuteAndHour()
    {
        var t = TimeBuckets.ToUnix(new DateTime(2026, 3, 5, 10, 37, 42, DateTimeKind.Utc));
        Assert.Equal(TimeBuckets.ToUnix(new DateTime(2026, 3, 5, 10, 37, 0, DateTimeKind.Utc)), TimeBuckets.FloorMinute(t));
        Assert.Equal(TimeBuckets.ToUnix(new DateTime(2026, 3, 5, 10, 0, 0, DateTimeKind.Utc)), TimeBuckets.FloorHour(t));
    }

    [Fact]
    public void DayKey_UsesTimeZone()
    {
        var utc = new DateTime(2026, 3, 5, 22, 30, 0, DateTimeKind.Utc);
        var msk = TimeZoneInfo.CreateCustomTimeZone("MSK", TimeSpan.FromHours(3), "MSK", "MSK");
        Assert.Equal("2026-03-06", TimeBuckets.DayKey(utc, msk));
        Assert.Equal("2026-03-05", TimeBuckets.DayKey(utc, TimeZoneInfo.Utc));
    }

    [Fact]
    public void LocalDayStart_ConvertsToUtc()
    {
        var plus5 = TimeZoneInfo.CreateCustomTimeZone("P5", TimeSpan.FromHours(5), "P5", "P5");
        Assert.Equal(new DateTime(2026, 1, 9, 19, 0, 0, DateTimeKind.Utc), TimeBuckets.LocalDayStartUtc(new DateOnly(2026, 1, 10), plus5));
    }

    [Theory]
    [InlineData("2026-03-15", 1, "2026-03-01", "2026-04-01")]
    [InlineData("2026-03-15", 10, "2026-03-10", "2026-04-10")]
    [InlineData("2026-03-05", 10, "2026-02-10", "2026-03-10")]
    [InlineData("2026-02-28", 31, "2026-02-28", "2026-03-31")] // в феврале нет 31-го — старт 28-го
    [InlineData("2026-02-27", 31, "2026-01-31", "2026-02-28")]
    [InlineData("2026-01-01", 15, "2025-12-15", "2026-01-15")] // переход через год
    [InlineData("2028-02-29", 30, "2028-02-29", "2028-03-30")] // високосный год
    public void BillingPeriod_Computed(string day, int startDay, string expectedStart, string expectedEnd)
    {
        var p = BillingPeriod.For(DateOnly.Parse(day), startDay);
        Assert.Equal(DateOnly.Parse(expectedStart), p.Start);
        Assert.Equal(DateOnly.Parse(expectedEnd), p.EndExclusive);
        Assert.True(p.Contains(DateOnly.Parse(day)));
    }

    [Fact]
    public void BillingPeriods_AreContiguous()
    {
        for (var startDay = 1; startDay <= 31; startDay++)
        {
            var p = BillingPeriod.For(new DateOnly(2026, 1, 1), startDay);
            for (var i = 0; i < 24; i++)
            {
                var next = BillingPeriod.For(p.EndExclusive, startDay);
                Assert.Equal(p.EndExclusive, next.Start);
                Assert.InRange(next.TotalDays, 28, 31);
                p = next;
            }
        }
    }

    [Fact]
    public void Forecast_IsLinear()
    {
        var p = new BillingPeriod(new DateOnly(2026, 4, 1), new DateOnly(2026, 5, 1)); // 30 дней
        Assert.Equal(20_000, p.Forecast(10_000, new DateTime(2026, 4, 16, 0, 0, 0))); // прошло 15 дней
    }

    [Theory]
    [InlineData(0, "0")]
    [InlineData(850, "850")]
    [InlineData(1024, "1K")]
    [InlineData(1536, "1.5K")]
    [InlineData(12 * 1024 * 1024, "12M")]
    [InlineData(999 * 1024, "999K")]
    public void TrayFormat_Bytes(double bytesPerSec, string expected)
    {
        Assert.Equal(expected, UnitFormatter.FormatTray(bytesPerSec, SpeedUnits.Bytes));
    }

    [Fact]
    public void TrayFormat_Bits()
    {
        Assert.Equal("8M", UnitFormatter.FormatTray(1_000_000, SpeedUnits.Bits));
    }

    [Fact]
    public void FormatBytes_English()
    {
        var prev = UnitFormatter.UseRussian;
        UnitFormatter.UseRussian = false;
        try
        {
            Assert.Equal("1.50 KB", UnitFormatter.FormatBytes(1536));
            Assert.Equal("10.0 Mbps", UnitFormatter.FormatSpeed(1_250_000, SpeedUnits.Bits));
        }
        finally
        {
            UnitFormatter.UseRussian = prev;
        }
    }
}
