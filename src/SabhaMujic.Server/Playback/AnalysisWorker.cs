using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using SabhaMujic.Audio.Analysis;
using SabhaMujic.Audio.Decoding;
using SabhaMujic.Contracts;
using SabhaMujic.Data.Repositories;
using SabhaMujic.Data.Storage;
using SabhaMujic.Server.Application;
using AudioAnalysis = SabhaMujic.Audio.Analysis.TrackAnalysis;
using StoredAnalysis = SabhaMujic.Data.Repositories.TrackAnalysis;

namespace SabhaMujic.Server.Playback;

/// <summary>
/// Background Smart Loop analysis: one dedicated below-normal-priority thread, one track at a time.
/// For each track: status Pending → Analyzing → Ready (or Failed with a client-safe reason), the result goes to the
/// database and the <see cref="StoreTrackResolver"/>, and <see cref="LibraryChangeKind.AnalysisUpdated"/> plus (for
/// imports) <see cref="ImportStages.Analyzing"/>/<see cref="ImportStages.Done"/> progress are published.
/// Results the engine computed itself (<c>PlaybackEngine.TrackAnalyzed</c>) are persisted through the same thread via
/// <see cref="AcceptEngineResult"/>. A bad file only fails its own track. Dispose cancels the running analysis.
/// </summary>
public sealed class AnalysisWorker : IAnalysisQueue, IDisposable
{
    private readonly IAudioDecoderFactory _decoders;
    private readonly ITrackRepository _tracks;
    private readonly ITrackAnalysisRepository _analysis;
    private readonly ProtectedStore _store;
    private readonly StoreTrackResolver _resolver;
    private readonly EventBroadcaster _events;
    private readonly AnalysisOptions _options;
    private readonly ILogger _logger;
    private readonly BlockingCollection<Job> _jobs = new();
    private readonly ConcurrentDictionary<Guid, byte> _queued = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Thread _thread;
    private int _completed;

