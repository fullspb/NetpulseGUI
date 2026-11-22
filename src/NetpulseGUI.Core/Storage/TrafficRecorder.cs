using Microsoft.Data.Sqlite;
using NetpulseGUI.Core.Models;
using NetpulseGUI.Core.Time;

namespace NetpulseGUI.Core.Storage;

/// <summary>
/// Буферизует посекундные дельты и пакетно (раз в 10–30 сек) записывает их сразу во все уровни
/// агрегации — секунды → минуты → часы → дни — одной транзакцией. Итоги «Сегодня/Месяц» читаются
/// мгновенно из дневной таблицы, а компакция сводится к удалению устаревших детальных уровней.
/// </summary>
public sealed class TrafficRecorder
{
    private readonly record struct AdapterRow(long Ts, AdapterDelta Delta);
    private readonly record struct AppRow(long Ts, AppDelta Delta);

    /// <summary>Максимум строк в буфере (защита памяти, если БД долго недоступна).</summary>
    public const int MaxBufferedRows = 300_000;

    private readonly Database _db;
    private readonly TimeZoneInfo? _zone;
    private readonly object _sync = new();
    private readonly object _flushSync = new();
    private List<AdapterRow> _adapterRows = new();
    private List<AppRow> _appRows = new();
    private readonly Dictionary<string, long> _adapterIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _appIds = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _lastTzRefresh = DateTime.UtcNow;

    /// <param name="db">База данных.</param>
    /// <param name="zone">Часовой пояс для дневных бакетов (по умолчанию системный).</param>
    public TrafficRecorder(Database db, TimeZoneInfo? zone = null)
    {
        _db = db;
        _zone = zone;
    }

    public Database Database => _db;

    public int PendingCount
    {
        get { lock (_sync) return _adapterRows.Count + _appRows.Count; }
    }

    /// <summary>Незаписанные байты (для точных итогов «Сегодня» между сбросами буфера).</summary>
    public TrafficTotals PendingTotals
    {
        get
        {
            lock (_sync)
            {
                long rx = 0, tx = 0;
                foreach (var r in _adapterRows) { rx += r.Delta.Rx; tx += r.Delta.Tx; }
                return new TrafficTotals(rx, tx);
            }
        }
    }

    public void AddAdapterTick(DateTime timestampUtc, IEnumerable<AdapterDelta> deltas)
    {
        var ts = TimeBuckets.ToUnix(timestampUtc);
        lock (_sync)
        {
            foreach (var d in deltas)
                if (d.Rx > 0 || d.Tx > 0)
                    _adapterRows.Add(new AdapterRow(ts, d));
            Trim(_adapterRows);
        }
    }

    public void AddAppTick(DateTime timestampUtc, IEnumerable<AppDelta> deltas)
    {
        var ts = TimeBuckets.ToUnix(timestampUtc);
        lock (_sync)
        {
            foreach (var d in deltas)
                if (d.Rx > 0 || d.Tx > 0)
                    _appRows.Add(new AppRow(ts, d));
            Trim(_appRows);
        }
    }

    /// <summary>Отбросить накопленное.</summary>
    public void Discard()
    {
        lock (_sync)
        {
            _adapterRows.Clear();
            _appRows.Clear();
        }
    }

    /// <summary>Сбросить кэш идентификаторов (после сброса статистики или восстановления БД).</summary>
    public void InvalidateCaches()
    {
        lock (_flushSync)
        {
            _adapterIds.Clear();
            _appIds.Clear();
        }
    }

