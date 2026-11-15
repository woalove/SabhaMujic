using Dapper;
using Microsoft.Data.Sqlite;
using SabhaMujic.Contracts;
using SabhaMujic.Data.Internal;

namespace SabhaMujic.Data.Repositories;

public interface IPlaylistRepository
{
    /// <summary>Creates a playlist with optional initial tracks, in one transaction. NotFound for unknown tracks.</summary>
    Task<PlaylistDto> CreateAsync(string name, IReadOnlyList<Guid>? trackIds = null, Guid? createdByUserId = null, CancellationToken ct = default);

    Task<PlaylistDto?> GetAsync(Guid id, CancellationToken ct = default);

    /// <summary>All playlists ordered by name.</summary>
    Task<IReadOnlyList<PlaylistDto>> ListAsync(CancellationToken ct = default);

    Task<PlaylistDto?> RenameAsync(Guid id, string name, CancellationToken ct = default);

    /// <summary>
    /// Replaces the ordered track list atomically (duplicates allowed). Null when the playlist is unknown;
    /// NotFound (and no change) when any track is unknown.
    /// </summary>
    Task<PlaylistDto?> SetTracksAsync(Guid id, IReadOnlyList<Guid> trackIds, CancellationToken ct = default);

    Task<bool> DeleteAsync(Guid id, CancellationToken ct = default);
}

public sealed class SqlitePlaylistRepository(SqliteDatabase database) : SqliteRepository(database), IPlaylistRepository
{
    public const int MaxNameLength = 100;
    public const int MaxItems = 5_000;

    public async Task<PlaylistDto> CreateAsync(string name, IReadOnlyList<Guid>? trackIds = null, Guid? createdByUserId = null, CancellationToken ct = default)
    {
        var playlistName = DataRules.RequireText(name, "Playlist name", MaxNameLength);
        var tracks = trackIds ?? [];
        RequireItemCount(tracks);
        var id = Guid.NewGuid();
        var now = Now();
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        using var tx = conn.BeginTransaction(deferred: false);
        try
        {
            await conn.ExecuteAsync(new CommandDefinition(
                "INSERT INTO playlists (id, name, created_by, created_at, updated_at) VALUES (@Id, @Name, @CreatedBy, @Now, @Now);",
                new { Id = id, Name = playlistName, CreatedBy = createdByUserId, Now = now }, tx, cancellationToken: ct)).ConfigureAwait(false);
        }
        catch (SqliteException ex) when (DataRules.IsForeignKeyViolation(ex))
        {
            throw DataRules.NotFound("The creating user does not exist.");
        }

        await InsertItemsAsync(conn, tx, id, tracks, ct).ConfigureAwait(false);
        tx.Commit();
        return new PlaylistDto(id, playlistName, tracks.ToArray(), now);
    }

    public async Task<PlaylistDto?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        return await GetAsync(conn, null, id, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<PlaylistDto>> ListAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        // One read transaction so headers and items come from the same snapshot.
        using var tx = conn.BeginTransaction(deferred: true);
        var headers = (await conn.QueryAsync<PlaylistRow>(new CommandDefinition(
            "SELECT id AS Id, name AS Name, updated_at AS UpdatedAt FROM playlists ORDER BY name COLLATE NOCASE, id;",
            transaction: tx, cancellationToken: ct)).ConfigureAwait(false)).ToList();
        var items = await conn.QueryAsync<ItemRow>(new CommandDefinition(
            "SELECT playlist_id AS PlaylistId, track_id AS TrackId FROM playlist_items ORDER BY playlist_id, position;",
            transaction: tx, cancellationToken: ct)).ConfigureAwait(false);
        tx.Commit();

        var byPlaylist = items.GroupBy(i => i.PlaylistId).ToDictionary(g => g.Key, g => g.Select(i => i.TrackId).ToArray());
        return headers.Select(h => new PlaylistDto(h.Id, h.Name, byPlaylist.GetValueOrDefault(h.Id, []), h.UpdatedAt)).ToArray();
    }

