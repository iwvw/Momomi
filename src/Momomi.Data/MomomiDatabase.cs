using Microsoft.Data.Sqlite;

namespace Momomi.Data;

public sealed class MomomiDatabase
{
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly string _connectionString;

    public string DatabasePath { get; }

    public MomomiDatabase(string databasePath)
    {
        DatabasePath = databasePath;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        }.ToString();
    }

    public static MomomiDatabase OpenDefault()
    {
        var dir = MomomiAppData.ResolveDirectory();
        Directory.CreateDirectory(dir);
        return new MomomiDatabase(Path.Combine(dir, "momomi.db"));
    }

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        await ExecuteWriteAsync(async conn =>
        {
            await using var wal = conn.CreateCommand();
            wal.CommandText = "PRAGMA journal_mode=WAL;";
            await wal.ExecuteNonQueryAsync().ConfigureAwait(false);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS app_settings (
                    key TEXT PRIMARY KEY,
                    value TEXT NOT NULL,
                    updated_at TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS profile (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    name TEXT NOT NULL,
                    kind TEXT NOT NULL,
                    source TEXT,
                    file_path TEXT NOT NULL,
                    subscription_userinfo TEXT,
                    is_active INTEGER NOT NULL DEFAULT 0,
                    sort_order INTEGER NOT NULL DEFAULT 0,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS traffic_minute (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    bucket TEXT NOT NULL,
                    up INTEGER NOT NULL,
                    down INTEGER NOT NULL,
                    memory INTEGER NOT NULL,
                    connections INTEGER NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_traffic_bucket ON traffic_minute(bucket);
                CREATE TABLE IF NOT EXISTS connection_history (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    ts TEXT NOT NULL,
                    host TEXT,
                    destination TEXT,
                    process TEXT,
                    rule TEXT,
                    chain TEXT,
                    upload INTEGER NOT NULL,
                    download INTEGER NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_conn_ts ON connection_history(ts);
                """;
            await cmd.ExecuteNonQueryAsync();
            return 0;
        }).ConfigureAwait(false);
    }

    public async Task<SqliteConnection> OpenAsync()
    {
        var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync().ConfigureAwait(false);
        await using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA busy_timeout=5000;";
        await pragma.ExecuteNonQueryAsync().ConfigureAwait(false);
        return conn;
    }

    public async Task<T> ExecuteWriteAsync<T>(Func<SqliteConnection, Task<T>> action)
    {
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await using var conn = await OpenAsync().ConfigureAwait(false);
            return await action(conn).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<T> ExecuteReadAsync<T>(Func<SqliteConnection, Task<T>> action)
    {
        await using var conn = await OpenAsync().ConfigureAwait(false);
        return await action(conn).ConfigureAwait(false);
    }
}