    /// <summary>Записать буфер в БД. Возвращает число записанных исходных строк.</summary>
    public int Flush()
    {
        lock (_flushSync)
        {
            List<AdapterRow> adapters;
            List<AppRow> apps;
            lock (_sync)
            {
                if (_adapterRows.Count == 0 && _appRows.Count == 0) return 0;
                adapters = _adapterRows;
                apps = _appRows;
                _adapterRows = new List<AdapterRow>();
                _appRows = new List<AppRow>();
            }

            if (DateTime.UtcNow - _lastTzRefresh > TimeSpan.FromMinutes(10))
            {
                // Подхватываем смену часового пояса системы.
                TimeZoneInfo.ClearCachedData();
                _lastTzRefresh = DateTime.UtcNow;
            }

            try
            {
                using var c = _db.Open();
                using var tx = c.BeginTransaction(deferred: false);
                var now = TimeBuckets.ToUnix(DateTime.UtcNow);
                WriteAdapters(c, tx, adapters, now);
                WriteApps(c, tx, apps, now);
                tx.Commit();
                return adapters.Count + apps.Count;
            }
            catch
            {
                _adapterIds.Clear();
                _appIds.Clear();
                // Возвращаем данные в буфер, чтобы не потерять их при временной ошибке.
                lock (_sync)
                {
                    _adapterRows.InsertRange(0, adapters);
                    _appRows.InsertRange(0, apps);
                    Trim(_adapterRows);
                    Trim(_appRows);
                }
                throw;
            }
        }
    }

    private static void Trim<T>(List<T> list)
    {
        if (list.Count > MaxBufferedRows) list.RemoveRange(0, list.Count - MaxBufferedRows);
    }

    private void WriteAdapters(SqliteConnection c, SqliteTransaction tx, List<AdapterRow> rows, long now)
    {
        if (rows.Count == 0) return;

        var raw = new Dictionary<(long, long), (long rx, long tx)>();
        var minute = new Dictionary<(long, long), (long rx, long tx)>();
        var hour = new Dictionary<(long, long), (long rx, long tx)>();
        var day = new Dictionary<(string, long), (long rx, long tx)>();

        foreach (var row in rows)
        {
            var id = GetAdapterId(c, tx, row.Delta, now);
            Add(raw, (row.Ts, id), row.Delta.Rx, row.Delta.Tx);
            Add(minute, (TimeBuckets.FloorMinute(row.Ts), id), row.Delta.Rx, row.Delta.Tx);
            Add(hour, (TimeBuckets.FloorHour(row.Ts), id), row.Delta.Rx, row.Delta.Tx);
            Add(day, (TimeBuckets.DayKey(TimeBuckets.FromUnix(row.Ts), _zone), id), row.Delta.Rx, row.Delta.Tx);
        }

        Upsert(c, tx, "traffic_raw", "adapter_id", raw);
        Upsert(c, tx, "traffic_minute", "adapter_id", minute);
        Upsert(c, tx, "traffic_hour", "adapter_id", hour);
        UpsertDay(c, tx, "traffic_day", "adapter_id", day);
    }

    private void WriteApps(SqliteConnection c, SqliteTransaction tx, List<AppRow> rows, long now)
    {
        if (rows.Count == 0) return;

        var raw = new Dictionary<(long, long), (long rx, long tx)>();
        var minute = new Dictionary<(long, long), (long rx, long tx)>();
        var hour = new Dictionary<(long, long), (long rx, long tx)>();
        var day = new Dictionary<(string, long), (long rx, long tx)>();

        foreach (var row in rows)
        {
            var id = GetAppId(c, tx, row.Delta, now);
            Add(raw, (row.Ts, id), row.Delta.Rx, row.Delta.Tx);
            Add(minute, (TimeBuckets.FloorMinute(row.Ts), id), row.Delta.Rx, row.Delta.Tx);
            Add(hour, (TimeBuckets.FloorHour(row.Ts), id), row.Delta.Rx, row.Delta.Tx);
            Add(day, (TimeBuckets.DayKey(TimeBuckets.FromUnix(row.Ts), _zone), id), row.Delta.Rx, row.Delta.Tx);
        }

        Upsert(c, tx, "app_raw", "app_id", raw);
        Upsert(c, tx, "app_minute", "app_id", minute);
        Upsert(c, tx, "app_hour", "app_id", hour);
        UpsertDay(c, tx, "app_day", "app_id", day);
    }

