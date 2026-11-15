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

/// <summary>A freshly created pairing code. <see cref="Code"/> is only available here; the database stores its hash.</summary>
public sealed record PairingCode(Guid Id, string Code, Role Role, DateTimeOffset ExpiresAt);

public sealed record ConsumedPairingCode(Guid Id, Role Role);

public interface IPairingCodeRepository
{
    /// <summary>Generates a fresh 6-digit single-use code (distinct from every other active code).</summary>
    Task<PairingCode> CreateAsync(Role role, DateTimeOffset expiresAt, Guid? createdByUserId = null, CancellationToken ct = default);

    /// <summary>
    /// Atomically consumes an active (unconsumed, unexpired) code. Returns null when the code is unknown, expired or
    /// already used. Race-safe: of any number of concurrent consumers exactly one succeeds.
    /// </summary>
    Task<ConsumedPairingCode?> TryConsumeAsync(string code, CancellationToken ct = default);

    /// <summary>Deletes consumed and expired codes.</summary>
    Task<int> PurgeAsync(CancellationToken ct = default);
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

public sealed class SqlitePairingCodeRepository(SqliteDatabase database) : SqliteRepository(database), IPairingCodeRepository
{
    private const int MaxGenerationAttempts = 20;

    public async Task<PairingCode> CreateAsync(Role role, DateTimeOffset expiresAt, Guid? createdByUserId = null, CancellationToken ct = default)
    {
        SqliteUserRepository.RequireRole(role);
        var now = Now();
        if (expiresAt <= now)
        {
            throw DataRules.Invalid("The pairing code expiry must be in the future.");
        }

        expiresAt = DapperTypeHandlers.FromDbTime(DapperTypeHandlers.ToDb(expiresAt));
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        using var tx = conn.BeginTransaction(deferred: false);
        for (var attempt = 0; attempt < MaxGenerationAttempts; attempt++)
        {
            var code = Tokens.NewPairingCode();
            var hash = Tokens.HashToken(code);
            var clash = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
                "SELECT COUNT(*) FROM pairing_codes WHERE code_hash = @Hash AND consumed_at IS NULL AND expires_at > @Now;",
                new { Hash = hash, Now = now }, tx, cancellationToken: ct)).ConfigureAwait(false);
            if (clash > 0)
            {
                continue;
            }

            var id = Guid.NewGuid();
            try
            {
                await conn.ExecuteAsync(new CommandDefinition(
                    """
                    INSERT INTO pairing_codes (id, code_hash, role, created_by, created_at, expires_at, consumed_at)
                    VALUES (@Id, @Hash, @Role, @CreatedBy, @Now, @ExpiresAt, NULL);
                    """,
                    new { Id = id, Hash = hash, Role = role, CreatedBy = createdByUserId, Now = now, ExpiresAt = expiresAt },
                    tx, cancellationToken: ct)).ConfigureAwait(false);
            }
            catch (Microsoft.Data.Sqlite.SqliteException ex) when (DataRules.IsForeignKeyViolation(ex))
            {
                throw DataRules.NotFound("The creating user does not exist.");
            }

            tx.Commit();
            return new PairingCode(id, code, role, expiresAt);
        }

        throw DataRules.Conflict("Too many active pairing codes; try again later.");
    }

    public async Task<ConsumedPairingCode?> TryConsumeAsync(string code, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(code) || code.Length != 6 || !code.All(char.IsAsciiDigit))
        {
            return null;
        }

        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        // A single UPDATE … RETURNING is atomic: concurrent consumers serialize on SQLite's write lock and only the
        // first one still sees consumed_at IS NULL.
        var row = await conn.QuerySingleOrDefaultAsync<ConsumedRow>(new CommandDefinition(
            """
            UPDATE pairing_codes SET consumed_at = @Now
            WHERE id = (SELECT id FROM pairing_codes
                        WHERE code_hash = @Hash AND consumed_at IS NULL AND expires_at > @Now
                        LIMIT 1)
              AND consumed_at IS NULL
            RETURNING id AS Id, role AS Role;
            """,
            new { Hash = Tokens.HashToken(code), Now = Now() }, cancellationToken: ct)).ConfigureAwait(false);
        return row is null ? null : new ConsumedPairingCode(row.Id, row.Role);
    }

    public async Task<int> PurgeAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        return await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM pairing_codes WHERE consumed_at IS NOT NULL OR expires_at <= @Now;",
            new { Now = Now() }, cancellationToken: ct)).ConfigureAwait(false);
    }

    private sealed class ConsumedRow
    {
        public Guid Id { get; set; }

        public Role Role { get; set; }
    }
}
