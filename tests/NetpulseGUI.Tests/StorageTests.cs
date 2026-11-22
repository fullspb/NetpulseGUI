using Dapper;
using NetpulseGUI.Core.Models;
using NetpulseGUI.Core.Storage;
using NetpulseGUI.Core.Time;

namespace NetpulseGUI.Tests;

public sealed class StorageTests : IDisposable
{
    private readonly string _dir;
    private readonly Database _db;
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    public StorageTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "netpulse-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _db = new Database(Path.Combine(_dir, "test.db"));
        _db.EnsureCreated();
    }

    public void Dispose()
    {
        Database.ClearPools();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static AdapterDelta Eth(long rx, long tx) => new("{eth}", "Ethernet", "Intel", "Ethernet", "AA", rx, tx);

    [Fact]
    public void Flush_WritesAllAggregationLevels()
    {
        var rec = new TrafficRecorder(_db, Utc);
        var t = new DateTime(2026, 5, 10, 12, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < 120; i++)
            rec.AddAdapterTick(t.AddSeconds(i), new[] { Eth(100, 10) });
        Assert.Equal(120, rec.Flush());

        using var c = _db.Open();
        Assert.Equal(120, c.ExecuteScalar<long>("SELECT COUNT(*) FROM traffic_raw"));
        Assert.Equal(2, c.ExecuteScalar<long>("SELECT COUNT(*) FROM traffic_minute"));
        Assert.Equal(1, c.ExecuteScalar<long>("SELECT COUNT(*) FROM traffic_hour"));
        Assert.Equal(12_000, c.ExecuteScalar<long>("SELECT SUM(rx) FROM traffic_minute"));
        Assert.Equal(12_000, c.ExecuteScalar<long>("SELECT rx FROM traffic_hour"));
        Assert.Equal(1_200, c.ExecuteScalar<long>("SELECT tx FROM traffic_day WHERE day='2026-05-10'"));
    }

    [Fact]
    public void RepeatedFlushes_AccumulateIntoSameBuckets()
    {
        var rec = new TrafficRecorder(_db, Utc);
        var t = new DateTime(2026, 5, 10, 12, 0, 0, DateTimeKind.Utc);
        rec.AddAdapterTick(t, new[] { Eth(100, 1) });
        rec.Flush();
        rec.AddAdapterTick(t.AddSeconds(5), new[] { Eth(300, 3) });
        rec.Flush();
        // Тот же ts повторно (например, дубль секунды) — суммируется, а не падает на PK.
        rec.AddAdapterTick(t.AddSeconds(5), new[] { Eth(1, 0) });
        rec.Flush();

        using var c = _db.Open();
        Assert.Equal(401, c.ExecuteScalar<long>("SELECT rx FROM traffic_minute"));
        Assert.Equal(401, c.ExecuteScalar<long>("SELECT rx FROM traffic_day"));
        Assert.Equal(301, c.ExecuteScalar<long>("SELECT rx FROM traffic_raw WHERE ts=@ts", new { ts = TimeBuckets.ToUnix(t.AddSeconds(5)) }));
    }

    [Fact]
    public void DayBoundary_SplitsByLocalDate()
    {
        var plus3 = TimeZoneInfo.CreateCustomTimeZone("P3", TimeSpan.FromHours(3), "P3", "P3");
        var rec = new TrafficRecorder(_db, plus3);
        // 20:59:59 UTC = 23:59:59 по +3; 21:00:00 UTC = 00:00 следующих суток.
        rec.AddAdapterTick(new DateTime(2026, 5, 10, 20, 59, 59, DateTimeKind.Utc), new[] { Eth(10, 0) });
        rec.AddAdapterTick(new DateTime(2026, 5, 10, 21, 0, 0, DateTimeKind.Utc), new[] { Eth(20, 0) });
        rec.Flush();

        using var c = _db.Open();
        Assert.Equal(10, c.ExecuteScalar<long>("SELECT rx FROM traffic_day WHERE day='2026-05-10'"));
        Assert.Equal(20, c.ExecuteScalar<long>("SELECT rx FROM traffic_day WHERE day='2026-05-11'"));
    }

    [Fact]
    public async Task AppTotals_MergeByPath()
    {
        var rec = new TrafficRecorder(_db, Utc);
        var t = new DateTime(2026, 5, 10, 12, 0, 0, DateTimeKind.Utc);
        rec.AddAppTick(t, new[] { new AppDelta("chrome.exe", @"C:\Chrome\chrome.exe", 100, 10) });
        rec.AddAppTick(t.AddSeconds(1), new[] { new AppDelta("chrome.exe", @"c:\chrome\CHROME.EXE", 50, 5) });
        rec.AddAppTick(t.AddSeconds(1), new[] { new AppDelta("curl.exe", @"C:\curl.exe", 7, 1) });
        rec.Flush();

        var repo = new TrafficRepository(_db);
        var apps = await repo.GetAppTotalsAsync(new DateOnly(2026, 5, 10), new DateOnly(2026, 5, 10));
        Assert.Equal(2, apps.Count);
        Assert.Equal("chrome.exe", apps[0].ExeName);
        Assert.Equal(150, apps[0].Rx);
        Assert.Equal(15, apps[0].Tx);
    }

    [Fact]
    public async Task Totals_And_Series()
    {
        var rec = new TrafficRecorder(_db, Utc);
        var t = new DateTime(2026, 5, 10, 12, 0, 0, DateTimeKind.Utc);
        rec.AddAdapterTick(t, new[] { Eth(1000, 100) });
        rec.AddAdapterTick(t.AddDays(1), new[] { Eth(2000, 200) });
        rec.AddAdapterTick(t.AddHours(1), new[] { Eth(500, 50) });
        rec.Flush();

        var repo = new TrafficRepository(_db);
        var day1 = await repo.GetTotalsAsync(new DateOnly(2026, 5, 10), new DateOnly(2026, 5, 10));
        Assert.Equal(1500, day1.Rx);
        var all = await repo.GetAllTimeTotalsAsync();
        Assert.Equal(3500, all.Rx);
        Assert.Equal(350, all.Tx);

        var hours = await repo.GetSeriesAsync(Granularity.Hour, t, t.AddHours(2));
        Assert.Equal(2, hours.Count);
        Assert.Equal(1000, hours[0].Rx);

        var daily = await repo.GetDailySeriesAsync(new DateOnly(2026, 5, 1), new DateOnly(2026, 5, 31));
        Assert.Equal(2, daily.Count);
    }

    [Fact]
    public void Compactor_RemovesOnlyExpiredLevels()
    {
        var rec = new TrafficRecorder(_db, Utc);
        var now = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        rec.AddAdapterTick(now.AddDays(-40), new[] { Eth(1, 1) }); // старше 30 дней
        rec.AddAdapterTick(now.AddDays(-2), new[] { Eth(2, 2) });  // старше 24 часов
        rec.AddAdapterTick(now.AddMinutes(-5), new[] { Eth(3, 3) }); // свежее
        rec.AddAppTick(now.AddDays(-2), new[] { new AppDelta("a.exe", "a.exe", 5, 5) });
        rec.Flush();

        var result = new Compactor(_db).Compact(now);
        Assert.Equal(2, result.RawDeleted);
        Assert.Equal(1, result.MinuteDeleted);
        Assert.Equal(1, result.AppRawDeleted);

        using var c = _db.Open();
        Assert.Equal(1, c.ExecuteScalar<long>("SELECT COUNT(*) FROM traffic_raw"));
        Assert.Equal(2, c.ExecuteScalar<long>("SELECT COUNT(*) FROM traffic_minute"));
        Assert.Equal(3, c.ExecuteScalar<long>("SELECT COUNT(*) FROM traffic_hour")); // часы бессрочно
        Assert.Equal(3, c.ExecuteScalar<long>("SELECT COUNT(*) FROM traffic_day"));  // дни бессрочно
        Assert.Equal(1, c.ExecuteScalar<long>("SELECT COUNT(*) FROM app_minute"));
    }

    [Fact]
    public void Compactor_RepairsMissingHourBuckets()
    {
        var rec = new TrafficRecorder(_db, Utc);
        var t = new DateTime(2026, 5, 10, 12, 0, 0, DateTimeKind.Utc);
        rec.AddAdapterTick(t, new[] { Eth(10, 1) });
        rec.AddAdapterTick(t.AddMinutes(5), new[] { Eth(20, 2) });
        rec.Flush();
        using (var c = _db.Open()) c.Execute("DELETE FROM traffic_hour");

        new Compactor(_db).Compact(t.AddMinutes(10));

        using var c2 = _db.Open();
        Assert.Equal(30, c2.ExecuteScalar<long>("SELECT rx FROM traffic_hour"));
    }

    [Fact]
    public async Task ResetAndBackupRestore()
    {
        var rec = new TrafficRecorder(_db, Utc);
        rec.AddAdapterTick(new DateTime(2026, 5, 10, 12, 0, 0, DateTimeKind.Utc), new[] { Eth(42, 4) });
        rec.Flush();
        var repo = new TrafficRepository(_db);

        var backup = Path.Combine(_dir, "backup.db");
        await repo.BackupAsync(backup);
        await repo.ResetAllAsync();
        Assert.Equal(0, (await repo.GetAllTimeTotalsAsync()).Rx);

        await repo.RestoreAsync(backup);
        Assert.Equal(42, (await repo.GetAllTimeTotalsAsync()).Rx);
    }

    [Fact]
    public void ZeroDeltas_AreNotStored()
    {
        var rec = new TrafficRecorder(_db, Utc);
        rec.AddAdapterTick(DateTime.UtcNow, new[] { Eth(0, 0) });
        Assert.Equal(0, rec.PendingCount);
    }

    [Fact]
    public void CsvEscape()
    {
        Assert.Equal("plain", CsvExporter.Escape("plain"));
        Assert.Equal("\"a;b\"", CsvExporter.Escape("a;b"));
        Assert.Equal("\"say \"\"hi\"\"\"", CsvExporter.Escape("say \"hi\""));
    }
}
