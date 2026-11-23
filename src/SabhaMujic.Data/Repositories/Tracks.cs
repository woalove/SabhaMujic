using System.Text;
using Dapper;
using Microsoft.Data.Sqlite;
using SabhaMujic.Contracts;
using SabhaMujic.Data.Internal;
using SabhaMujic.Data.Storage;

namespace SabhaMujic.Data.Repositories;

/// <summary>
/// A track to add after its file is in the <see cref="ProtectedStore"/>. <see cref="ContentHash"/> is the lowercase
/// SHA-256 hex of the file and <see cref="FileKey"/> the store key (<c>&lt;hash&gt;.&lt;ext&gt;</c>).
/// <see cref="CategoryIds"/> are its tags (null or empty: none).
/// </summary>
public sealed record NewTrack(
    string ContentHash,
    string FileKey,
    string Title,
    string? Artist,
    IReadOnlyList<Guid>? CategoryIds,
    double DurationSeconds,
    long SizeBytes,
    string? OriginalFileName = null,
    Guid? AddedByUserId = null);

/// <summary>
/// Result of <see cref="ITrackRepository.AddAsync"/>. When <see cref="WasDuplicate"/> is true, <see cref="Track"/> is the
/// existing track and <see cref="FileKey"/> its stored key (which may differ from the new one if the extension differed;
/// the caller should then delete the newly stored file).
/// </summary>
public sealed record TrackAddResult(TrackDto Track, string FileKey, bool WasDuplicate);

/// <summary>Service-internal file information for a track. Never sent to clients.</summary>
public sealed record TrackFileInfo(Guid TrackId, string FileKey, string ContentHash, long SizeBytes, string? OriginalFileName);

public interface ITrackRepository
{
    /// <summary>Adds a track, or returns the existing one with <c>WasDuplicate</c> when the content hash is known. Race-safe.</summary>
    Task<TrackAddResult> AddAsync(NewTrack track, CancellationToken ct = default);

    Task<TrackDto?> GetAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Batch lookup in one query. Order follows <paramref name="ids"/>; unknown ids are omitted and a repeated id is
    /// returned once (at its first position). InvalidArgument above <see cref="SqliteTrackRepository.MaxBatch"/> ids.
    /// </summary>
    Task<IReadOnlyList<TrackDto>> GetManyAsync(IReadOnlyList<Guid> ids, CancellationToken ct = default);

    Task<TrackFileInfo?> GetFileInfoAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Updates title, artist and tags (the tags are replaced). Null when the track is unknown; NotFound when a category is
    /// unknown (nothing changes then).
    /// </summary>
    Task<TrackDto?> UpdateMetadataAsync(TrackUpdateDto update, CancellationToken ct = default);

    Task<bool> SetDurationAsync(Guid id, double durationSeconds, CancellationToken ct = default);

    /// <summary>
    /// Deletes the track (analysis, loop override, playlist and queue entries cascade). Returns the stored file key so the
    /// caller can delete the file, or null when the track was not found.
    /// </summary>
    Task<string?> DeleteAsync(Guid id, CancellationToken ct = default);

    /// <summary>Filters by tag (category) and/or a literal, case-insensitive substring of title or artist. Ordered by title.</summary>
    Task<PagedResult<TrackDto>> QueryAsync(TrackQuery query, CancellationToken ct = default);
}

public sealed class SqliteTrackRepository(SqliteDatabase database) : SqliteRepository(database), ITrackRepository
{
    public const int MaxTitleLength = 300;
    public const int MaxArtistLength = 300;
    public const int MaxSearchLength = 200;
    public const int MaxTake = 1000;
    public const int MaxBatch = 1000;
    public const int MaxTagsPerTrack = 50;
    private const int MaxOriginalFileNameLength = 255;

    internal const string SelectTrackDto =
        """
        SELECT t.id AS Id, t.title AS Title, t.artist AS Artist,
               (SELECT json_group_array(l.category_id) FROM (
                    SELECT tc.category_id FROM track_categories tc JOIN categories c ON c.id = tc.category_id
                    WHERE tc.track_id = t.id ORDER BY c.sort_order, c.name_key) l) AS CategoryIdsJson,
               t.duration_seconds AS DurationSeconds, t.added_at AS AddedAt,
               a.status AS AnalysisStatus, a.loop_start_seconds AS LoopStart, a.loop_end_seconds AS LoopEnd,
               a.loop_crossfade_seconds AS LoopCrossfade, a.confidence AS Confidence,
               o.start_seconds AS OverrideStart, o.end_seconds AS OverrideEnd, o.crossfade_seconds AS OverrideCrossfade
        FROM tracks t
        LEFT JOIN track_analysis a ON a.track_id = t.id
        LEFT JOIN track_loop_overrides o ON o.track_id = t.id
        """;

