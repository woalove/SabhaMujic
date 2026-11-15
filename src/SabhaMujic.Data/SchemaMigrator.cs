using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Data.Sqlite;

namespace SabhaMujic.Data;

/// <summary>A versioned schema script embedded as <c>Migrations/NNNN_name.sql</c>.</summary>
internal sealed record Migration(int Version, string Name, string Sql);

/// <summary>Thrown when the database was written by a newer SabhaMujic than this build understands.</summary>
public sealed class SchemaVersionException : Exception
{
    public SchemaVersionException(int databaseVersion, int supportedVersion)
        : base($"The database schema version {databaseVersion} is newer than this version of SabhaMujic supports ({supportedVersion}). " +
               "Install a newer SabhaMujic or restore a matching backup.")
    {
        DatabaseVersion = databaseVersion;
        SupportedVersion = supportedVersion;
    }

    public int DatabaseVersion { get; }

    public int SupportedVersion { get; }
}

internal static partial class SchemaMigrator
{
    private const string ResourcePrefix = "SabhaMujic.Data.Migrations.";

    public static IReadOnlyList<Migration> Embedded { get; } = LoadEmbedded();

    public static int LatestVersion => Embedded[^1].Version;

    /// <summary>
    /// Applies every migration newer than <c>PRAGMA user_version</c>, each in its own IMMEDIATE transaction
    /// that also records it in <c>schema_migrations</c> and bumps <c>user_version</c>. Safe to re-run and safe
    /// against a concurrent migrator (the version is re-read under the write lock).
    /// </summary>
    public static async Task<int> MigrateAsync(SqliteConnection connection, IReadOnlyList<Migration> migrations, TimeProvider clock, CancellationToken ct)
    {
        ValidateSequence(migrations);
        var latest = migrations.Count == 0 ? 0 : migrations[^1].Version;

        var current = await GetVersionAsync(connection, null, ct).ConfigureAwait(false);
        ThrowIfNewer(current, latest);

        foreach (var migration in migrations)
        {
            if (migration.Version <= current)
            {
                continue;
            }

            ct.ThrowIfCancellationRequested();
            using var tx = connection.BeginTransaction(deferred: false);
            current = await GetVersionAsync(connection, tx, ct).ConfigureAwait(false);
            ThrowIfNewer(current, latest);
            if (current >= migration.Version)
            {
                tx.Rollback();
                continue;
            }

            if (current != migration.Version - 1)
            {
                throw new InvalidOperationException($"Database is at schema version {current} but the next migration is {migration.Version}.");
            }

            await connection.ExecuteAsync(new CommandDefinition(
                "CREATE TABLE IF NOT EXISTS schema_migrations (version INTEGER NOT NULL PRIMARY KEY, name TEXT NOT NULL, applied_at INTEGER NOT NULL);",
                transaction: tx, cancellationToken: ct)).ConfigureAwait(false);
            await connection.ExecuteAsync(new CommandDefinition(migration.Sql, transaction: tx, cancellationToken: ct)).ConfigureAwait(false);
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO schema_migrations (version, name, applied_at) VALUES (@Version, @Name, @AppliedAt);",
                new { migration.Version, migration.Name, AppliedAt = clock.GetUtcNow() }, tx, cancellationToken: ct)).ConfigureAwait(false);

            // PRAGMA values cannot be bound as parameters; the version is an int from this assembly, not user input.
            await connection.ExecuteAsync(new CommandDefinition(
                $"PRAGMA user_version = {migration.Version.ToString(CultureInfo.InvariantCulture)};",
                transaction: tx, cancellationToken: ct)).ConfigureAwait(false);
            tx.Commit();
            current = migration.Version;
        }

        return current;
    }

    public static async Task<int> GetVersionAsync(SqliteConnection connection, SqliteTransaction? tx, CancellationToken ct) =>
        (int)await connection.ExecuteScalarAsync<long>(new CommandDefinition("PRAGMA user_version;", transaction: tx, cancellationToken: ct))
            .ConfigureAwait(false);

    private static void ThrowIfNewer(int current, int latest)
    {
        if (current > latest)
        {
            throw new SchemaVersionException(current, latest);
        }
    }

    private static void ValidateSequence(IReadOnlyList<Migration> migrations)
    {
        for (var i = 0; i < migrations.Count; i++)
        {
            if (migrations[i].Version != i + 1)
            {
                throw new InvalidOperationException($"Migrations must be numbered 1..N without gaps; found {migrations[i].Version} at position {i + 1}.");
            }
        }
    }

    private static List<Migration> LoadEmbedded()
    {
        var assembly = typeof(SchemaMigrator).Assembly;
        var migrations = new List<Migration>();
        foreach (var resource in assembly.GetManifestResourceNames())
        {
            if (!resource.StartsWith(ResourcePrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var match = MigrationName().Match(resource[ResourcePrefix.Length..]);
            if (!match.Success)
            {
                throw new InvalidOperationException($"Embedded migration '{resource}' does not match NNNN_name.sql.");
            }

            migrations.Add(new Migration(
                int.Parse(match.Groups["version"].Value, CultureInfo.InvariantCulture),
                match.Groups["name"].Value,
                ReadResource(assembly, resource)));
        }

        migrations.Sort((a, b) => a.Version.CompareTo(b.Version));
        if (migrations.Count == 0)
        {
            throw new InvalidOperationException("No embedded migrations were found.");
        }

        ValidateSequence(migrations);
        return migrations;
    }

    private static string ReadResource(Assembly assembly, string name)
    {
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Missing embedded resource '{name}'.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [GeneratedRegex(@"^(?<version>\d{4})_(?<name>[a-z0-9_]+)\.sql$", RegexOptions.CultureInvariant)]
    private static partial Regex MigrationName();
}
