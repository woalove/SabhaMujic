using Microsoft.Extensions.Logging;
using SabhaMujic.Audio.Engine;
using SabhaMujic.Contracts;
using SabhaMujic.Data.Repositories;
using SabhaMujic.Server.Application;

namespace SabhaMujic.Server.Playback;

/// <summary>
/// Keeps the queue and playback settings in the database so the service restores them after a restart.
/// <para>
/// Restore: settings (volume, mute, loop mode, crossfade) and the queue with its queue item ids and current item.
/// Playback restarts <b>stopped</b>; the next Resume continues the current item at the saved position.
/// Save: debounced (default 1 s) after any queue or settings change, and immediately on <see cref="FlushAsync"/>
/// (shutdown). The position is written with every save, so after a crash it is that of the last save.
/// </para>
/// <para>
/// A failed save (database busy or locked, disk full) leaves the state dirty and is retried by itself with backoff
/// (<see cref="RetryInitialDelay"/> doubling up to <see cref="RetryMaxDelay"/>) until it succeeds, without waiting for
/// another change. One error is logged per outage, and one information entry when saving works again.
/// </para>
/// </summary>
public sealed class PlaybackPersistence : IAsyncDisposable
{
    private readonly IPlaybackStateRepository _repository;
    private readonly IPlaybackBackend _backend;
    private readonly ILogger _logger;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _debounce;
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly Lock _gate = new();
    private ITimer? _timer;
    private PlaybackSettings? _savedSettings;
    private long _savedQueueRevision = -1;
    private bool _attached;
    private bool _disposed;
    private bool _dirty;
    private int _consecutiveFailures;

    public PlaybackPersistence(IPlaybackStateRepository repository, IPlaybackBackend backend, ILogger<PlaybackPersistence> logger,
        TimeProvider? clock = null, TimeSpan? debounce = null)
    {
        _repository = repository;
        _backend = backend;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
        _debounce = debounce ?? TimeSpan.FromSeconds(1);
    }

    /// <summary>First retry delay after a failed save; doubles per consecutive failure.</summary>
    public static TimeSpan RetryInitialDelay { get; } = TimeSpan.FromSeconds(1);

    /// <summary>Longest delay between retries while saving keeps failing.</summary>
    public static TimeSpan RetryMaxDelay { get; } = TimeSpan.FromSeconds(30);

    /// <summary>Number of completed saves (diagnostics/tests).</summary>
    public int SaveCount { get; private set; }

    /// <summary>True while a change has not reached the database yet (pending debounce, or a failed save awaiting retry).</summary>
    public bool IsDirty
    {
        get
        {
            lock (_gate)
            {
                return _dirty;
            }
        }
    }

    /// <summary>Failed save attempts since the last successful one (0 when healthy).</summary>
    public int ConsecutiveFailures
    {
        get
        {
            lock (_gate)
            {
                return _consecutiveFailures;
            }
        }
    }

    /// <summary>The delay before retry number <paramref name="failures"/> (1-based): 1 s, 2 s, 4 s … capped at 30 s.</summary>
    public static TimeSpan RetryDelay(int failures)
    {
        var exponent = Math.Clamp(failures - 1, 0, 16);
        var delay = TimeSpan.FromTicks(RetryInitialDelay.Ticks << exponent);
        return delay < RetryMaxDelay ? delay : RetryMaxDelay;
    }

    /// <summary>
    /// Applies the persisted settings and queue to the engine, then starts saving on change.
    /// Call once at startup before clients connect.
    /// </summary>
    public async Task RestoreAsync(PlaybackEngine engine, CancellationToken ct = default)
    {
        var settings = await _repository.LoadSettingsAsync(ct).ConfigureAwait(false);
        var queue = await _repository.LoadQueueAsync(ct).ConfigureAwait(false);

        if (queue.Items.Count > 0)
        {
            await engine.RestoreQueueAsync(queue.Items, queue.CurrentIndex, queue.PositionSeconds > 0 ? queue.PositionSeconds : null, ct)
                .ConfigureAwait(false);
        }

        await engine.SetCrossfadeAsync(settings.CrossfadeSeconds, ct).ConfigureAwait(false);
        await engine.SetVolumeAsync(settings.Volume, ct).ConfigureAwait(false);
        await engine.SetMutedAsync(settings.Muted, ct).ConfigureAwait(false);
        await engine.SetLoopModeAsync(settings.LoopMode, ct).ConfigureAwait(false);

        lock (_gate)
        {
            _savedSettings = settings;
            _savedQueueRevision = engine.GetQueue().Revision;
            _attached = true;
        }

        _backend.StateChanged += OnStateChanged;
        _backend.QueueChanged += OnQueueChanged;
        _logger.LogInformation("Restored {Count} queue items (current index {Index}, position {Position:F1} s) and playback settings.",
            queue.Items.Count, queue.CurrentIndex, queue.PositionSeconds);
    }

