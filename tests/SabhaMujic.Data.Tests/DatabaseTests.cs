using Dapper;
using Microsoft.Data.Sqlite;

namespace SabhaMujic.Data.Tests;

public sealed class DatabaseTests : IDisposable
{
    private static readonly string[] ExpectedTables =
    [
        "audit_log", "categories", "devices", "pairing_codes", "playback_queue", "playback_state", "playlist_items",
        "playlists", "schema_migrations", "sessions", "settings", "track_analysis", "track_loop_overrides", "tracks", "users",
    ];

    private readonly TempDirectory _dir = new();
    private readonly SqliteDatabase _db;

    public DatabaseTests() => _db = new SqliteDatabase(Path.Combine(_dir.Path, "nested", "test.db"), new FakeClock());

    public void Dispose()
    {
        _db.ClearPool();
        _dir.Dispose();
    }

    [Fact]
    public async Task Migrations_apply_on_an_empty_database()
    {
        Assert.Equal(0, await _db.GetSchemaVersionAsync());
        var version = await _db.MigrateAsync();
        Assert.Equal(SqliteDatabase.LatestSchemaVersion, version);
        Assert.True(SqliteDatabase.LatestSchemaVersion >= 1);

        await using var conn = await _db.OpenConnectionAsync();
        var tables = (await conn.QueryAsync<string>(
            "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name;")).ToArray();
        Assert.Equal(ExpectedTables, tables);
        var recorded = (await conn.QueryAsync<long>("SELECT version FROM schema_migrations ORDER BY version;")).ToArray();
        Assert.Equal(Enumerable.Range(1, version).Select(v => (long)v), recorded);
    }

    [Fact]
    public async Task Migrations_are_idempotent()
    {
        await _db.MigrateAsync();
        var again = await _db.MigrateAsync();
        Assert.Equal(SqliteDatabase.LatestSchemaVersion, again);

        await using var conn = await _db.OpenConnectionAsync();
        Assert.Equal(SqliteDatabase.LatestSchemaVersion, await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM schema_migrations;"));
        Assert.Equal(1, await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM playback_state;"));
    }

    [Fact]
    public async Task Concurrent_migrators_apply_each_migration_once()
    {
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(() => _db.MigrateAsync())));
        Assert.All(results, v => Assert.Equal(SqliteDatabase.LatestSchemaVersion, v));
        await using var conn = await _db.OpenConnectionAsync();
        Assert.Equal(SqliteDatabase.LatestSchemaVersion, await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM schema_migrations;"));
    }

    [Fact]
    public async Task A_database_newer_than_the_code_is_refused()
    {
        await _db.MigrateAsync();
        await using (var conn = await _db.OpenConnectionAsync())
        {
            await conn.ExecuteAsync($"PRAGMA user_version = {SqliteDatabase.LatestSchemaVersion + 1};");
        }

        var ex = await Assert.ThrowsAsync<SchemaVersionException>(() => _db.MigrateAsync());
        Assert.Equal(SqliteDatabase.LatestSchemaVersion + 1, ex.DatabaseVersion);
        Assert.Equal(SqliteDatabase.LatestSchemaVersion, ex.SupportedVersion);
    }

    [Fact]
    public async Task A_failing_migration_is_rolled_back_completely()
    {
        var good = SchemaMigrator.Embedded;
        var broken = good.Append(new Migration(good.Count + 1, "broken",
            "CREATE TABLE half_done (x INTEGER); INSERT INTO no_such_table VALUES (1);")).ToArray();

        await Assert.ThrowsAsync<SqliteException>(() => _db.MigrateAsync(broken, CancellationToken.None));

        Assert.Equal(good.Count, await _db.GetSchemaVersionAsync());
        await using var conn = await _db.OpenConnectionAsync();
        Assert.Equal(0, await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM sqlite_master WHERE name = 'half_done';"));
        Assert.Equal(good.Count, await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM schema_migrations;"));
    }

    [Fact]
    public async Task Every_connection_gets_the_required_pragmas()
    {
        await _db.MigrateAsync();
        await using var conn = await _db.OpenConnectionAsync();
        Assert.Equal("wal", await conn.ExecuteScalarAsync<string>("PRAGMA journal_mode;"));
        Assert.Equal(1, await conn.ExecuteScalarAsync<long>("PRAGMA foreign_keys;"));
        Assert.Equal(5000, await conn.ExecuteScalarAsync<long>("PRAGMA busy_timeout;"));
        Assert.Equal(1, await conn.ExecuteScalarAsync<long>("PRAGMA synchronous;")); // NORMAL
    }

    [Fact]
    public async Task Guids_and_times_use_the_documented_representation()
    {
        await _db.MigrateAsync();
        var id = Guid.NewGuid();
        var when = new DateTimeOffset(2026, 1, 2, 3, 4, 5, 678, TimeSpan.FromHours(5.5));
        await using var conn = await _db.OpenConnectionAsync();
        await conn.ExecuteAsync("INSERT INTO settings (key, value, updated_at) VALUES ('k', @Id, @When);", new { Id = id, When = when });

        var raw = await conn.QuerySingleAsync<(string Value, string ValueType, long UpdatedAt, string TimeType)>(
            "SELECT value, typeof(value), updated_at, typeof(updated_at) FROM settings WHERE key = 'k';");
        Assert.Equal(id.ToString("D").ToLowerInvariant(), raw.Value);
        Assert.Equal("text", raw.ValueType);
        Assert.Equal(when.ToUnixTimeMilliseconds(), raw.UpdatedAt);
        Assert.Equal("integer", raw.TimeType);

        var parsed = await conn.QuerySingleAsync<GuidTimeRow>("SELECT value AS Id, updated_at AS At FROM settings WHERE key = 'k';");
        Assert.Equal(id, parsed.Id);
        Assert.Equal(when, parsed.At);
        Assert.Equal(TimeSpan.Zero, parsed.At.Offset);
    }

    [Fact]
    public async Task Concurrent_writers_on_separate_connections_all_succeed()
    {
        await _db.MigrateAsync();
        var settings = new Repositories.SqliteSettingsRepository(_db);
        await Task.WhenAll(Enumerable.Range(0, 40).Select(i => Task.Run(() => settings.SetAsync($"key{i}", $"v{i}"))));
        Assert.Equal(40, (await settings.GetAllAsync()).Count);
    }

    private sealed class GuidTimeRow
    {
        public Guid Id { get; set; }

        public DateTimeOffset At { get; set; }
    }
}
