using Dapper;
using Microsoft.Data.Sqlite;
using SabhaMujic.Contracts;
using SabhaMujic.Data.Internal;

namespace SabhaMujic.Data.Repositories;

public interface ICategoryRepository
{
    /// <summary>Creates a category at the end of the sort order. Conflict when the name exists (case-insensitive).</summary>
    Task<CategoryDto> CreateAsync(string name, CancellationToken ct = default);

    Task<CategoryDto?> GetAsync(Guid id, CancellationToken ct = default);

    /// <summary>All categories in sort order, with the number of tracks tagged with each.</summary>
    Task<IReadOnlyList<CategoryDto>> ListAsync(CancellationToken ct = default);

    /// <summary>Null when not found; Conflict when the new name is taken.</summary>
    Task<CategoryDto?> RenameAsync(Guid id, string name, CancellationToken ct = default);

    /// <summary>
    /// Puts the given categories first, in the given order; categories not listed keep their relative order after them.
    /// NotFound for unknown ids, InvalidArgument for duplicates.
    /// </summary>
    Task ReorderAsync(IReadOnlyList<Guid> orderedIds, CancellationToken ct = default);

    /// <summary>
    /// Makes <paramref name="trackIds"/> exactly the tracks tagged with the category, in one transaction; other tags of
    /// those tracks are untouched. Null when the category is unknown; InvalidArgument when a track is unknown (nothing
    /// changes then). Returns the category and the tracks whose tags changed (added or removed).
    /// </summary>
    Task<(CategoryDto Category, IReadOnlyList<Guid> ChangedTrackIds)?> SetTracksAsync(Guid id, IReadOnlyList<Guid> trackIds, CancellationToken ct = default);

    /// <summary>Deletes the category; it is removed from every track that had it (track_categories ON DELETE CASCADE).</summary>
    Task<bool> DeleteAsync(Guid id, CancellationToken ct = default);
}

public sealed class SqliteCategoryRepository(SqliteDatabase database) : SqliteRepository(database), ICategoryRepository
{
    public const int MaxNameLength = 100;

    private const string SelectWithCounts =
        """
        SELECT c.id AS Id, c.name AS Name, c.sort_order AS SortOrder,
               (SELECT COUNT(*) FROM track_categories tc WHERE tc.category_id = c.id) AS TrackCount
        FROM categories c
        """;