    public async Task<TrackAddResult> AddAsync(NewTrack track, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(track);
        var hash = track.ContentHash ?? "";
        if (hash.Length != 64 || !hash.All(char.IsAsciiHexDigitLower))
        {
            throw DataRules.Invalid("The content hash must be 64 lowercase hexadecimal characters.");
        }

        if (!ProtectedStore.IsValidFileKey(track.FileKey) || !track.FileKey.StartsWith(hash + ".", StringComparison.Ordinal))
        {
            throw DataRules.Invalid("The file key must be '<content hash>.<extension>'.");
        }

        var title = DataRules.RequireText(track.Title, "Title", MaxTitleLength);
        var artist = DataRules.OptionalText(track.Artist, "Artist", MaxArtistLength);
        DataRules.RequireFinite(track.DurationSeconds, "Duration", 0, double.MaxValue);
        if (track.SizeBytes < 0)
        {
            throw DataRules.Invalid("The size must not be negative.");
        }

        var originalName = string.IsNullOrWhiteSpace(track.OriginalFileName) ? null
            : track.OriginalFileName.Length > MaxOriginalFileNameLength ? track.OriginalFileName[..MaxOriginalFileNameLength]
            : track.OriginalFileName;
        var tags = ValidateTags(track.CategoryIds);

        var id = Guid.NewGuid();
        var now = Now();
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        int inserted;
        using (var tx = conn.BeginTransaction(deferred: false))
        {
            try
            {
                // ON CONFLICT DO NOTHING makes "add or find existing" atomic, so concurrent imports of the same file race safely.
                inserted = await conn.ExecuteAsync(new CommandDefinition(
                    """
                    INSERT INTO tracks (id, content_hash, file_key, title, title_key, artist, artist_key,
                                        duration_seconds, size_bytes, original_file_name, added_by, added_at, updated_at)
                    VALUES (@Id, @Hash, @FileKey, @Title, @TitleKey, @Artist, @ArtistKey,
                            @Duration, @Size, @OriginalName, @AddedBy, @Now, @Now)
                    ON CONFLICT (content_hash) DO NOTHING;
                    """,
                    new
                    {
                        Id = id,
                        Hash = hash,
                        track.FileKey,
                        Title = title,
                        TitleKey = DataRules.FoldKey(title),
                        Artist = artist,
                        ArtistKey = artist is null ? null : DataRules.FoldKey(artist),
                        Duration = track.DurationSeconds,
                        Size = track.SizeBytes,
                        OriginalName = originalName,
                        AddedBy = track.AddedByUserId,
                        Now = now,
                    },
                    tx,
                    cancellationToken: ct)).ConfigureAwait(false);

                // A duplicate keeps the existing track's tags; only a new track gets the requested ones.
                if (inserted == 1)
                {
                    await InsertTagsAsync(conn, tx, id, tags, ct).ConfigureAwait(false);
                }

                tx.Commit();
            }
            catch (SqliteException ex) when (DataRules.IsForeignKeyViolation(ex))
            {
                throw DataRules.NotFound("The category or user does not exist.");
            }
            catch (SqliteException ex) when (DataRules.IsUniqueViolation(ex))
            {
                throw DataRules.Conflict("A different track already uses this file key.");
            }
        }

        if (inserted == 1)
        {
            return new TrackAddResult((await GetAsync(conn, id, ct).ConfigureAwait(false))!, track.FileKey, WasDuplicate: false);
        }

        var existing = await conn.QuerySingleAsync<(string Id, string FileKey)>(new CommandDefinition(
            "SELECT id, file_key FROM tracks WHERE content_hash = @Hash;", new { Hash = hash }, cancellationToken: ct)).ConfigureAwait(false);
        var existingId = Guid.ParseExact(existing.Id, "D");
        return new TrackAddResult((await GetAsync(conn, existingId, ct).ConfigureAwait(false))!, existing.FileKey, WasDuplicate: true);
    }

    public async Task<TrackDto?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        return await GetAsync(conn, id, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<TrackDto>> GetManyAsync(IReadOnlyList<Guid> ids, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count > MaxBatch)
        {
            throw DataRules.Invalid($"At most {MaxBatch} tracks can be looked up at once.");
        }

        var distinct = ids.Distinct().ToArray();
        if (distinct.Length == 0)
        {
            return [];
        }

        // The ids travel as one JSON array parameter (json_each), so the statement stays fully parameterized.
        var idsJson = System.Text.Json.JsonSerializer.Serialize(distinct.Select(DapperTypeHandlers.ToDb));
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        var rows = await conn.QueryAsync<TrackRow>(new CommandDefinition(
            SelectTrackDto + " WHERE t.id IN (SELECT value FROM json_each(@Ids));", new { Ids = idsJson }, cancellationToken: ct)).ConfigureAwait(false);
        var byId = rows.ToDictionary(r => r.Id, r => r.ToDto());
        return distinct.Where(byId.ContainsKey).Select(id => byId[id]).ToArray();
    }

