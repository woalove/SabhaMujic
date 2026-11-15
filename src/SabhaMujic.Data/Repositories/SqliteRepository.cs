using Microsoft.Data.Sqlite;

namespace SabhaMujic.Data.Repositories;

/// <summary>Shared plumbing for the SQLite repositories: one pooled connection per operation.</summary>
public abstract class SqliteRepository
{
    protected SqliteRepository(SqliteDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        Database = database;
    }

    protected SqliteDatabase Database { get; }

    /// <summary>Current UTC time at the storage precision (milliseconds), so returned values equal stored ones.</summary>
    protected DateTimeOffset Now() => DapperTypeHandlers.FromDbTime(Database.Clock.GetUtcNow().ToUnixTimeMilliseconds());

    protected Task<SqliteConnection> OpenAsync(CancellationToken ct) => Database.OpenConnectionAsync(ct);
}