    public AnalysisWorker(IAudioDecoderFactory decoders, ITrackRepository tracks, ITrackAnalysisRepository analysis, ProtectedStore store,
        StoreTrackResolver resolver, EventBroadcaster events, ILogger<AnalysisWorker> logger, AnalysisOptions? options = null)
    {
        _decoders = decoders;
        _tracks = tracks;
        _analysis = analysis;
        _store = store;
        _resolver = resolver;
        _events = events;
        _logger = logger;
        _options = options ?? new AnalysisOptions();
        _thread = new Thread(Run) { IsBackground = true, Name = "SabhaMujic analysis", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    public int AnalysisVersion => LoopAnalyzer.AnalysisVersion;

    /// <summary>Tracks analysed or stored so far (diagnostics/tests).</summary>
    public int CompletedCount => Volatile.Read(ref _completed);

    /// <summary>Jobs waiting or running.</summary>
    public int PendingCount => _jobs.Count + (_queued.IsEmpty ? 0 : 1);

    public void Enqueue(Guid trackId, ImportProgressTarget? progress = null)
    {
        if (_stop.IsCancellationRequested || !_queued.TryAdd(trackId, 0))
        {
            return;
        }

        try
        {
            _jobs.Add(new Job(trackId, progress, null));
        }
        catch (InvalidOperationException)
        {
            // Shutting down.
        }
    }

    /// <summary>Persists an analysis the engine computed in the background (called from any thread; never blocks).</summary>
    public void AcceptEngineResult(Guid trackId, AudioAnalysis analysis)
    {
        if (_stop.IsCancellationRequested)
        {
            return;
        }

        try
        {
            _jobs.Add(new Job(trackId, null, analysis));
        }
        catch (InvalidOperationException)
        {
        }
    }

    public void Dispose()
    {
        if (_stop.IsCancellationRequested)
        {
            return;
        }

        _stop.Cancel();
        _jobs.CompleteAdding();
        _thread.Join(TimeSpan.FromSeconds(10));
    }

    private void Run()
    {
        var ct = _stop.Token;
        try
        {
            foreach (var job in _jobs.GetConsumingEnumerable(ct))
            {
                try
                {
                    if (job.Precomputed is { } precomputed)
                    {
                        StoreEngineResult(job.TrackId, precomputed, ct);
                    }
                    else
                    {
                        Analyze(job, ct);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Analysis job for track {TrackId} failed unexpectedly.", job.TrackId);
                }
                finally
                {
                    if (job.Precomputed is null)
                    {
                        _queued.TryRemove(job.TrackId, out _);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void Analyze(Job job, CancellationToken ct)
    {
        var trackId = job.TrackId;
        var file = Wait(_tracks.GetFileInfoAsync(trackId, ct));
        if (file is null)
        {
            return; // deleted meanwhile
        }

        if (!TrySave(new StoredAnalysis(trackId, AnalysisStatus.Analyzing, AnalysisVersion), ct))
        {
            return;
        }

        Changed(trackId);
        Progress(job, ImportStages.Analyzing);

        AudioAnalysis result;
        try
        {
            result = LoopAnalyzer.Analyze(_decoders, _store.GetPhysicalPath(file.FileKey), _options, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Leave it Analyzing: MaintenanceService.StartupAsync resets it to Pending and re-queues it.
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            var reason = ex is AudioDecoderException ? ex.Message : "The audio could not be analysed.";
            _logger.LogWarning(ex, "Analysis of track {TrackId} failed.", trackId);
            if (TrySave(new StoredAnalysis(trackId, AnalysisStatus.Failed, AnalysisVersion, Error: reason), ct))
            {
                _resolver.Invalidate(trackId);
                Changed(trackId);
                Progress(job, ImportStages.Failed);
            }

            Interlocked.Increment(ref _completed);
            return;
        }

        if (TrySave(AnalysisMapping.ToStored(trackId, result), ct))
        {
            CorrectDuration(trackId, result, ct);
            _resolver.Invalidate(trackId);
            Changed(trackId);
            Progress(job, ImportStages.Done);
        }

        Interlocked.Increment(ref _completed);
    }

    private void StoreEngineResult(Guid trackId, AudioAnalysis analysis, CancellationToken ct)
    {
        var existing = Wait(_analysis.GetAsync(trackId, ct));
        if (existing is { Status: AnalysisStatus.Ready } && existing.AnalysisVersion >= analysis.Version)
        {
            return; // the worker (or an earlier run) already stored an equal or newer result
        }

        if (TrySave(AnalysisMapping.ToStored(trackId, analysis), ct))
        {
            CorrectDuration(trackId, analysis, ct);
            _resolver.Invalidate(trackId);
            Changed(trackId);
            Interlocked.Increment(ref _completed);
        }
    }

    /// <summary>Compressed formats only estimate their length when opened; the analysis decoded every frame.</summary>
    private void CorrectDuration(Guid trackId, AudioAnalysis analysis, CancellationToken ct)
    {
        try
        {
            Wait(_tracks.SetDurationAsync(trackId, analysis.DurationSeconds, ct));
        }
        catch (SabhaMujicException ex)
        {
            _logger.LogDebug("Could not update the duration of track {TrackId}: {Message}", trackId, ex.Message);
        }
    }

    /// <summary>Saves the analysis row; false when the track was deleted meanwhile.</summary>
    private bool TrySave(StoredAnalysis analysis, CancellationToken ct)
    {
        try
        {
            Wait(_analysis.UpsertAsync(analysis, ct));
            return true;
        }
        catch (SabhaMujicException ex) when (ex.Code == ErrorCode.NotFound)
        {
            return false;
        }
    }

    private void Changed(Guid trackId) => _events.PublishLibraryChanged(new LibraryChangedDto(LibraryChangeKind.AnalysisUpdated, trackId));

    private void Progress(Job job, string stage)
    {
        if (job.Progress is { } p)
        {
            _events.PublishImportProgress(p.SessionId, new ImportProgressDto(p.UploadId, p.FileName, p.TotalBytes, p.TotalBytes, stage));
        }
    }

    // Repository calls complete synchronously (Microsoft.Data.Sqlite); blocking here keeps the work on this thread.
    private static T Wait<T>(Task<T> task) => task.GetAwaiter().GetResult();

    private static void Wait(Task task) => task.GetAwaiter().GetResult();

    private sealed record Job(Guid TrackId, ImportProgressTarget? Progress, AudioAnalysis? Precomputed);
}