    public async Task<TrackFileInfo?> GetFileInfoAsync(Guid id, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        var row = await conn.QuerySingleOrDefaultAsync<FileInfoRow>(new CommandDefinition(
            """
            SELECT id AS TrackId, file_key AS FileKey, content_hash AS ContentHash, size_bytes AS SizeBytes,
                   original_file_name AS OriginalFileName
            FROM tracks WHERE id = @Id;
            """,
            new { Id = id }, cancellationToken: ct)).ConfigureAwait(false);
        return row is null ? null : new TrackFileInfo(row.TrackId, row.FileKey, row.ContentHash, row.SizeBytes, row.OriginalFileName);
    }

    public async Task<TrackDto?> UpdateMetadataAsync(TrackUpdateDto update, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        var title = DataRules.RequireText(update.Title, "Title", MaxTitleLength);
        var artist = DataRules.OptionalText(update.Artist, "Artist", MaxArtistLength);
        var tags = ValidateTags(update.CategoryIds);
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        int changed;
        using (var tx = conn.BeginTransaction(deferred: false))
        {
            try
            {
                changed = await conn.ExecuteAsync(new CommandDefinition(
                    """
                    UPDATE tracks SET title = @Title, title_key = @TitleKey, artist = @Artist, artist_key = @ArtistKey,
                                      updated_at = @Now
                    WHERE id = @Id;
                    """,
                    new
                    {
                        Id = update.TrackId,
                        Title = title,
                        TitleKey = DataRules.FoldKey(title),
                        Artist = artist,
                        ArtistKey = artist is null ? null : DataRules.FoldKey(artist),
                        Now = Now(),
                    },
                    tx,
                    cancellationToken: ct)).ConfigureAwait(false);
                if (changed == 0)
                {
                    return null;
                }

                await conn.ExecuteAsync(new CommandDefinition(
                    "DELETE FROM track_categories WHERE track_id = @Id;", new { Id = update.TrackId }, tx, cancellationToken: ct)).ConfigureAwait(false);
                await InsertTagsAsync(conn, tx, update.TrackId, tags, ct).ConfigureAwait(false);
                tx.Commit();
            }
            catch (SqliteException ex) when (DataRules.IsForeignKeyViolation(ex))
            {
                throw DataRules.NotFound("The category does not exist.");
            }
        }

        return await GetAsync(conn, update.TrackId, ct).ConfigureAwait(false);
    }

