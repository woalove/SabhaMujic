using Dapper;
using Microsoft.Data.Sqlite;
using SabhaMujic.Contracts;
using SabhaMujic.Data.Internal;

namespace SabhaMujic.Data.Repositories;

/// <summary>
/// Automatic analysis of one track (all positions in seconds). Loop start/end are both set or both null.
/// <see cref="LoopCrossfadeSeconds"/> is the short crossfade used at the loop seam.
/// <see cref="DetailsJson"/> (schema v2) is the engine's complete analysis as JSON (seam correlation, fallback flag, tempo,
/// exact frames); it is opaque here and must be valid JSON when set.
/// </summary>
public sealed record TrackAnalysis(
    Guid TrackId,
    AnalysisStatus Status,
    int AnalysisVersion,
    double? TrimStartSeconds = null,
    double? TrimEndSeconds = null,
    double? LoopStartSeconds = null,
    double? LoopEndSeconds = null,
    double? LoopCrossfadeSeconds = null,
    double? Confidence = null,
    string? Error = null,
    string? DetailsJson = null);

/// <summary>Admin-set loop region; stored separately from <see cref="TrackAnalysis"/> so re-analysis keeps it.</summary>
public sealed record LoopOverride(double StartSeconds, double EndSeconds, double CrossfadeSeconds);

public interface ITrackAnalysisRepository
{
    /// <summary>Inserts or replaces the analysis for the track. NotFound when the track does not exist.</summary>
    Task UpsertAsync(TrackAnalysis analysis, CancellationToken ct = default);

    Task<TrackAnalysis?> GetAsync(Guid trackId, CancellationToken ct = default);

    /// <summary>
    /// Tracks to (re)analyse for <paramref name="analysisVersion"/>, oldest first: never analysed, Pending, or Ready/Failed
    /// under an older version. Tracks currently Analyzing are excluded (see <see cref="ResetInterruptedAsync"/>).
    /// </summary>
    Task<IReadOnlyList<Guid>> ListTracksNeedingAnalysisAsync(int analysisVersion, int limit = 100, CancellationToken ct = default);

    /// <summary>Turns Analyzing back into Pending (call at startup, after a crash interrupted analysis).</summary>
    Task<int> ResetInterruptedAsync(CancellationToken ct = default);

    Task<LoopOverride?> GetLoopOverrideAsync(Guid trackId, CancellationToken ct = default);

    /// <summary>Sets the override, or clears it when <paramref name="region"/> is null. NotFound when the track does not exist.</summary>
    Task SetLoopOverrideAsync(Guid trackId, LoopOverride? region, Guid? updatedByUserId = null, CancellationToken ct = default);
}

public sealed class SqliteTrackAnalysisRepository(SqliteDatabase database) : SqliteRepository(database), ITrackAnalysisRepository
{
    private const int MaxErrorLength = 2000;
    private const double MaxCrossfadeSeconds = 30;
    private const int MaxDetailsJsonLength = 1024 * 1024;

