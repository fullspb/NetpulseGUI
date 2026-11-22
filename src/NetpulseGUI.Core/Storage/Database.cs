using Dapper;
using Microsoft.Data.Sqlite;

namespace NetpulseGUI.Core.Storage;

/// <summary>
/// Фабрика соединений SQLite и миграции схемы. Режим WAL позволяет UI читать отчёты,
/// пока фоновый поток сбора пишет; занятость БД обрабатывается таймаутом ожидания.
/// </summary>
public sealed class Database
{
    public const int SchemaVersion = 1;

    public string Path { get; }
    private readonly string _connectionString;

    public Database(string path)
    {
        Path = path;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = true,
            DefaultTimeout = 30
        }.ToString();
    }

    public SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA busy_timeout=10000; PRAGMA foreign_keys=ON; PRAGMA synchronous=NORMAL;";
        cmd.ExecuteNonQuery();
        return c;
    }

    public async Task<SqliteConnection> OpenAsync(CancellationToken ct = default)
    {
        var c = new SqliteConnection(_connectionString);
        await c.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA busy_timeout=10000; PRAGMA foreign_keys=ON; PRAGMA synchronous=NORMAL;";
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return c;
    }

    /// <summary>Создаёт файл и схему при необходимости. Идемпотентно.</summary>
    public void EnsureCreated()
    {
        var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        using var c = Open();
        c.Execute("PRAGMA auto_vacuum=INCREMENTAL;");
        c.Execute("PRAGMA journal_mode=WAL;");
        using var tx = c.BeginTransaction();
        c.Execute(Schema, transaction: tx);
        var version = c.ExecuteScalar<string?>("SELECT value FROM meta WHERE key='schema_version'", transaction: tx);
        if (version == null)
            c.Execute("INSERT INTO meta(key, value) VALUES('schema_version', @v)", new { v = SchemaVersion.ToString() }, tx);
        c.Execute("INSERT OR IGNORE INTO meta(key, value) VALUES('created_utc', @v)", new { v = DateTime.UtcNow.ToString("O") }, tx);
        tx.Commit();
    }

    public static void ClearPools() => SqliteConnection.ClearAllPools();

    internal const string Schema = """
        CREATE TABLE IF NOT EXISTS meta(
            key   TEXT PRIMARY KEY,
            value TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS adapters(
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            guid        TEXT NOT NULL UNIQUE COLLATE NOCASE,
            name        TEXT NOT NULL,
            description TEXT NOT NULL DEFAULT '',
            type        TEXT NOT NULL DEFAULT '',
            mac         TEXT NOT NULL DEFAULT '',
            first_seen  INTEGER NOT NULL,
            last_seen   INTEGER NOT NULL
        );

        -- Сырые секундные данные (хранятся 24 часа). ts — unix-время UTC.
        CREATE TABLE IF NOT EXISTS traffic_raw(
            ts         INTEGER NOT NULL,
            adapter_id INTEGER NOT NULL REFERENCES adapters(id) ON DELETE CASCADE,
            rx         INTEGER NOT NULL,
            tx         INTEGER NOT NULL,
            PRIMARY KEY (ts, adapter_id)
        ) WITHOUT ROWID;

        -- Минутные (30 дней), ts — начало минуты UTC.
        CREATE TABLE IF NOT EXISTS traffic_minute(
            ts         INTEGER NOT NULL,
            adapter_id INTEGER NOT NULL REFERENCES adapters(id) ON DELETE CASCADE,
            rx         INTEGER NOT NULL,
            tx         INTEGER NOT NULL,
            PRIMARY KEY (ts, adapter_id)
        ) WITHOUT ROWID;

        -- Часовые (бессрочно), ts — начало часа UTC.
        CREATE TABLE IF NOT EXISTS traffic_hour(
            ts         INTEGER NOT NULL,
            adapter_id INTEGER NOT NULL REFERENCES adapters(id) ON DELETE CASCADE,
            rx         INTEGER NOT NULL,
            tx         INTEGER NOT NULL,
            PRIMARY KEY (ts, adapter_id)
        ) WITHOUT ROWID;

        -- Дневные (бессрочно), day — локальная дата 'yyyy-MM-dd'.
        CREATE TABLE IF NOT EXISTS traffic_day(
            day        TEXT    NOT NULL,
            adapter_id INTEGER NOT NULL REFERENCES adapters(id) ON DELETE CASCADE,
            rx         INTEGER NOT NULL,
            tx         INTEGER NOT NULL,
            PRIMARY KEY (day, adapter_id)
        ) WITHOUT ROWID;

        CREATE TABLE IF NOT EXISTS apps(
            id         INTEGER PRIMARY KEY AUTOINCREMENT,
            exe_name   TEXT NOT NULL,
            path       TEXT NOT NULL UNIQUE COLLATE NOCASE,
            first_seen INTEGER NOT NULL,
            last_seen  INTEGER NOT NULL
        );

        -- Посекундный трафик процессов (24 часа).
        CREATE TABLE IF NOT EXISTS app_raw(
            ts     INTEGER NOT NULL,
            app_id INTEGER NOT NULL REFERENCES apps(id) ON DELETE CASCADE,
            rx     INTEGER NOT NULL,
            tx     INTEGER NOT NULL,
            PRIMARY KEY (ts, app_id)
        ) WITHOUT ROWID;

        CREATE TABLE IF NOT EXISTS app_minute(
            ts     INTEGER NOT NULL,
            app_id INTEGER NOT NULL REFERENCES apps(id) ON DELETE CASCADE,
            rx     INTEGER NOT NULL,
            tx     INTEGER NOT NULL,
            PRIMARY KEY (ts, app_id)
        ) WITHOUT ROWID;

        CREATE TABLE IF NOT EXISTS app_hour(
            ts     INTEGER NOT NULL,
            app_id INTEGER NOT NULL REFERENCES apps(id) ON DELETE CASCADE,
            rx     INTEGER NOT NULL,
            tx     INTEGER NOT NULL,
            PRIMARY KEY (ts, app_id)
        ) WITHOUT ROWID;

        CREATE TABLE IF NOT EXISTS app_day(
            day    TEXT    NOT NULL,
            app_id INTEGER NOT NULL REFERENCES apps(id) ON DELETE CASCADE,
            rx     INTEGER NOT NULL,
            tx     INTEGER NOT NULL,
            PRIMARY KEY (day, app_id)
        ) WITHOUT ROWID;

        CREATE INDEX IF NOT EXISTS ix_app_day_app ON app_day(app_id, day);
        CREATE INDEX IF NOT EXISTS ix_app_hour_app ON app_hour(app_id, ts);
        CREATE INDEX IF NOT EXISTS ix_app_minute_app ON app_minute(app_id, ts);

        CREATE TABLE IF NOT EXISTS alerts_log(
            id      INTEGER PRIMARY KEY AUTOINCREMENT,
            ts      INTEGER NOT NULL,
            kind    TEXT NOT NULL,
            message TEXT NOT NULL
        );
        """;
}
