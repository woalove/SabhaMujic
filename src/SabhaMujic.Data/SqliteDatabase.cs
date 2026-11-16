using Microsoft.Data.Sqlite;

namespace SabhaMujic.Data;

/// <summary>
/// Connection factory for the SabhaMujic SQLite database file. Thread-safe: every operation opens its own
/// (pooled) connection, and each connection gets WAL, foreign keys, the busy timeout and synchronous=NORMAL.
/// Call <see cref="MigrateAsync"/> once at startup before using repositories.
/// <para>
/// Busy waiting: one deliberate limit, <see cref="BusyTimeoutSeconds"/> (5 s), used both for SQLite's
/// <c>busy_timeout</c> pragma and for Microsoft.Data.Sqlite's command timeout (which otherwise keeps retrying a busy
/// statement for 30 s). A statement blocked longer than that fails with SQLITE_BUSY; background writers (playback state,
/// analysis results) retry with backoff, and client calls fail fast with a "storage busy or full" message.
/// </para>
/// </summary>
public sealed class SqliteDatabase
{
    /// <summary>How long a statement waits for a lock held by another connection before failing with SQLITE_BUSY.</summary>
    public const int BusyTimeoutSeconds = 5;

    private const string ConnectionPragmas =
        "PRAGMA busy_timeout = 5000; PRAGMA foreign_keys = ON; PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL;";

    static SqliteDatabase() => DapperTypeHandlers.EnsureRegistered();

    public SqliteDatabase(string databasePath, TimeProvider? clock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        DatabasePath = Path.GetFullPath(databasePath);
        Clock = clock ?? TimeProvider.System;

        var directory = Path.GetDirectoryName(DatabasePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        ConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = true,
            ForeignKeys = true,
            DefaultTimeout = BusyTimeoutSeconds, // must match busy_timeout in ConnectionPragmas (a test checks both)
        }.ToString();
    }

    public string DatabasePath { get; }

    public string ConnectionString { get; }

    /// <summary>Clock used for every timestamp the data layer writes or compares (expiry, audit, etc.).</summary>
    public TimeProvider Clock { get; }

    /// <summary>The newest schema version this build knows.</summary>
    public static int LatestSchemaVersion => SchemaMigrator.LatestVersion;

    public async Task<SqliteConnection> OpenConnectionAsync(CancellationToken ct = default)
    {
        var connection = new SqliteConnection(ConnectionString);
        try
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText = ConnectionPragmas;
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Applies pending migrations. Idempotent. Throws <see cref="SchemaVersionException"/> when the database is newer
    /// than <see cref="LatestSchemaVersion"/>. Returns the resulting schema version.
    /// </summary>
    public Task<int> MigrateAsync(CancellationToken ct = default) => MigrateAsync(SchemaMigrator.Embedded, ct);

    internal async Task<int> MigrateAsync(IReadOnlyList<Migration> migrations, CancellationToken ct)
    {
        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        return await SchemaMigrator.MigrateAsync(connection, migrations, Clock, ct).ConfigureAwait(false);
    }

    public async Task<int> GetSchemaVersionAsync(CancellationToken ct = default)
    {
        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        return await SchemaMigrator.GetVersionAsync(connection, null, ct).ConfigureAwait(false);
    }

    /// <summary>Releases pooled connections to this file (e.g. before deleting or replacing it).</summary>
    public void ClearPool()
    {
        using var connection = new SqliteConnection(ConnectionString);
        SqliteConnection.ClearPool(connection);
    }
}