    public async Task<CategoryDto> CreateAsync(string name, CancellationToken ct = default)
    {
        var categoryName = DataRules.RequireText(name, "Category name", MaxNameLength);
        var id = Guid.NewGuid();
        var now = Now();
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        try
        {
            await conn.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO categories (id, name, name_key, sort_order, created_at, updated_at)
                VALUES (@Id, @Name, @Key, (SELECT COALESCE(MAX(sort_order), -1) + 1 FROM categories), @Now, @Now);
                """,
                new { Id = id, Name = categoryName, Key = DataRules.FoldKey(categoryName), Now = now }, cancellationToken: ct)).ConfigureAwait(false);
        }
        catch (SqliteException ex) when (DataRules.IsUniqueViolation(ex))
        {
            throw DataRules.Conflict($"A category named '{categoryName}' already exists.");
        }

        return (await GetAsync(conn, id, ct).ConfigureAwait(false))!;
    }

    public async Task<CategoryDto?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        return await GetAsync(conn, id, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<CategoryDto>> ListAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        var rows = await conn.QueryAsync<CategoryRow>(new CommandDefinition(
            SelectWithCounts + " ORDER BY c.sort_order, c.name_key;", cancellationToken: ct)).ConfigureAwait(false);
        return rows.Select(r => r.ToDto()).ToArray();
    }

    public async Task<CategoryDto?> RenameAsync(Guid id, string name, CancellationToken ct = default)
    {
        var categoryName = DataRules.RequireText(name, "Category name", MaxNameLength);
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        int changed;
        try
        {
            changed = await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE categories SET name = @Name, name_key = @Key, updated_at = @Now WHERE id = @Id;",
                new { Id = id, Name = categoryName, Key = DataRules.FoldKey(categoryName), Now = Now() }, cancellationToken: ct)).ConfigureAwait(false);
        }
        catch (SqliteException ex) when (DataRules.IsUniqueViolation(ex))
        {
            throw DataRules.Conflict($"A category named '{categoryName}' already exists.");
        }

        return changed == 0 ? null : await GetAsync(conn, id, ct).ConfigureAwait(false);
    }

    public async Task ReorderAsync(IReadOnlyList<Guid> orderedIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(orderedIds);
        if (orderedIds.Distinct().Count() != orderedIds.Count)
        {
            throw DataRules.Invalid("The category order contains duplicates.");
        }

        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        using var tx = conn.BeginTransaction(deferred: false);
        var existing = (await conn.QueryAsync<Guid>(new CommandDefinition(
            "SELECT id FROM categories ORDER BY sort_order, name_key;", transaction: tx, cancellationToken: ct)).ConfigureAwait(false)).ToList();
        var known = existing.ToHashSet();
        foreach (var id in orderedIds)
        {
            if (!known.Contains(id))
            {
                throw DataRules.NotFound($"Category {id} does not exist.");
            }
        }

        var listed = orderedIds.ToHashSet();
        var finalOrder = orderedIds.Concat(existing.Where(id => !listed.Contains(id)));
        var now = Now();
        var position = 0;
        foreach (var id in finalOrder)
        {
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE categories SET sort_order = @Position, updated_at = @Now WHERE id = @Id;",
                new { Id = id, Position = position++, Now = now }, tx, cancellationToken: ct)).ConfigureAwait(false);
        }

        tx.Commit();
    }

    public const int MaxTracksPerSet = 10_000;

    public async Task<(CategoryDto Category, IReadOnlyList<Guid> ChangedTrackIds)?> SetTracksAsync(Guid id, IReadOnlyList<Guid> trackIds,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(trackIds);
        var wanted = trackIds.Distinct().ToArray();
        if (wanted.Length > MaxTracksPerSet)
        {
            throw DataRules.Invalid($"At most {MaxTracksPerSet} tracks can be set at once.");
        }

        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        using var tx = conn.BeginTransaction(deferred: false);
        if (await conn.ExecuteScalarAsync<long>(new CommandDefinition(
                "SELECT COUNT(*) FROM categories WHERE id = @Id;", new { Id = id }, tx, cancellationToken: ct)).ConfigureAwait(false) == 0)
        {
            return null;
        }

        // The ids travel as one JSON array parameter (json_each), so the statements stay fully parameterized.
        var idsJson = System.Text.Json.JsonSerializer.Serialize(wanted.Select(DapperTypeHandlers.ToDb));
        var known = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(*) FROM tracks WHERE id IN (SELECT value FROM json_each(@Ids));", new { Ids = idsJson }, tx, cancellationToken: ct))
            .ConfigureAwait(false);
        if (known != wanted.Length)
        {
            throw DataRules.Invalid("One or more of the tracks do not exist.");
        }

        var before = (await conn.QueryAsync<Guid>(new CommandDefinition(
            "SELECT track_id FROM track_categories WHERE category_id = @Id;", new { Id = id }, tx, cancellationToken: ct)).ConfigureAwait(false))
            .ToHashSet();
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM track_categories WHERE category_id = @Id AND track_id NOT IN (SELECT value FROM json_each(@Ids));",
            new { Id = id, Ids = idsJson }, tx, cancellationToken: ct)).ConfigureAwait(false);
        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO track_categories (track_id, category_id)
            SELECT value, @Id FROM json_each(@Ids) WHERE true
            ON CONFLICT (track_id, category_id) DO NOTHING;
            """,
            new { Id = id, Ids = idsJson }, tx, cancellationToken: ct)).ConfigureAwait(false);
        tx.Commit();

        var after = wanted.ToHashSet();
        var changed = before.Where(t => !after.Contains(t)).Concat(after.Where(t => !before.Contains(t))).ToArray();
        return ((await GetAsync(conn, id, ct).ConfigureAwait(false))!, changed);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        return await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM categories WHERE id = @Id;", new { Id = id }, cancellationToken: ct)).ConfigureAwait(false) == 1;
    }

    private static async Task<CategoryDto?> GetAsync(SqliteConnection conn, Guid id, CancellationToken ct)
    {
        var row = await conn.QuerySingleOrDefaultAsync<CategoryRow>(new CommandDefinition(
            SelectWithCounts + " WHERE c.id = @Id;", new { Id = id }, cancellationToken: ct)).ConfigureAwait(false);
        return row?.ToDto();
    }

    private sealed class CategoryRow
    {
        public Guid Id { get; set; }

        public string Name { get; set; } = "";

        public int SortOrder { get; set; }

        public int TrackCount { get; set; }

        public CategoryDto ToDto() => new(Id, Name, SortOrder, TrackCount);
    }
}
