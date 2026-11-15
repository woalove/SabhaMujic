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

    /// <summary>All categories in sort order, with their track counts.</summary>
    Task<IReadOnlyList<CategoryDto>> ListAsync(CancellationToken ct = default);

    /// <summary>Null when not found; Conflict when the new name is taken.</summary>
    Task<CategoryDto?> RenameAsync(Guid id, string name, CancellationToken ct = default);

    /// <summary>
    /// Puts the given categories first, in the given order; categories not listed keep their relative order after them.
    /// NotFound for unknown ids, InvalidArgument for duplicates.
    /// </summary>
    Task ReorderAsync(IReadOnlyList<Guid> orderedIds, CancellationToken ct = default);

    /// <summary>Deletes the category; its tracks become uncategorized (FK ON DELETE SET NULL).</summary>
    Task<bool> DeleteAsync(Guid id, CancellationToken ct = default);
}

public sealed class SqliteCategoryRepository(SqliteDatabase database) : SqliteRepository(database), ICategoryRepository
{
    public const int MaxNameLength = 100;

    private const string SelectWithCounts =
        """
        SELECT c.id AS Id, c.name AS Name, c.sort_order AS SortOrder,
               (SELECT COUNT(*) FROM tracks t WHERE t.category_id = c.id) AS TrackCount
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