    public async Task<PlaylistDto?> RenameAsync(Guid id, string name, CancellationToken ct = default)
    {
        var playlistName = DataRules.RequireText(name, "Playlist name", MaxNameLength);
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        var changed = await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE playlists SET name = @Name, updated_at = @Now WHERE id = @Id;",
            new { Id = id, Name = playlistName, Now = Now() }, cancellationToken: ct)).ConfigureAwait(false);
        return changed == 0 ? null : await GetAsync(conn, null, id, ct).ConfigureAwait(false);
    }

    public async Task<PlaylistDto?> SetTracksAsync(Guid id, IReadOnlyList<Guid> trackIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(trackIds);
        RequireItemCount(trackIds);
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        using var tx = conn.BeginTransaction(deferred: false);
        var changed = await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE playlists SET updated_at = @Now WHERE id = @Id;", new { Id = id, Now = Now() }, tx, cancellationToken: ct)).ConfigureAwait(false);
        if (changed == 0)
        {
            return null;
        }

        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM playlist_items WHERE playlist_id = @Id;", new { Id = id }, tx, cancellationToken: ct)).ConfigureAwait(false);
        await InsertItemsAsync(conn, tx, id, trackIds, ct).ConfigureAwait(false);
        var result = await GetAsync(conn, tx, id, ct).ConfigureAwait(false);
        tx.Commit();
        return result;
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        return await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM playlists WHERE id = @Id;", new { Id = id }, cancellationToken: ct)).ConfigureAwait(false) == 1;
    }

    private static async Task InsertItemsAsync(SqliteConnection conn, SqliteTransaction tx, Guid playlistId, IReadOnlyList<Guid> trackIds, CancellationToken ct)
    {
        for (var i = 0; i < trackIds.Count; i++)
        {
            try
            {
                await conn.ExecuteAsync(new CommandDefinition(
                    "INSERT INTO playlist_items (playlist_id, position, track_id) VALUES (@PlaylistId, @Position, @TrackId);",
                    new { PlaylistId = playlistId, Position = i, TrackId = trackIds[i] }, tx, cancellationToken: ct)).ConfigureAwait(false);
            }
            catch (SqliteException ex) when (DataRules.IsForeignKeyViolation(ex))
            {
                throw DataRules.NotFound($"Track {trackIds[i]} does not exist.");
            }
        }
    }

    private static async Task<PlaylistDto?> GetAsync(SqliteConnection conn, SqliteTransaction? tx, Guid id, CancellationToken ct)
    {
        var header = await conn.QuerySingleOrDefaultAsync<PlaylistRow>(new CommandDefinition(
            "SELECT id AS Id, name AS Name, updated_at AS UpdatedAt FROM playlists WHERE id = @Id;",
            new { Id = id }, tx, cancellationToken: ct)).ConfigureAwait(false);
        if (header is null)
        {
            return null;
        }

        var trackIds = await conn.QueryAsync<Guid>(new CommandDefinition(
            "SELECT track_id FROM playlist_items WHERE playlist_id = @Id ORDER BY position;",
            new { Id = id }, tx, cancellationToken: ct)).ConfigureAwait(false);
        return new PlaylistDto(header.Id, header.Name, trackIds.ToArray(), header.UpdatedAt);
    }

    private static void RequireItemCount(IReadOnlyList<Guid> trackIds)
    {
        if (trackIds.Count > MaxItems)
        {
            throw DataRules.Invalid($"A playlist is limited to {MaxItems} tracks.");
        }
    }

    private sealed class PlaylistRow
    {
        public Guid Id { get; set; }

        public string Name { get; set; } = "";

        public DateTimeOffset UpdatedAt { get; set; }
    }

    private sealed class ItemRow
    {
        public Guid PlaylistId { get; set; }

        public Guid TrackId { get; set; }
    }
}
