using Dapper;
using SabhaMujic.Contracts;
using SabhaMujic.Data.Internal;

namespace SabhaMujic.Data.Repositories;

/// <summary>Simple key/value service settings.</summary>
public interface ISettingsRepository
{
    Task<string?> GetAsync(string key, CancellationToken ct = default);

    Task SetAsync(string key, string value, CancellationToken ct = default);

    Task<bool> DeleteAsync(string key, CancellationToken ct = default);

    Task<IReadOnlyDictionary<string, string>> GetAllAsync(CancellationToken ct = default);
}

public sealed class SqliteSettingsRepository(SqliteDatabase database) : SqliteRepository(database), ISettingsRepository
{
    public const int MaxKeyLength = 200;

    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        var k = RequireKey(key);
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        return await conn.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(
            "SELECT value FROM settings WHERE key = @Key;", new { Key = k }, cancellationToken: ct)).ConfigureAwait(false);
    }

    public async Task SetAsync(string key, string value, CancellationToken ct = default)
    {
        var k = RequireKey(key);
        ArgumentNullException.ThrowIfNull(value);
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO settings (key, value, updated_at) VALUES (@Key, @Value, @Now)
            ON CONFLICT (key) DO UPDATE SET value = excluded.value, updated_at = excluded.updated_at;
            """,
            new { Key = k, Value = value, Now = Now() }, cancellationToken: ct)).ConfigureAwait(false);
    }

    public async Task<bool> DeleteAsync(string key, CancellationToken ct = default)
    {
        var k = RequireKey(key);
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        return await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM settings WHERE key = @Key;", new { Key = k }, cancellationToken: ct)).ConfigureAwait(false) == 1;
    }

    public async Task<IReadOnlyDictionary<string, string>> GetAllAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        var rows = await conn.QueryAsync<(string Key, string Value)>(new CommandDefinition(
            "SELECT key, value FROM settings ORDER BY key;", cancellationToken: ct)).ConfigureAwait(false);
        return rows.ToDictionary(r => r.Key, r => r.Value, StringComparer.Ordinal);
    }

    private static string RequireKey(string key)
    {
        if (string.IsNullOrEmpty(key) || key.Length > MaxKeyLength || key.Any(char.IsControl))
        {
            throw DataRules.Invalid($"A settings key must be 1–{MaxKeyLength} characters without control characters.");
        }

        return key;
    }
}

/// <summary>One audit event. <see cref="Actor"/> names the user/device (and Windows principal where known).</summary>
public sealed record AuditEntry(
    string Actor,
    ClientKind? ClientKind,
    string Action,
    string? Target,
    string? Details,
    DateTimeOffset Timestamp);

public sealed record AuditRecord(
    long Id,
    string Actor,
    ClientKind? ClientKind,
    string Action,
    string? Target,
    string? Details,
    DateTimeOffset Timestamp);

public interface IAuditLog
{
    Task AppendAsync(AuditEntry entry, CancellationToken ct = default);

    /// <summary>Newest first; pass <paramref name="beforeId"/> (an earlier result's smallest id) to page further back.</summary>
    Task<IReadOnlyList<AuditRecord>> QueryRecentAsync(int limit = 100, long? beforeId = null, CancellationToken ct = default);

    Task<int> PurgeOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct = default);
}

public sealed class SqliteAuditLog(SqliteDatabase database) : SqliteRepository(database), IAuditLog
{
    public const int MaxActorLength = 200;
    public const int MaxActionLength = 100;
    public const int MaxTargetLength = 400;
    public const int MaxDetailsLength = 4000;
    public const int MaxQueryLimit = 1000;

    public async Task AppendAsync(AuditEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (string.IsNullOrWhiteSpace(entry.Actor) || string.IsNullOrWhiteSpace(entry.Action))
        {
            throw DataRules.Invalid("An audit entry needs an actor and an action.");
        }

        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO audit_log (occurred_at, actor, client_kind, action, target, details)
            VALUES (@Timestamp, @Actor, @ClientKind, @Action, @Target, @Details);
            """,
            new
            {
                entry.Timestamp,
                Actor = Truncate(entry.Actor, MaxActorLength),
                ClientKind = entry.ClientKind is { } kind ? (int?)kind : null,
                Action = Truncate(entry.Action, MaxActionLength),
                Target = entry.Target is null ? null : Truncate(entry.Target, MaxTargetLength),
                Details = entry.Details is null ? null : Truncate(entry.Details, MaxDetailsLength),
            },
            cancellationToken: ct)).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AuditRecord>> QueryRecentAsync(int limit = 100, long? beforeId = null, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        var rows = await conn.QueryAsync<AuditRow>(new CommandDefinition(
            """
            SELECT id AS Id, actor AS Actor, client_kind AS ClientKind, action AS Action, target AS Target,
                   details AS Details, occurred_at AS Timestamp
            FROM audit_log
            WHERE @BeforeId IS NULL OR id < @BeforeId
            ORDER BY id DESC
            LIMIT @Limit;
            """,
            new { BeforeId = beforeId, Limit = Math.Min(limit, MaxQueryLimit) }, cancellationToken: ct)).ConfigureAwait(false);
        return rows.Select(r => new AuditRecord(r.Id, r.Actor, r.ClientKind, r.Action, r.Target, r.Details, r.Timestamp)).ToArray();
    }

    public async Task<int> PurgeOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        return await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM audit_log WHERE occurred_at < @Cutoff;", new { Cutoff = cutoff }, cancellationToken: ct)).ConfigureAwait(false);
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    private sealed class AuditRow
    {
        public long Id { get; set; }

        public string Actor { get; set; } = "";

        public ClientKind? ClientKind { get; set; }

        public string Action { get; set; } = "";

        public string? Target { get; set; }

        public string? Details { get; set; }

        public DateTimeOffset Timestamp { get; set; }
    }
}