    private long GetAdapterId(SqliteConnection c, SqliteTransaction tx, AdapterDelta d, long now)
    {
        if (_adapterIds.TryGetValue(d.Id, out var id)) return id;
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO adapters(guid, name, description, type, mac, first_seen, last_seen)
            VALUES($g, $n, $d, $t, $m, $now, $now)
            ON CONFLICT(guid) DO UPDATE SET name = excluded.name, description = excluded.description,
                type = excluded.type, mac = excluded.mac, last_seen = excluded.last_seen
            RETURNING id
            """;
        cmd.Parameters.AddWithValue("$g", d.Id);
        cmd.Parameters.AddWithValue("$n", d.Name);
        cmd.Parameters.AddWithValue("$d", d.Description);
        cmd.Parameters.AddWithValue("$t", d.Type);
        cmd.Parameters.AddWithValue("$m", d.Mac);
        cmd.Parameters.AddWithValue("$now", now);
        id = Convert.ToInt64(cmd.ExecuteScalar());
        _adapterIds[d.Id] = id;
        return id;
    }

    private long GetAppId(SqliteConnection c, SqliteTransaction tx, AppDelta d, long now)
    {
        var key = string.IsNullOrEmpty(d.Path) ? d.ExeName : d.Path;
        if (_appIds.TryGetValue(key, out var id)) return id;
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO apps(exe_name, path, first_seen, last_seen) VALUES($n, $p, $now, $now)
            ON CONFLICT(path) DO UPDATE SET exe_name = excluded.exe_name, last_seen = excluded.last_seen
            RETURNING id
            """;
        cmd.Parameters.AddWithValue("$n", d.ExeName);
        cmd.Parameters.AddWithValue("$p", key);
        cmd.Parameters.AddWithValue("$now", now);
        id = Convert.ToInt64(cmd.ExecuteScalar());
        _appIds[key] = id;
        return id;
    }

    private static void Add<TKey>(Dictionary<TKey, (long rx, long tx)> map, TKey key, long rx, long tx) where TKey : notnull
    {
        map[key] = map.TryGetValue(key, out var v) ? (v.rx + rx, v.tx + tx) : (rx, tx);
    }

    private static void Upsert(SqliteConnection c, SqliteTransaction tx, string table, string idCol,
        Dictionary<(long ts, long id), (long rx, long tx)> data)
    {
        if (data.Count == 0) return;
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"""
            INSERT INTO {table}(ts, {idCol}, rx, tx) VALUES($ts, $id, $rx, $tx)
            ON CONFLICT(ts, {idCol}) DO UPDATE SET rx = rx + excluded.rx, tx = tx + excluded.tx
            """;
        var pTs = cmd.Parameters.Add("$ts", SqliteType.Integer);
        var pId = cmd.Parameters.Add("$id", SqliteType.Integer);
        var pRx = cmd.Parameters.Add("$rx", SqliteType.Integer);
        var pTx = cmd.Parameters.Add("$tx", SqliteType.Integer);
        cmd.Prepare();
        foreach (var kv in data)
        {
            pTs.Value = kv.Key.ts;
            pId.Value = kv.Key.id;
            pRx.Value = kv.Value.rx;
            pTx.Value = kv.Value.tx;
            cmd.ExecuteNonQuery();
        }
    }

    private static void UpsertDay(SqliteConnection c, SqliteTransaction tx, string table, string idCol,
        Dictionary<(string day, long id), (long rx, long tx)> data)
    {
        if (data.Count == 0) return;
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"""
            INSERT INTO {table}(day, {idCol}, rx, tx) VALUES($day, $id, $rx, $tx)
            ON CONFLICT(day, {idCol}) DO UPDATE SET rx = rx + excluded.rx, tx = tx + excluded.tx
            """;
        var pDay = cmd.Parameters.Add("$day", SqliteType.Text);
        var pId = cmd.Parameters.Add("$id", SqliteType.Integer);
        var pRx = cmd.Parameters.Add("$rx", SqliteType.Integer);
        var pTx = cmd.Parameters.Add("$tx", SqliteType.Integer);
        cmd.Prepare();
        foreach (var kv in data)
        {
            pDay.Value = kv.Key.day;
            pId.Value = kv.Key.id;
            pRx.Value = kv.Value.rx;
            pTx.Value = kv.Value.tx;
            cmd.ExecuteNonQuery();
        }
    }
}