    /// <summary>
    /// Saves immediately if anything changed since the last save (shutdown), cancelling a pending debounce or retry.
    /// Throws if the save fails; the state then stays dirty and, unless disposed, the retry timer is re-armed.
    /// </summary>
    public async Task FlushAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
        }

        try
        {
            await SaveAsync(ct).ConfigureAwait(false);
            OnSaveSucceeded();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            OnSaveFailed(ex);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _timer?.Dispose();
            _timer = null;
        }

        _backend.StateChanged -= OnStateChanged;
        _backend.QueueChanged -= OnQueueChanged;
        await _saveGate.WaitAsync().ConfigureAwait(false);
        _saveGate.Release();
    }

    /// <summary>The position inside the current queue item while it is loaded (playing or paused), else 0.</summary>
    private static double PositionOf(PlaybackStateDto state, QueueDto queue)
    {
        var current = queue.CurrentIndex >= 0 && queue.CurrentIndex < queue.Items.Count ? queue.Items[queue.CurrentIndex].QueueItemId : (Guid?)null;
        var loaded = state.Status is PlaybackStatus.Playing or PlaybackStatus.Paused or PlaybackStatus.Crossfading;
        return loaded && current is not null && state.CurrentQueueItemId == current && double.IsFinite(state.PositionSeconds)
            ? Math.Max(0, state.PositionSeconds)
            : 0;
    }

    private void OnStateChanged(PlaybackStateDto state)
    {
        var settings = new PlaybackSettings(state.Volume, state.Muted, state.LoopMode, state.CrossfadeSeconds);
        lock (_gate)
        {
            if (settings == _savedSettings)
            {
                return;
            }
        }

        Schedule();
    }

    private void OnQueueChanged(QueueDto queue) => Schedule();

    private void Schedule()
    {
        lock (_gate)
        {
            if (_disposed || !_attached)
            {
                return;
            }

            _dirty = true;
            // While a retry is pending its (longer) timer stays: changes during an outage do not hammer the database.
            _timer ??= _clock.CreateTimer(_ => _ = SaveFromTimerAsync(), null, _debounce, Timeout.InfiniteTimeSpan);
        }
    }

    private async Task SaveFromTimerAsync()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
        }

        try
        {
            await SaveAsync(CancellationToken.None).ConfigureAwait(false);
            OnSaveSucceeded();
        }
        catch (Exception ex)
        {
            OnSaveFailed(ex);
        }
    }

    private void OnSaveSucceeded()
    {
        int failures;
        lock (_gate)
        {
            failures = _consecutiveFailures;
            _consecutiveFailures = 0;
        }

        if (failures > 0)
        {
            _logger.LogInformation("Saving the playback state works again (after {Failures} failed attempts).", failures);
        }
    }

    /// <summary>Keeps the state dirty and re-arms the timer with backoff; logs an error once per outage.</summary>
    private void OnSaveFailed(Exception ex)
    {
        int failures;
        TimeSpan delay;
        lock (_gate)
        {
            _dirty = true;
            failures = ++_consecutiveFailures;
            delay = RetryDelay(failures);
            if (!_disposed && _attached)
            {
                _timer?.Dispose();
                _timer = _clock.CreateTimer(_ => _ = SaveFromTimerAsync(), null, delay, Timeout.InfiniteTimeSpan);
            }
        }

        if (failures == 1)
        {
            _logger.LogError(ex, "Saving the playback state failed; retrying in the background (every {MaxDelay} s at most) until it succeeds.",
                RetryMaxDelay.TotalSeconds);
        }
        else
        {
            _logger.LogDebug(ex, "Saving the playback state failed again (attempt {Failures}); next retry in {Delay} s.", failures, delay.TotalSeconds);
        }
    }

    private async Task SaveAsync(CancellationToken ct)
    {
        await _saveGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var state = _backend.GetState();
            var queue = _backend.GetQueue();
            var settings = new PlaybackSettings(state.Volume, state.Muted, state.LoopMode, state.CrossfadeSeconds);
            bool saveSettings, saveQueue;
            lock (_gate)
            {
                if (!_attached)
                {
                    return;
                }

                saveSettings = settings != _savedSettings;
                saveQueue = queue.Revision != _savedQueueRevision;
                // Cleared before writing: a change during the write sets it again (and schedules its own save); a
                // failure sets it again in OnSaveFailed.
                _dirty = false;
            }

            if (saveQueue)
            {
                var index = queue.CurrentIndex >= 0 && queue.CurrentIndex < queue.Items.Count ? queue.CurrentIndex : -1;
                await _repository.SaveQueueAsync(queue.Items, index, ct).ConfigureAwait(false);
            }

            if (saveSettings)
            {
                await _repository.SaveSettingsAsync(settings, ct).ConfigureAwait(false);
            }

            await _repository.SavePositionAsync(PositionOf(state, queue), ct).ConfigureAwait(false);

            lock (_gate)
            {
                if (saveQueue)
                {
                    _savedQueueRevision = queue.Revision;
                }

                if (saveSettings)
                {
                    _savedSettings = settings;
                }
            }

            if (saveQueue || saveSettings)
            {
                SaveCount++;
            }
        }
        finally
        {
            _saveGate.Release();
        }
    }
}
