using Dapper;
using SabhaMujic.Contracts;
using SabhaMujic.Core.Security;
using SabhaMujic.Data.Internal;

namespace SabhaMujic.Data.Repositories;

public sealed record DeviceRecord(Guid Id, string Name, Role Role, DateTimeOffset PairedAt, DateTimeOffset? LastSeenAt, DateTimeOffset? RevokedAt)
{
    public bool IsRevoked => RevokedAt is not null;

    public DeviceDto ToDto() => new(Id, Name, Role, PairedAt, LastSeenAt);
}

public interface IDeviceRepository
{
    Task<DeviceRecord> CreateAsync(string name, Role role, CancellationToken ct = default);

    /// <summary>Paired devices, newest first. Revoked devices are omitted unless requested.</summary>
    Task<IReadOnlyList<DeviceDto>> ListAsync(bool includeRevoked = false, CancellationToken ct = default);

    Task<DeviceRecord?> GetAsync(Guid id, CancellationToken ct = default);

    Task<bool> TouchAsync(Guid id, CancellationToken ct = default);

    /// <summary>Marks the device revoked and revokes all its sessions in one transaction. False if unknown or already revoked.</summary>
    Task<bool> RevokeAsync(Guid id, CancellationToken ct = default);
}

public sealed class SqliteDeviceRepository(SqliteDatabase database) : SqliteRepository(database), IDeviceRepository
{
    public const int MaxNameLength = 100;

    private const string SelectColumns =
        "SELECT id AS Id, name AS Name, role AS Role, paired_at AS PairedAt, last_seen_at AS LastSeenAt, revoked_at AS RevokedAt FROM devices";

    public async Task<DeviceRecord> CreateAsync(string name, Role role, CancellationToken ct = default)
    {
        var deviceName = DataRules.RequireText(name, "Device name", MaxNameLength);
        SqliteUserRepository.RequireRole(role);
        var record = new DeviceRecord(Guid.NewGuid(), deviceName, role, Now(), null, null);
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await conn.ExecuteAsync(new CommandDefinition(
            "INSERT INTO devices (id, name, role, paired_at, last_seen_at, revoked_at) VALUES (@Id, @Name, @Role, @PairedAt, NULL, NULL);",
            record, cancellationToken: ct)).ConfigureAwait(false);
        return record;
    }

    public async Task<IReadOnlyList<DeviceDto>> ListAsync(bool includeRevoked = false, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        var rows = await conn.QueryAsync<DeviceRow>(new CommandDefinition(
            SelectColumns + " WHERE @IncludeRevoked = 1 OR revoked_at IS NULL ORDER BY paired_at DESC, id;",
            new { IncludeRevoked = includeRevoked ? 1 : 0 }, cancellationToken: ct)).ConfigureAwait(false);
        return rows.Select(r => r.ToRecord().ToDto()).ToArray();
    }

    public async Task<DeviceRecord?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        var row = await conn.QuerySingleOrDefaultAsync<DeviceRow>(new CommandDefinition(
            SelectColumns + " WHERE id = @Id;", new { Id = id }, cancellationToken: ct)).ConfigureAwait(false);
        return row?.ToRecord();
    }

    public async Task<bool> TouchAsync(Guid id, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        return await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE devices SET last_seen_at = @Now WHERE id = @Id AND revoked_at IS NULL;",
            new { Id = id, Now = Now() }, cancellationToken: ct)).ConfigureAwait(false) == 1;
    }

    public async Task<bool> RevokeAsync(Guid id, CancellationToken ct = default)
    {
        var now = Now();
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        using var tx = conn.BeginTransaction(deferred: false);
        var changed = await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE devices SET revoked_at = @Now WHERE id = @Id AND revoked_at IS NULL;",
            new { Id = id, Now = now }, tx, cancellationToken: ct)).ConfigureAwait(false);
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE sessions SET revoked_at = @Now WHERE device_id = @Id AND revoked_at IS NULL;",
            new { Id = id, Now = now }, tx, cancellationToken: ct)).ConfigureAwait(false);
        tx.Commit();
        return changed == 1;
    }

    private sealed class DeviceRow
    {
        public Guid Id { get; set; }

        public string Name { get; set; } = "";

        public Role Role { get; set; }

        public DateTimeOffset PairedAt { get; set; }

        public DateTimeOffset? LastSeenAt { get; set; }

        public DateTimeOffset? RevokedAt { get; set; }

        public DeviceRecord ToRecord() => new(Id, Name, Role, PairedAt, LastSeenAt, RevokedAt);
    }
}
