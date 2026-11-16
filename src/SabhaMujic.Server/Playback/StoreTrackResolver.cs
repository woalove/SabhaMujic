using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using SabhaMujic.Audio;
using SabhaMujic.Audio.Analysis;
using SabhaMujic.Audio.Engine;
using SabhaMujic.Contracts;
using SabhaMujic.Data.Repositories;
using SabhaMujic.Data.Storage;
using AudioAnalysis = SabhaMujic.Audio.Analysis.TrackAnalysis;

namespace SabhaMujic.Server.Playback;

/// <summary>
/// Resolves a track id to its protected-store path and analysis (admin loop override applied) for the engine.
/// <para>
/// The engine calls <see cref="ResolveAsync"/> synchronously on its control thread, so results are cached in memory and
/// the backend warms the cache on request threads (<see cref="WarmAsync"/>) before posting commands. A miss (e.g. the
/// engine advancing to a track nobody warmed, after a cache invalidation) does three indexed SQLite reads (well under a
/// millisecond; in WAL mode readers never wait for writers). Nothing here calls back into the engine.
/// </para>
/// </summary>
public sealed class StoreTrackResolver : ITrackResolver
{
    private readonly ITrackRepository _tracks;
    private readonly ITrackAnalysisRepository _analysis;
    private readonly ProtectedStore _store;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<Guid, ResolvedTrack> _cache = new();

    // Bumped by every invalidation, so a load that read the database before a change cannot cache stale data after it.
    private readonly ConcurrentDictionary<Guid, long> _generations = new();

    public StoreTrackResolver(ITrackRepository tracks, ITrackAnalysisRepository analysis, ProtectedStore store, ILogger<StoreTrackResolver> logger)
    {
        _tracks = tracks;
        _analysis = analysis;
        _store = store;
        _logger = logger;
    }

    public int CachedCount => _cache.Count;

    public async Task<ResolvedTrack?> ResolveAsync(Guid trackId, CancellationToken ct = default)
    {
        if (_cache.TryGetValue(trackId, out var cached))
        {
            return cached;
        }

        var generation = _generations.GetValueOrDefault(trackId);
        var resolved = await LoadAsync(trackId, ct).ConfigureAwait(false);
        if (resolved is not null && _generations.GetValueOrDefault(trackId) == generation)
        {
            _cache[trackId] = resolved;
        }

        return resolved;
    }

    /// <summary>Loads tracks into the cache ahead of an engine command (call from request threads).</summary>
    public async Task WarmAsync(IEnumerable<Guid> trackIds, CancellationToken ct = default)
    {
        foreach (var id in trackIds.Distinct())
        {
            await ResolveAsync(id, ct).ConfigureAwait(false);
        }
    }

    /// <summary>The cached file path, if the track was resolved before (used to wait for the engine to release it).</summary>
    public string? TryGetCachedPath(Guid trackId) => _cache.TryGetValue(trackId, out var cached) ? cached.Path : null;

    /// <summary>Drops the cached entry so the next resolve re-reads metadata, analysis and override.</summary>
    public void Invalidate(Guid trackId)
    {
        _generations.AddOrUpdate(trackId, 1, (_, generation) => generation + 1);
        _cache.TryRemove(trackId, out _);
    }

    /// <summary>Forgets everything about a deleted track.</summary>
    public void Forget(Guid trackId) => Invalidate(trackId);

    private async Task<ResolvedTrack?> LoadAsync(Guid trackId, CancellationToken ct)
    {
        var file = await _tracks.GetFileInfoAsync(trackId, ct).ConfigureAwait(false);
        if (file is null)
        {
            return null;
        }

        var track = await _tracks.GetAsync(trackId, ct).ConfigureAwait(false);
        var stored = await _analysis.GetAsync(trackId, ct).ConfigureAwait(false);
        var loopOverride = await _analysis.GetLoopOverrideAsync(trackId, ct).ConfigureAwait(false);
        var duration = track?.DurationSeconds ?? 0;

        AudioAnalysis? analysis = null;
        if (stored is { Status: AnalysisStatus.Ready })
        {
            analysis = AnalysisMapping.FromStored(stored, duration);
        }

        if (loopOverride is not null)
        {
            var dto = new LoopRegionDto(loopOverride.StartSeconds, loopOverride.EndSeconds, loopOverride.CrossfadeSeconds, 1.0, true);
            var baseAnalysis = analysis ?? (duration > 0 ? AudioAnalysis.Unanalyzed(AudioFormat.SecondsToFrames(duration)) : null);
            try
            {
                analysis = baseAnalysis?.WithLoopOverride(dto) ?? analysis;
            }
            catch (SabhaMujicException ex)
            {
                _logger.LogWarning("Ignoring the loop override of track {TrackId}: {Reason}", trackId, ex.Message);
            }
        }

        return new ResolvedTrack(trackId, _store.GetPhysicalPath(file.FileKey), analysis);
    }
}
