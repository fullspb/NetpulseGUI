using Dapper;
using NetpulseGUI.Core.Time;

namespace NetpulseGUI.Core.Storage;

public sealed record RetentionPolicy(TimeSpan Raw, TimeSpan Minute)
{
    /// <summary>Сырые секундные данные — 24 часа, минутные — 30 дней; часовые и дневные — бессрочно.</summary>
    public static RetentionPolicy Default { get; } = new(TimeSpan.FromHours(24), TimeSpan.FromDays(30));
}

public sealed record CompactionResult(int RawDeleted, int MinuteDeleted, int AppRawDeleted, int AppMinuteDeleted);

/// <summary>
/// Фоновая компакция: удаляет устаревшие уровни детализации и освобождает место в файле.
/// Часовые/дневные агрегаты пишутся инкрементально при записи, поэтому здесь их не пересчитываем;
/// дополнительно восстанавливаются отсутствующие часовые бакеты из минутных (после сбоев/импорта).
/// </summary>
public sealed class Compactor
{
    private readonly Database _db;
    private readonly RetentionPolicy _policy;

    public Compactor(Database db, RetentionPolicy? policy = null)
    {
        _db = db;
        _policy = policy ?? RetentionPolicy.Default;
    }

    public CompactionResult Compact(DateTime nowUtc)
    {
        var now = TimeBuckets.ToUnix(nowUtc);
        var rawCutoff = now - (long)_policy.Raw.TotalSeconds;
        var minuteCutoff = now - (long)_policy.Minute.TotalSeconds;

        using var c = _db.Open();
        int raw, minute, appRaw, appMinute;
        using (var tx = c.BeginTransaction(deferred: false))
        {
            // Восстановление: часы, для которых есть минуты, но нет часовой записи.
            c.Execute("""
                INSERT INTO traffic_hour(ts, adapter_id, rx, tx)
                SELECT (m.ts / 3600) * 3600 AS h, m.adapter_id, SUM(m.rx), SUM(m.tx)
                FROM traffic_minute m
                WHERE NOT EXISTS (SELECT 1 FROM traffic_hour x WHERE x.ts = (m.ts / 3600) * 3600 AND x.adapter_id = m.adapter_id)
                GROUP BY h, m.adapter_id
                """, transaction: tx);
            c.Execute("""
                INSERT INTO app_hour(ts, app_id, rx, tx)
                SELECT (m.ts / 3600) * 3600 AS h, m.app_id, SUM(m.rx), SUM(m.tx)
                FROM app_minute m
                WHERE NOT EXISTS (SELECT 1 FROM app_hour x WHERE x.ts = (m.ts / 3600) * 3600 AND x.app_id = m.app_id)
                GROUP BY h, m.app_id
                """, transaction: tx);

            raw = c.Execute("DELETE FROM traffic_raw WHERE ts < @c", new { c = rawCutoff }, tx);
            appRaw = c.Execute("DELETE FROM app_raw WHERE ts < @c", new { c = rawCutoff }, tx);
            minute = c.Execute("DELETE FROM traffic_minute WHERE ts < @c", new { c = minuteCutoff }, tx);
            appMinute = c.Execute("DELETE FROM app_minute WHERE ts < @c", new { c = minuteCutoff }, tx);
            tx.Commit();
        }

        c.Execute("PRAGMA incremental_vacuum(2000);");
        c.Execute("PRAGMA optimize;");
        try { c.Execute("PRAGMA wal_checkpoint(PASSIVE);"); } catch { /* занято читателем — не критично */ }
        return new CompactionResult(raw, minute, appRaw, appMinute);
    }
}
