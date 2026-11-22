using Dapper;
using Microsoft.Data.Sqlite;
using NetpulseGUI.Core.Models;
using NetpulseGUI.Core.Time;

namespace NetpulseGUI.Core.Storage;

/// <summary>Запросы для отчётов и UI. Все методы асинхронные и не держат соединение дольше запроса.</summary>
public sealed class TrafficRepository
{
    private readonly Database _db;

    public TrafficRepository(Database db) => _db = db;

    public Database Database => _db;

    private sealed class RxTx
    {
        public long Rx { get; set; }
        public long Tx { get; set; }
    }

    private sealed class TsRow
    {
        public long Ts { get; set; }
        public long Rx { get; set; }
        public long Tx { get; set; }
    }

    private sealed class DayRow
    {
        public string Day { get; set; } = "";
        public long Rx { get; set; }
        public long Tx { get; set; }
    }

    /// <summary>Итоги по локальным датам [from; toInclusive].</summary>
    public async Task<TrafficTotals> GetTotalsAsync(DateOnly from, DateOnly toInclusive, string? adapterGuid = null, CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct).ConfigureAwait(false);
        var sql = adapterGuid == null
            ? "SELECT COALESCE(SUM(rx),0) AS Rx, COALESCE(SUM(tx),0) AS Tx FROM traffic_day WHERE day BETWEEN @f AND @t"
            : "SELECT COALESCE(SUM(d.rx),0) AS Rx, COALESCE(SUM(d.tx),0) AS Tx FROM traffic_day d JOIN adapters a ON a.id = d.adapter_id WHERE d.day BETWEEN @f AND @t AND a.guid = @g";
        var r = await c.QuerySingleAsync<RxTx>(new CommandDefinition(sql,
            new { f = TimeBuckets.DayKey(from), t = TimeBuckets.DayKey(toInclusive), g = adapterGuid }, cancellationToken: ct)).ConfigureAwait(false);
        return new TrafficTotals(r.Rx, r.Tx);
    }

    public async Task<TrafficTotals> GetAllTimeTotalsAsync(CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct).ConfigureAwait(false);
        var r = await c.QuerySingleAsync<RxTx>(new CommandDefinition(
            "SELECT COALESCE(SUM(rx),0) AS Rx, COALESCE(SUM(tx),0) AS Tx FROM traffic_day", cancellationToken: ct)).ConfigureAwait(false);
        return new TrafficTotals(r.Rx, r.Tx);
    }

    /// <summary>Итоги за произвольный интервал UTC по минутной или часовой таблице.</summary>
    public async Task<TrafficTotals> GetTotalsAsync(DateTime fromUtc, DateTime toUtc, Granularity granularity, CancellationToken ct = default)
    {
        var table = TableFor(granularity);
        await using var c = await _db.OpenAsync(ct).ConfigureAwait(false);
        var r = await c.QuerySingleAsync<RxTx>(new CommandDefinition(
            $"SELECT COALESCE(SUM(rx),0) AS Rx, COALESCE(SUM(tx),0) AS Tx FROM {table} WHERE ts >= @f AND ts < @t",
            new { f = TimeBuckets.ToUnix(fromUtc), t = TimeBuckets.ToUnix(toUtc) }, cancellationToken: ct)).ConfigureAwait(false);
        return new TrafficTotals(r.Rx, r.Tx);
    }

    /// <summary>Временной ряд суммарного трафика (по всем адаптерам или по одному).</summary>
    public async Task<IReadOnlyList<SeriesPoint>> GetSeriesAsync(Granularity granularity, DateTime fromUtc, DateTime toUtc,
        string? adapterGuid = null, CancellationToken ct = default)
    {
        if (granularity == Granularity.Day)
            throw new ArgumentException("Для дневного ряда используйте GetDailySeriesAsync", nameof(granularity));
        var table = TableFor(granularity);
        await using var c = await _db.OpenAsync(ct).ConfigureAwait(false);
        var sql = adapterGuid == null
            ? $"SELECT ts AS Ts, SUM(rx) AS Rx, SUM(tx) AS Tx FROM {table} WHERE ts >= @f AND ts < @t GROUP BY ts ORDER BY ts"
            : $"SELECT d.ts AS Ts, SUM(d.rx) AS Rx, SUM(d.tx) AS Tx FROM {table} d JOIN adapters a ON a.id = d.adapter_id WHERE d.ts >= @f AND d.ts < @t AND a.guid = @g GROUP BY d.ts ORDER BY d.ts";
        var rows = await c.QueryAsync<TsRow>(new CommandDefinition(sql,
            new { f = TimeBuckets.ToUnix(fromUtc), t = TimeBuckets.ToUnix(toUtc), g = adapterGuid }, cancellationToken: ct)).ConfigureAwait(false);
        return rows.Select(r => new SeriesPoint(TimeBuckets.FromUnix(r.Ts), r.Rx, r.Tx)).ToList();
    }

    public async Task<IReadOnlyList<(DateOnly Day, TrafficTotals Totals)>> GetDailySeriesAsync(DateOnly from, DateOnly toInclusive, CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct).ConfigureAwait(false);
        var rows = await c.QueryAsync<DayRow>(new CommandDefinition(
            "SELECT day AS Day, SUM(rx) AS Rx, SUM(tx) AS Tx FROM traffic_day WHERE day BETWEEN @f AND @t GROUP BY day ORDER BY day",
            new { f = TimeBuckets.DayKey(from), t = TimeBuckets.DayKey(toInclusive) }, cancellationToken: ct)).ConfigureAwait(false);
        return rows.Select(r => (TimeBuckets.ParseDay(r.Day), new TrafficTotals(r.Rx, r.Tx))).ToList();
    }

    public async Task<IReadOnlyList<AdapterTotal>> GetAdapterTotalsAsync(DateOnly from, DateOnly toInclusive, CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct).ConfigureAwait(false);
        var rows = await c.QueryAsync<AdapterTotal>(new CommandDefinition("""
            SELECT a.id AS AdapterId, a.guid AS Guid, a.name AS Name, COALESCE(SUM(d.rx),0) AS Rx, COALESCE(SUM(d.tx),0) AS Tx
            FROM adapters a JOIN traffic_day d ON d.adapter_id = a.id
            WHERE d.day BETWEEN @f AND @t
            GROUP BY a.id
            """, new { f = TimeBuckets.DayKey(from), t = TimeBuckets.DayKey(toInclusive) }, cancellationToken: ct)).ConfigureAwait(false);
        return rows.ToList();
    }

    /// <summary>Итоги по приложениям за локальные даты [from; toInclusive].</summary>
    public async Task<IReadOnlyList<AppTotal>> GetAppTotalsAsync(DateOnly from, DateOnly toInclusive, CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct).ConfigureAwait(false);
        var rows = await c.QueryAsync<AppTotal>(new CommandDefinition("""
            SELECT a.id AS AppId, a.exe_name AS ExeName, a.path AS Path, SUM(d.rx) AS Rx, SUM(d.tx) AS Tx
            FROM app_day d JOIN apps a ON a.id = d.app_id
            WHERE d.day BETWEEN @f AND @t
            GROUP BY a.id
            ORDER BY SUM(d.rx) + SUM(d.tx) DESC
            """, new { f = TimeBuckets.DayKey(from), t = TimeBuckets.DayKey(toInclusive) }, cancellationToken: ct)).ConfigureAwait(false);
        return rows.ToList();
    }

    public async Task<AppTotal?> FindAppAsync(string pathOrName, CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct).ConfigureAwait(false);
        return await c.QueryFirstOrDefaultAsync<AppTotal>(new CommandDefinition(
            "SELECT id AS AppId, exe_name AS ExeName, path AS Path, 0 AS Rx, 0 AS Tx FROM apps WHERE path = @p",
            new { p = pathOrName }, cancellationToken: ct)).ConfigureAwait(false);
    }

    public async Task<TrafficTotals> GetAppTotalsAsync(long appId, DateOnly from, DateOnly toInclusive, CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct).ConfigureAwait(false);
        var r = await c.QuerySingleAsync<RxTx>(new CommandDefinition(
            "SELECT COALESCE(SUM(rx),0) AS Rx, COALESCE(SUM(tx),0) AS Tx FROM app_day WHERE app_id = @id AND day BETWEEN @f AND @t",
            new { id = appId, f = TimeBuckets.DayKey(from), t = TimeBuckets.DayKey(toInclusive) }, cancellationToken: ct)).ConfigureAwait(false);
        return new TrafficTotals(r.Rx, r.Tx);
    }

    public async Task<IReadOnlyList<SeriesPoint>> GetAppSeriesAsync(long appId, Granularity granularity, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
    {
        var table = granularity switch
        {
            Granularity.Second => "app_raw",
            Granularity.Minute => "app_minute",
            Granularity.Hour => "app_hour",
            _ => throw new ArgumentException("Для дневного ряда используйте GetAppDailySeriesAsync", nameof(granularity))
        };
        await using var c = await _db.OpenAsync(ct).ConfigureAwait(false);
        var rows = await c.QueryAsync<TsRow>(new CommandDefinition(
            $"SELECT ts AS Ts, rx AS Rx, tx AS Tx FROM {table} WHERE app_id = @id AND ts >= @f AND ts < @t ORDER BY ts",
            new { id = appId, f = TimeBuckets.ToUnix(fromUtc), t = TimeBuckets.ToUnix(toUtc) }, cancellationToken: ct)).ConfigureAwait(false);
        return rows.Select(r => new SeriesPoint(TimeBuckets.FromUnix(r.Ts), r.Rx, r.Tx)).ToList();
    }

    public async Task<IReadOnlyList<(DateOnly Day, TrafficTotals Totals)>> GetAppDailySeriesAsync(long appId, DateOnly from, DateOnly toInclusive, CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct).ConfigureAwait(false);
        var rows = await c.QueryAsync<DayRow>(new CommandDefinition(
            "SELECT day AS Day, rx AS Rx, tx AS Tx FROM app_day WHERE app_id = @id AND day BETWEEN @f AND @t ORDER BY day",
            new { id = appId, f = TimeBuckets.DayKey(from), t = TimeBuckets.DayKey(toInclusive) }, cancellationToken: ct)).ConfigureAwait(false);
        return rows.Select(r => (TimeBuckets.ParseDay(r.Day), new TrafficTotals(r.Rx, r.Tx))).ToList();
    }

    public async Task AddAlertAsync(string kind, string message, CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct).ConfigureAwait(false);
        await c.ExecuteAsync(new CommandDefinition("INSERT INTO alerts_log(ts, kind, message) VALUES(@ts, @k, @m)",
            new { ts = TimeBuckets.ToUnix(DateTime.UtcNow), k = kind, m = message }, cancellationToken: ct)).ConfigureAwait(false);
    }

    private sealed class AlertRow
    {
        public long Id { get; set; }
        public long Ts { get; set; }
        public string Kind { get; set; } = "";
        public string Message { get; set; } = "";
    }

    public async Task<IReadOnlyList<AlertEntry>> GetAlertsAsync(int limit = 100, CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct).ConfigureAwait(false);
        var rows = await c.QueryAsync<AlertRow>(new CommandDefinition(
            "SELECT id AS Id, ts AS Ts, kind AS Kind, message AS Message FROM alerts_log ORDER BY ts DESC LIMIT @l",
            new { l = limit }, cancellationToken: ct)).ConfigureAwait(false);
        return rows.Select(r => new AlertEntry { Id = r.Id, TimeUtc = TimeBuckets.FromUnix(r.Ts), Kind = r.Kind, Message = r.Message }).ToList();
    }

    public async Task<DateOnly?> GetFirstDayAsync(CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct).ConfigureAwait(false);
        var d = await c.ExecuteScalarAsync<string?>(new CommandDefinition("SELECT MIN(day) FROM traffic_day", cancellationToken: ct)).ConfigureAwait(false);
        return d == null ? null : TimeBuckets.ParseDay(d);
    }

    /// <summary>Полный сброс статистики (адаптеры, приложения, все уровни агрегации, журнал).</summary>
    public async Task ResetAllAsync(CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(ct).ConfigureAwait(false);
        foreach (var t in new[] { "traffic_raw", "traffic_minute", "traffic_hour", "traffic_day", "app_raw", "app_minute", "app_hour", "app_day", "alerts_log", "apps", "adapters" })
            await c.ExecuteAsync(new CommandDefinition($"DELETE FROM {t}", transaction: tx, cancellationToken: ct)).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        await c.ExecuteAsync(new CommandDefinition("PRAGMA incremental_vacuum;", cancellationToken: ct)).ConfigureAwait(false);
    }

    /// <summary>Онлайн-резервная копия (SQLite Backup API) — безопасна во время записи.</summary>
    public Task BackupAsync(string destinationPath, CancellationToken ct = default) => Task.Run(() =>
    {
        if (File.Exists(destinationPath)) File.Delete(destinationPath);
        using var src = _db.Open();
        using var dst = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = destinationPath, Pooling = false }.ToString());
        dst.Open();
        src.BackupDatabase(dst);
        dst.Execute("PRAGMA journal_mode=DELETE;");
    }, ct);

    /// <summary>Импорт резервной копии: проверяет файл и заменяет содержимое текущей БД.</summary>
    public Task RestoreAsync(string sourcePath, CancellationToken ct = default) => Task.Run(() =>
    {
        using (var src = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = sourcePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
        {
            src.Open();
            var ok = src.ExecuteScalar<long>("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('meta','traffic_day','adapters')");
            if (ok < 3) throw new InvalidDataException("Файл не является резервной копией NetpulseGUI.");
            using var dst = _db.Open();
            src.BackupDatabase(dst);
        }
        _db.EnsureCreated(); // применить миграции, если копия старее
    }, ct);

    private static string TableFor(Granularity g) => g switch
    {
        Granularity.Second => "traffic_raw",
        Granularity.Minute => "traffic_minute",
        Granularity.Hour => "traffic_hour",
        _ => throw new ArgumentOutOfRangeException(nameof(g))
    };
}
