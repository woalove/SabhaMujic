using Dapper;
using SabhaMujic.Contracts;
using SabhaMujic.Data.Internal;

namespace SabhaMujic.Data.Repositories;

/// <summary>
/// Input for a new session. <see cref="TokenHash"/> is <c>Tokens.HashToken(token)</c>; the token itself is never stored.
/// At least one of <see cref="UserId"/> (desktop/user login) or <see cref="DeviceId"/> (paired remote) is required.
/// </summary>
public sealed record NewSession(
    string TokenHash,
    Guid? UserId,
    Guid? DeviceId,
    Role Role,
    ClientKind ClientKind,
    DateTimeOffset ExpiresAt);

public sealed record SessionRecord(
    Guid Id,
    string TokenHash,
    Guid? UserId,
    Guid? DeviceId,
    Role Role,
    ClientKind ClientKind,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset LastSeenAt,
    DateTimeOffset? RevokedAt);

public interface ISessionRepository
{
    /// <summary>Throws NotFound when the user/device does not exist, Conflict on a duplicate token hash.</summary>
    Task<SessionRecord> CreateAsync(NewSession session, CancellationToken ct = default);

    /// <summary>
    /// Returns the session only when it is neither expired nor revoked, and (for device sessions) the device is not
    /// revoked. Returns null otherwise.
    /// </summary>
    Task<SessionRecord?> GetActiveByTokenHashAsync(string tokenHash, CancellationToken ct = default);

    /// <summary>Updates last-seen; optionally extends the expiry (sliding sessions). Only affects active sessions.</summary>
    Task<bool> TouchAsync(Guid sessionId, DateTimeOffset? extendExpiryTo = null, CancellationToken ct = default);

    Task<bool> RevokeAsync(Guid sessionId, CancellationToken ct = default);

    Task<int> RevokeAllForUserAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Revokes every session of the user except <paramref name="keepSessionId"/> (e.g. the one changing its own password).</summary>
    Task<int> RevokeAllForUserExceptAsync(Guid userId, Guid keepSessionId, CancellationToken ct = default);

    Task<int> RevokeAllForDeviceAsync(Guid deviceId, CancellationToken ct = default);

    /// <summary>Deletes sessions that are expired or revoked. Returns the number removed.</summary>
    Task<int> PurgeExpiredAsync(CancellationToken ct = default);
}

public sealed class SqliteSessionRepository(SqliteDatabase database) : SqliteRepository(database), ISessionRepository
{
    private const string SelectColumns =
        """
        SELECT s.id AS Id, s.token_hash AS TokenHash, s.user_id AS UserId, s.device_id AS DeviceId, s.role AS Role,
               s.client_kind AS ClientKind, s.created_at AS CreatedAt, s.expires_at AS ExpiresAt,
               s.last_seen_at AS LastSeenAt, s.revoked_at AS RevokedAt
        FROM sessions s
        """;

