using Dapper;
using Microsoft.Data.Sqlite;
using SabhaMujic.Contracts;
using SabhaMujic.Data.Internal;

namespace SabhaMujic.Data.Repositories;

/// <summary>A stored account. <see cref="SecretHash"/> is a <c>SecretHasher</c> string, never a plain secret.</summary>
public sealed record UserRecord(Guid Id, string UserName, Role Role, string SecretHash, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    public UserDto ToDto() => new(Id, UserName, Role, CreatedAt);
}

public interface IUserRepository
{
    /// <summary>Creates an account. Throws Conflict when the name exists (case-insensitive).</summary>
    Task<UserRecord> CreateAsync(string userName, Role role, string secretHash, CancellationToken ct = default);

    /// <summary>Case-insensitive lookup.</summary>
    Task<UserRecord?> GetByNameAsync(string userName, CancellationToken ct = default);

    Task<UserRecord?> GetByIdAsync(Guid id, CancellationToken ct = default);

    Task<IReadOnlyList<UserDto>> ListAsync(CancellationToken ct = default);

    Task<bool> UpdateSecretHashAsync(Guid id, string secretHash, CancellationToken ct = default);

    /// <summary>
    /// Deletes the account and (by cascade) its sessions. When <paramref name="preserveLastAdmin"/> is true the check
    /// and the delete run in one write transaction and Conflict is thrown for the last admin. Returns false if not found.
    /// </summary>
    Task<bool> DeleteAsync(Guid id, bool preserveLastAdmin = true, CancellationToken ct = default);

    Task<int> CountAdminsAsync(CancellationToken ct = default);
}

public sealed class SqliteUserRepository(SqliteDatabase database) : SqliteRepository(database), IUserRepository
{
    public const int MaxUserNameLength = 64;

    private const string SelectColumns =
        "SELECT id AS Id, user_name AS UserName, role AS Role, secret_hash AS SecretHash, created_at AS CreatedAt, updated_at AS UpdatedAt FROM users";

    public async Task<UserRecord> CreateAsync(string userName, Role role, string secretHash, CancellationToken ct = default)
    {
        var name = DataRules.RequireText(userName, "User name", MaxUserNameLength);
        RequireRole(role);
        RequireSecretHash(secretHash);
        var now = Now();
        var user = new UserRecord(Guid.NewGuid(), name, role, secretHash, now, now);

        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        try
        {
            await conn.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO users (id, user_name, user_name_key, role, secret_hash, created_at, updated_at)
                VALUES (@Id, @UserName, @Key, @Role, @SecretHash, @CreatedAt, @UpdatedAt);
                """,
                new { user.Id, user.UserName, Key = DataRules.FoldKey(name), user.Role, user.SecretHash, user.CreatedAt, user.UpdatedAt },
                cancellationToken: ct)).ConfigureAwait(false);
        }
        catch (SqliteException ex) when (DataRules.IsUniqueViolation(ex))
        {
            throw DataRules.Conflict($"A user named '{name}' already exists.");
        }

        return user;
    }

    public async Task<UserRecord?> GetByNameAsync(string userName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userName))
        {
            return null;
        }

        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        var row = await conn.QuerySingleOrDefaultAsync<UserRow>(new CommandDefinition(
            SelectColumns + " WHERE user_name_key = @Key;",
            new { Key = DataRules.FoldKey(userName.Trim()) }, cancellationToken: ct)).ConfigureAwait(false);
        return row?.ToRecord();
    }

    public async Task<UserRecord?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        var row = await conn.QuerySingleOrDefaultAsync<UserRow>(new CommandDefinition(
            SelectColumns + " WHERE id = @Id;", new { Id = id }, cancellationToken: ct)).ConfigureAwait(false);
        return row?.ToRecord();
    }

    public async Task<IReadOnlyList<UserDto>> ListAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        var rows = await conn.QueryAsync<UserRow>(new CommandDefinition(
            SelectColumns + " ORDER BY user_name_key;", cancellationToken: ct)).ConfigureAwait(false);
        return rows.Select(r => r.ToRecord().ToDto()).ToArray();
    }

    public async Task<bool> UpdateSecretHashAsync(Guid id, string secretHash, CancellationToken ct = default)
    {
        RequireSecretHash(secretHash);
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        var changed = await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE users SET secret_hash = @SecretHash, updated_at = @Now WHERE id = @Id;",
            new { Id = id, SecretHash = secretHash, Now = Now() }, cancellationToken: ct)).ConfigureAwait(false);
        return changed == 1;
    }

    public async Task<bool> DeleteAsync(Guid id, bool preserveLastAdmin = true, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        using var tx = conn.BeginTransaction(deferred: false);
        var role = await conn.QuerySingleOrDefaultAsync<int?>(new CommandDefinition(
            "SELECT role FROM users WHERE id = @Id;", new { Id = id }, tx, cancellationToken: ct)).ConfigureAwait(false);
        if (role is null)
        {
            return false;
        }

        if (preserveLastAdmin && role == (int)Role.Admin)
        {
            var admins = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
                "SELECT COUNT(*) FROM users WHERE role = @Admin;", new { Admin = Role.Admin }, tx, cancellationToken: ct)).ConfigureAwait(false);
            if (admins <= 1)
            {
                throw DataRules.Conflict("The last administrator cannot be deleted.");
            }
        }

        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM users WHERE id = @Id;", new { Id = id }, tx, cancellationToken: ct)).ConfigureAwait(false);
        tx.Commit();
        return true;
    }

    public async Task<int> CountAdminsAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        return (int)await conn.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(*) FROM users WHERE role = @Admin;", new { Admin = Role.Admin }, cancellationToken: ct)).ConfigureAwait(false);
    }

    internal static void RequireRole(Role role)
    {
        if (!Enum.IsDefined(role))
        {
            throw DataRules.Invalid("Unknown role.");
        }
    }

    /// <summary>Guards against accidentally persisting a plain secret: only SecretHasher output is accepted.</summary>
    private static void RequireSecretHash(string secretHash)
    {
        if (string.IsNullOrEmpty(secretHash) || !secretHash.StartsWith("pbkdf2-sha256$", StringComparison.Ordinal) ||
            secretHash.Split('$').Length != 4)
        {
            throw DataRules.Invalid("The secret must be a SecretHasher hash, not a plain secret.");
        }
    }

    private sealed class UserRow
    {
        public Guid Id { get; set; }

        public string UserName { get; set; } = "";

        public Role Role { get; set; }

        public string SecretHash { get; set; } = "";

        public DateTimeOffset CreatedAt { get; set; }

        public DateTimeOffset UpdatedAt { get; set; }

        public UserRecord ToRecord() => new(Id, UserName, Role, SecretHash, CreatedAt, UpdatedAt);
    }
}