    public async Task UpsertAsync(TrackAnalysis analysis, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        Validate(analysis);
        var error = analysis.Error is { Length: > MaxErrorLength } e ? e[..MaxErrorLength] : analysis.Error;
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        try
        {
            await conn.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO track_analysis (track_id, status, analysis_version, trim_start_seconds, trim_end_seconds,
                    loop_start_seconds, loop_end_seconds, loop_crossfade_seconds, confidence, error, details_json, updated_at)
                VALUES (@TrackId, @Status, @AnalysisVersion, @TrimStartSeconds, @TrimEndSeconds,
                    @LoopStartSeconds, @LoopEndSeconds, @LoopCrossfadeSeconds, @Confidence, @Error, @DetailsJson, @Now)
                ON CONFLICT (track_id) DO UPDATE SET
                    status = excluded.status, analysis_version = excluded.analysis_version,
                    trim_start_seconds = excluded.trim_start_seconds, trim_end_seconds = excluded.trim_end_seconds,
                    loop_start_seconds = excluded.loop_start_seconds, loop_end_seconds = excluded.loop_end_seconds,
                    loop_crossfade_seconds = excluded.loop_crossfade_seconds, confidence = excluded.confidence,
                    error = excluded.error, details_json = excluded.details_json, updated_at = excluded.updated_at;
                """,
                new
                {
                    analysis.TrackId,
                    analysis.Status,
                    analysis.AnalysisVersion,
                    analysis.TrimStartSeconds,
                    analysis.TrimEndSeconds,
                    analysis.LoopStartSeconds,
                    analysis.LoopEndSeconds,
                    analysis.LoopCrossfadeSeconds,
                    analysis.Confidence,
                    Error = error,
                    analysis.DetailsJson,
                    Now = Now(),
                },
                cancellationToken: ct)).ConfigureAwait(false);
        }
        catch (SqliteException ex) when (DataRules.IsForeignKeyViolation(ex))
        {
            throw DataRules.NotFound("The track does not exist.");
        }
    }

    public async Task<TrackAnalysis?> GetAsync(Guid trackId, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        var row = await conn.QuerySingleOrDefaultAsync<AnalysisRow>(new CommandDefinition(
            """
            SELECT track_id AS TrackId, status AS Status, analysis_version AS AnalysisVersion,
                   trim_start_seconds AS TrimStartSeconds, trim_end_seconds AS TrimEndSeconds,
                   loop_start_seconds AS LoopStartSeconds, loop_end_seconds AS LoopEndSeconds,
                   loop_crossfade_seconds AS LoopCrossfadeSeconds, confidence AS Confidence, error AS Error,
                   details_json AS DetailsJson
            FROM track_analysis WHERE track_id = @TrackId;
            """,
            new { TrackId = trackId }, cancellationToken: ct)).ConfigureAwait(false);
        return row?.ToRecord();
    }

    public async Task<IReadOnlyList<Guid>> ListTracksNeedingAnalysisAsync(int analysisVersion, int limit = 100, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(analysisVersion);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        var ids = await conn.QueryAsync<Guid>(new CommandDefinition(
            """
            SELECT t.id FROM tracks t
            LEFT JOIN track_analysis a ON a.track_id = t.id
            WHERE a.track_id IS NULL
               OR a.status = @Pending
               OR (a.status IN (@Ready, @Failed) AND a.analysis_version < @Version)
            ORDER BY t.added_at, t.id
            LIMIT @Limit;
            """,
            new
            {
                Pending = AnalysisStatus.Pending,
                Ready = AnalysisStatus.Ready,
                Failed = AnalysisStatus.Failed,
                Version = analysisVersion,
                Limit = limit,
            },
            cancellationToken: ct)).ConfigureAwait(false);
        return ids.ToArray();
    }

    public async Task<int> ResetInterruptedAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        return await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE track_analysis SET status = @Pending, updated_at = @Now WHERE status = @Analyzing;",
            new { Pending = AnalysisStatus.Pending, Analyzing = AnalysisStatus.Analyzing, Now = Now() },
            cancellationToken: ct)).ConfigureAwait(false);
    }

    public async Task<LoopOverride?> GetLoopOverrideAsync(Guid trackId, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        var row = await conn.QuerySingleOrDefaultAsync<OverrideRow>(new CommandDefinition(
            """
            SELECT start_seconds AS StartSeconds, end_seconds AS EndSeconds, crossfade_seconds AS CrossfadeSeconds
            FROM track_loop_overrides WHERE track_id = @TrackId;
            """,
            new { TrackId = trackId }, cancellationToken: ct)).ConfigureAwait(false);
        return row is null ? null : new LoopOverride(row.StartSeconds, row.EndSeconds, row.CrossfadeSeconds);
    }

    public async Task SetLoopOverrideAsync(Guid trackId, LoopOverride? region, Guid? updatedByUserId = null, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        using var tx = conn.BeginTransaction(deferred: false);
        var exists = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(*) FROM tracks WHERE id = @TrackId;", new { TrackId = trackId }, tx, cancellationToken: ct)).ConfigureAwait(false);
        if (exists == 0)
        {
            throw DataRules.NotFound("The track does not exist.");
        }

        if (region is null)
        {
            await conn.ExecuteAsync(new CommandDefinition(
                "DELETE FROM track_loop_overrides WHERE track_id = @TrackId;", new { TrackId = trackId }, tx, cancellationToken: ct)).ConfigureAwait(false);
        }
        else
        {
            DataRules.RequireFinite(region.StartSeconds, "Loop start", 0, double.MaxValue);
            DataRules.RequireFinite(region.EndSeconds, "Loop end", 0, double.MaxValue);
            DataRules.RequireFinite(region.CrossfadeSeconds, "Loop crossfade", 0, MaxCrossfadeSeconds);
            if (region.EndSeconds <= region.StartSeconds)
            {
                throw DataRules.Invalid("The loop end must be after the loop start.");
            }

            try
            {
                await conn.ExecuteAsync(new CommandDefinition(
                    """
                    INSERT INTO track_loop_overrides (track_id, start_seconds, end_seconds, crossfade_seconds, updated_by, updated_at)
                    VALUES (@TrackId, @StartSeconds, @EndSeconds, @CrossfadeSeconds, @UpdatedBy, @Now)
                    ON CONFLICT (track_id) DO UPDATE SET
                        start_seconds = excluded.start_seconds, end_seconds = excluded.end_seconds,
                        crossfade_seconds = excluded.crossfade_seconds, updated_by = excluded.updated_by,
                        updated_at = excluded.updated_at;
                    """,
                    new { TrackId = trackId, region.StartSeconds, region.EndSeconds, region.CrossfadeSeconds, UpdatedBy = updatedByUserId, Now = Now() },
                    tx, cancellationToken: ct)).ConfigureAwait(false);
            }
            catch (SqliteException ex) when (DataRules.IsForeignKeyViolation(ex))
            {
                throw DataRules.NotFound("The updating user does not exist.");
            }
        }

        tx.Commit();
    }

    private static void Validate(TrackAnalysis a)
    {
        if (!Enum.IsDefined(a.Status))
        {
            throw DataRules.Invalid("Unknown analysis status.");
        }

        if (a.DetailsJson is { } json)
        {
            if (json.Length > MaxDetailsJsonLength)
            {
                throw DataRules.Invalid("The analysis details are too large.");
            }

            try
            {
                using var _ = System.Text.Json.JsonDocument.Parse(json);
            }
            catch (System.Text.Json.JsonException)
            {
                throw DataRules.Invalid("The analysis details must be valid JSON.");
            }
        }

        if (a.AnalysisVersion < 0)
        {
            throw DataRules.Invalid("The analysis version must not be negative.");
        }

        CheckOptional(a.TrimStartSeconds, "Trim start", double.MaxValue);
        CheckOptional(a.TrimEndSeconds, "Trim end", double.MaxValue);
        CheckOptional(a.LoopStartSeconds, "Loop start", double.MaxValue);
        CheckOptional(a.LoopEndSeconds, "Loop end", double.MaxValue);
        CheckOptional(a.LoopCrossfadeSeconds, "Loop crossfade", MaxCrossfadeSeconds);
        CheckOptional(a.Confidence, "Confidence", 1);
        if ((a.LoopStartSeconds is null) != (a.LoopEndSeconds is null))
        {
            throw DataRules.Invalid("Loop start and end must both be set or both be empty.");
        }

        if (a.LoopStartSeconds is { } start && a.LoopEndSeconds is { } end && end <= start)
        {
            throw DataRules.Invalid("The loop end must be after the loop start.");
        }
    }

    private static void CheckOptional(double? value, string field, double max)
    {
        if (value is { } v)
        {
            DataRules.RequireFinite(v, field, 0, max);
        }
    }

    private sealed class AnalysisRow
    {
        public Guid TrackId { get; set; }

        public AnalysisStatus Status { get; set; }

        public int AnalysisVersion { get; set; }

        public double? TrimStartSeconds { get; set; }

        public double? TrimEndSeconds { get; set; }

        public double? LoopStartSeconds { get; set; }

        public double? LoopEndSeconds { get; set; }

        public double? LoopCrossfadeSeconds { get; set; }

        public double? Confidence { get; set; }

        public string? Error { get; set; }

        public string? DetailsJson { get; set; }

        public TrackAnalysis ToRecord() => new(TrackId, Status, AnalysisVersion, TrimStartSeconds, TrimEndSeconds,
            LoopStartSeconds, LoopEndSeconds, LoopCrossfadeSeconds, Confidence, Error, DetailsJson);
    }

    private sealed class OverrideRow
    {
        public double StartSeconds { get; set; }

        public double EndSeconds { get; set; }

        public double CrossfadeSeconds { get; set; }
    }
}