    public async Task<bool> SetDurationAsync(Guid id, double durationSeconds, CancellationToken ct = default)
    {
        DataRules.RequireFinite(durationSeconds, "Duration", 0, double.MaxValue);
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        return await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE tracks SET duration_seconds = @Duration, updated_at = @Now WHERE id = @Id;",
            new { Id = id, Duration = durationSeconds, Now = Now() }, cancellationToken: ct)).ConfigureAwait(false) == 1;
    }

    public async Task<string?> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        return await conn.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(
            "DELETE FROM tracks WHERE id = @Id RETURNING file_key;", new { Id = id }, cancellationToken: ct)).ConfigureAwait(false);
    }

    public async Task<PagedResult<TrackDto>> QueryAsync(TrackQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Skip < 0)
        {
            throw DataRules.Invalid("Skip must not be negative.");
        }

        if (query.Take < 1)
        {
            throw DataRules.Invalid("Take must be at least 1.");
        }

        var take = Math.Min(query.Take, MaxTake);
        var search = query.Search?.Trim();
        if (search is { Length: > MaxSearchLength })
        {
            throw DataRules.Invalid($"The search text must be at most {MaxSearchLength} characters.");
        }

        // Only fixed SQL fragments are concatenated; every user value is a bound parameter.
        var where = new StringBuilder(" WHERE 1 = 1");
        if (query.CategoryId is not null)
        {
            where.Append(" AND EXISTS (SELECT 1 FROM track_categories tq WHERE tq.track_id = t.id AND tq.category_id = @CategoryId)");
        }

        string? pattern = null;
        if (!string.IsNullOrEmpty(search))
        {
            // Fold first, then escape: folding can turn e.g. a full-width '％' into '%'.
            pattern = "%" + DataRules.EscapeLike(DataRules.FoldKey(search)) + "%";
            where.Append(@" AND (t.title_key LIKE @Pattern ESCAPE '\' OR t.artist_key LIKE @Pattern ESCAPE '\')");
        }

        var parameters = new { query.CategoryId, Pattern = pattern, Skip = query.Skip, Take = take };
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        var total = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(*) FROM tracks t" + where + ";", parameters, cancellationToken: ct)).ConfigureAwait(false);
        var rows = await conn.QueryAsync<TrackRow>(new CommandDefinition(
            SelectTrackDto + where + " ORDER BY t.title_key, t.id LIMIT @Take OFFSET @Skip;", parameters, cancellationToken: ct)).ConfigureAwait(false);
        return new PagedResult<TrackDto>(rows.Select(r => r.ToDto()).ToArray(), (int)total);
    }

    /// <summary>Distinct tags, at most <see cref="MaxTagsPerTrack"/>; null means none.</summary>
    private static Guid[] ValidateTags(IReadOnlyList<Guid>? categoryIds)
    {
        if (categoryIds is null)
        {
            return [];
        }

        var tags = categoryIds.Distinct().ToArray();
        if (tags.Length > MaxTagsPerTrack)
        {
            throw DataRules.Invalid($"A track can have at most {MaxTagsPerTrack} categories.");
        }

        if (tags.Contains(Guid.Empty))
        {
            throw DataRules.Invalid("A category id is missing.");
        }

        return tags;
    }

    private static async Task InsertTagsAsync(SqliteConnection conn, SqliteTransaction tx, Guid trackId, Guid[] tags, CancellationToken ct)
    {
        foreach (var categoryId in tags)
        {
            await conn.ExecuteAsync(new CommandDefinition(
                "INSERT INTO track_categories (track_id, category_id) VALUES (@TrackId, @CategoryId);",
                new { TrackId = trackId, CategoryId = categoryId }, tx, cancellationToken: ct)).ConfigureAwait(false);
        }
    }

    internal static async Task<TrackDto?> GetAsync(SqliteConnection conn, Guid id, CancellationToken ct, SqliteTransaction? tx = null)
    {
        var row = await conn.QuerySingleOrDefaultAsync<TrackRow>(new CommandDefinition(
            SelectTrackDto + " WHERE t.id = @Id;", new { Id = id }, tx, cancellationToken: ct)).ConfigureAwait(false);
        return row?.ToDto();
    }

    internal sealed class TrackRow
    {
        public Guid Id { get; set; }

        public string Title { get; set; } = "";

        public string? Artist { get; set; }

        /// <summary>JSON array of category ids (json_group_array), in category sort order.</summary>
        public string? CategoryIdsJson { get; set; }

        public double DurationSeconds { get; set; }

        public DateTimeOffset AddedAt { get; set; }

        public AnalysisStatus? AnalysisStatus { get; set; }

        public double? LoopStart { get; set; }

        public double? LoopEnd { get; set; }

        public double? LoopCrossfade { get; set; }

        public double? Confidence { get; set; }

        public double? OverrideStart { get; set; }

        public double? OverrideEnd { get; set; }

        public double? OverrideCrossfade { get; set; }

        /// <summary>A manual override wins; otherwise the analysed loop once analysis is Ready.</summary>
        public TrackDto ToDto()
        {
            var status = AnalysisStatus ?? Contracts.AnalysisStatus.Pending;
            LoopRegionDto? loop = null;
            if (OverrideStart is { } os && OverrideEnd is { } oe)
            {
                loop = new LoopRegionDto(os, oe, OverrideCrossfade ?? 0, 1.0, IsManualOverride: true);
            }
            else if (status == Contracts.AnalysisStatus.Ready && LoopStart is { } ls && LoopEnd is { } le)
            {
                loop = new LoopRegionDto(ls, le, LoopCrossfade ?? 0, Confidence ?? 0, IsManualOverride: false);
            }

            return new TrackDto(Id, Title, Artist, ParseTags(CategoryIdsJson), DurationSeconds, status, loop, AddedAt);
        }

        private static IReadOnlyList<Guid> ParseTags(string? json)
        {
            if (string.IsNullOrEmpty(json) || json == "[]")
            {
                return [];
            }

            var ids = System.Text.Json.JsonSerializer.Deserialize<string[]>(json) ?? [];
            return ids.Select(id => Guid.ParseExact(id, "D")).ToArray();
        }
    }

    private sealed class FileInfoRow
    {
        public Guid TrackId { get; set; }

        public string FileKey { get; set; } = "";

        public string ContentHash { get; set; } = "";

        public long SizeBytes { get; set; }

        public string? OriginalFileName { get; set; }
    }
}