    public async Task<SessionRecord> CreateAsync(NewSession session, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        var hash = DataRules.RequireTokenHash(session.TokenHash);
        if (session.UserId is null && session.DeviceId is null)
        {
            throw DataRules.Invalid("A session needs a user or a device.");
        }

        SqliteUserRepository.RequireRole(session.Role);
        if (!Enum.IsDefined(session.ClientKind))
        {
            throw DataRules.Invalid("Unknown client kind.");
        }

        var now = Now();
        if (session.ExpiresAt <= now)
        {
            throw DataRules.Invalid("The session expiry must be in the future.");
        }

        var record = new SessionRecord(Guid.NewGuid(), hash, session.UserId, session.DeviceId, session.Role, session.ClientKind,
            now, DapperTypeHandlers.FromDbTime(DapperTypeHandlers.ToDb(session.ExpiresAt)), now, null);

        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        try
        {
            await conn.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO sessions (id, token_hash, user_id, device_id, role, client_kind, created_at, expires_at, last_seen_at, revoked_at)
                VALUES (@Id, @TokenHash, @UserId, @DeviceId, @Role, @ClientKind, @CreatedAt, @ExpiresAt, @LastSeenAt, NULL);
                """,
                record, cancellationToken: ct)).ConfigureAwait(false);
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex) when (DataRules.IsForeignKeyViolation(ex))
        {
            throw DataRules.NotFound("The session's user or device does not exist.");
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex) when (DataRules.IsUniqueViolation(ex))
        {
            throw DataRules.Conflict("A session with this token already exists.");
        }

        return record;
    }

    public async Task<SessionRecord?> GetActiveByTokenHashAsync(string tokenHash, CancellationToken ct = default)
    {
        string hash;
        try
        {
            hash = DataRules.RequireTokenHash(tokenHash);
        }
        catch (SabhaMujicException)
        {
            return null;
        }

        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        var row = await conn.QuerySingleOrDefaultAsync<SessionRow>(new CommandDefinition(
            SelectColumns +
            """

            LEFT JOIN devices d ON d.id = s.device_id
            WHERE s.token_hash = @Hash AND s.revoked_at IS NULL AND s.expires_at > @Now
              AND (s.device_id IS NULL OR d.revoked_at IS NULL);
            """,
            new { Hash = hash, Now = Now() }, cancellationToken: ct)).ConfigureAwait(false);
        return row?.ToRecord();
    }

    public async Task<bool> TouchAsync(Guid sessionId, DateTimeOffset? extendExpiryTo = null, CancellationToken ct = default)
    {
        var now = Now();
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        var changed = await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE sessions
            SET last_seen_at = @Now,
                expires_at = CASE WHEN @Extend IS NOT NULL AND @Extend > expires_at THEN @Extend ELSE expires_at END
            WHERE id = @Id AND revoked_at IS NULL AND expires_at > @Now;
            """,
            new { Id = sessionId, Now = now, Extend = extendExpiryTo }, cancellationToken: ct)).ConfigureAwait(false);
        return changed == 1;
    }

    public async Task<bool> RevokeAsync(Guid sessionId, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        var changed = await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE sessions SET revoked_at = @Now WHERE id = @Id AND revoked_at IS NULL;",
            new { Id = sessionId, Now = Now() }, cancellationToken: ct)).ConfigureAwait(false);
        return changed == 1;
    }

    public async Task<int> RevokeAllForUserExceptAsync(Guid userId, Guid keepSessionId, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        return await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE sessions SET revoked_at = @Now WHERE user_id = @Id AND id <> @Keep AND revoked_at IS NULL;",
            new { Id = userId, Keep = keepSessionId, Now = Now() }, cancellationToken: ct)).ConfigureAwait(false);
    }

    public async Task<int> RevokeAllForUserAsync(Guid userId, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        return await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE sessions SET revoked_at = @Now WHERE user_id = @Id AND revoked_at IS NULL;",
            new { Id = userId, Now = Now() }, cancellationToken: ct)).ConfigureAwait(false);
    }

    public async Task<int> RevokeAllForDeviceAsync(Guid deviceId, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        return await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE sessions SET revoked_at = @Now WHERE device_id = @Id AND revoked_at IS NULL;",
            new { Id = deviceId, Now = Now() }, cancellationToken: ct)).ConfigureAwait(false);
    }

    public async Task<int> PurgeExpiredAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        return await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM sessions WHERE expires_at <= @Now OR revoked_at IS NOT NULL;",
            new { Now = Now() }, cancellationToken: ct)).ConfigureAwait(false);
    }

    private sealed class SessionRow
    {
        public Guid Id { get; set; }

        public string TokenHash { get; set; } = "";

        public Guid? UserId { get; set; }

        public Guid? DeviceId { get; set; }

        public Role Role { get; set; }

        public ClientKind ClientKind { get; set; }

        public DateTimeOffset CreatedAt { get; set; }

        public DateTimeOffset ExpiresAt { get; set; }

        public DateTimeOffset LastSeenAt { get; set; }

        public DateTimeOffset? RevokedAt { get; set; }

        public SessionRecord ToRecord() =>
            new(Id, TokenHash, UserId, DeviceId, Role, ClientKind, CreatedAt, ExpiresAt, LastSeenAt, RevokedAt);
    }
}
