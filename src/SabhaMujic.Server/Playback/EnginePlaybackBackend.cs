using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using SabhaMujic.Audio.Engine;
using SabhaMujic.Contracts;
using SabhaMujic.Server.Application;

namespace SabhaMujic.Server.Playback;

/// <summary>
/// <see cref="IPlaybackBackend"/> over the real <see cref="PlaybackEngine"/>.
/// <list type="bullet">
/// <item>Engine events arrive on the engine's control thread; they only set a flag here. A dispatcher task reads the
/// latest snapshot and raises <see cref="StateChanged"/>/<see cref="QueueChanged"/> off that thread, so subscribers can
/// never stall the engine (intermediate snapshots are coalesced).</item>
/// <item>Every state handed out gets a revision from one monotonic counter, assigned when the snapshot is read, so the
/// ~5 s heartbeat while playing (fresh position) is never dropped as stale by clients.</item>
/// <item>Track ids are resolved into the <see cref="StoreTrackResolver"/> cache before a command is posted, keeping
/// database reads off the engine thread.</item>
/// <item>Loop overrides and re-analysis results reach the live voice through <see cref="PlaybackEngine.RefreshTrackAsync"/>:
/// a seam that has not started yet is rebuilt from the new region.</item>
/// </list>
/// </summary>
public sealed class EnginePlaybackBackend : IPlaybackBackend, IAsyncDisposable
{
    private readonly PlaybackEngine _engine;
    private readonly StoreTrackResolver _resolver;
    private readonly ILogger _logger;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _heartbeat;
    private readonly TimeSpan _releaseTimeout;
    private readonly Lock _revisionGate = new();
    private readonly Channel<bool> _wakeup = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _dispatcher;
    private readonly Task _heartbeatLoop;
    private long _revision;
    private int _stateDirty;
    private int _queueDirty;

    public EnginePlaybackBackend(PlaybackEngine engine, StoreTrackResolver resolver, ILogger<EnginePlaybackBackend> logger,
        TimeProvider? clock = null, TimeSpan? heartbeat = null, TimeSpan? fileReleaseTimeout = null)
    {
        _engine = engine;
        _resolver = resolver;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
        _heartbeat = heartbeat ?? TimeSpan.FromSeconds(5);
        _releaseTimeout = fileReleaseTimeout ?? TimeSpan.FromSeconds(5);
        _engine.StateChanged += OnEngineState;
        _engine.QueueChanged += OnEngineQueue;
        _engine.PlaybackError += OnEngineError;
        _dispatcher = Task.Run(DispatchAsync);
        _heartbeatLoop = Task.Run(HeartbeatAsync);
    }

    public event Action<PlaybackStateDto>? StateChanged;

    public event Action<QueueDto>? QueueChanged;

    public PlaybackEngine Engine => _engine;

    public PlaybackStateDto GetState()
    {
        lock (_revisionGate)
        {
            return _engine.GetState() with { Revision = ++_revision };
        }
    }

    public QueueDto GetQueue() => _engine.GetQueue();

    public async Task PlayTrackAsync(Guid trackId, CancellationToken ct)
    {
        await _resolver.WarmAsync([trackId], ct).ConfigureAwait(false);
        await _engine.PlayTrackAsync(trackId, ct).ConfigureAwait(false);
    }

    public async Task PlayQueueItemAsync(Guid queueItemId, CancellationToken ct)
    {
        await WarmQueueAsync(ct).ConfigureAwait(false);
        await _engine.PlayQueueItemAsync(queueItemId, ct).ConfigureAwait(false);
    }

    public Task PauseAsync(CancellationToken ct) => _engine.PauseAsync(ct);

    public async Task ResumeAsync(CancellationToken ct)
    {
        await WarmQueueAsync(ct).ConfigureAwait(false);
        await _engine.ResumeAsync(ct).ConfigureAwait(false);
    }

    public Task StopAsync(CancellationToken ct) => _engine.StopAsync(ct);

    public async Task NextAsync(CancellationToken ct)
    {
        await WarmQueueAsync(ct).ConfigureAwait(false);
        await _engine.NextAsync(ct).ConfigureAwait(false);
    }

    public async Task PreviousAsync(CancellationToken ct)
    {
        await WarmQueueAsync(ct).ConfigureAwait(false);
        await _engine.PreviousAsync(ct).ConfigureAwait(false);
    }

    public Task SeekAsync(double positionSeconds, CancellationToken ct) => _engine.SeekAsync(positionSeconds, ct);

    public Task SetVolumeAsync(float volume, CancellationToken ct) => _engine.SetVolumeAsync(volume, ct);

    public Task SetMutedAsync(bool muted, CancellationToken ct) => _engine.SetMutedAsync(muted, ct);

    public Task SetLoopModeAsync(LoopMode mode, CancellationToken ct) => _engine.SetLoopModeAsync(mode, ct);

    public Task SetCrossfadeAsync(double seconds, CancellationToken ct) => _engine.SetCrossfadeAsync(seconds, ct);

    public async Task<QueueDto> EnqueueAsync(IReadOnlyList<Guid> trackIds, bool playNext, CancellationToken ct)
    {
        await _resolver.WarmAsync(trackIds, ct).ConfigureAwait(false);
        return await _engine.EnqueueAsync(trackIds, playNext, ct).ConfigureAwait(false);
    }

    /// <summary>Replaces the queue; whatever is playing keeps playing and the new queue follows it.</summary>
    public async Task<QueueDto> ReplaceQueueAsync(IReadOnlyList<Guid> trackIds, CancellationToken ct)
    {
        await _resolver.WarmAsync(trackIds, ct).ConfigureAwait(false);
        return await _engine.ReplaceQueueAsync(trackIds, startPlayback: false, ct).ConfigureAwait(false);
    }

    public Task<QueueDto> RemoveFromQueueAsync(Guid queueItemId, CancellationToken ct) => _engine.RemoveFromQueueAsync(queueItemId, ct);

    public Task<QueueDto> MoveQueueItemAsync(Guid queueItemId, int newIndex, CancellationToken ct) => _engine.MoveQueueItemAsync(queueItemId, newIndex, ct);

    public Task<QueueDto> ClearQueueAsync(CancellationToken ct) => _engine.ClearQueueAsync(ct);

    /// <summary>
    /// <see cref="LibraryChangeKind.TrackRemoved"/>: removes every queue item of the track. If it is playing, the engine
    /// cuts (no crossfade) to the next item so the file is released promptly; this method returns once the file can be
    /// opened exclusively again (or after a timeout), so the caller can delete it.
    /// Other kinds (metadata, loop override, analysis) reload the track and hand the new analysis to the engine, which applies
    /// it to the live voice at the next seam that has not started yet.
    /// </summary>
    public async Task OnTrackChangedAsync(Guid trackId, LibraryChangeKind kind, CancellationToken ct)
    {
        if (kind != LibraryChangeKind.TrackRemoved)
        {
            _resolver.Invalidate(trackId);
            await _resolver.WarmAsync([trackId], ct).ConfigureAwait(false);
            await _engine.RefreshTrackAsync(trackId, ct).ConfigureAwait(false);
            return;
        }

        var path = _resolver.TryGetCachedPath(trackId);
        _resolver.Forget(trackId);
        var items = _engine.GetQueue().Items.Where(i => i.TrackId == trackId).Select(i => i.QueueItemId).ToList();
        if (items.Count > 0)
        {
            await WarmQueueAsync(CancellationToken.None, except: trackId).ConfigureAwait(false);
            var state = _engine.GetState();
            var playingIt = state.CurrentTrackId == trackId && state.Status is PlaybackStatus.Playing or PlaybackStatus.Crossfading
                or PlaybackStatus.Paused or PlaybackStatus.Loading;
            var crossfade = state.CrossfadeSeconds;
            if (playingIt)
            {
                await _engine.SetCrossfadeAsync(0, CancellationToken.None).ConfigureAwait(false);
            }

            try
            {
                foreach (var item in items)
                {
                    try
                    {
                        await _engine.RemoveFromQueueAsync(item, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (SabhaMujicException ex) when (ex.Code == ErrorCode.NotFound)
                    {
                    }
                }
            }
            finally
            {
                if (playingIt)
                {
                    await _engine.SetCrossfadeAsync(crossfade, CancellationToken.None).ConfigureAwait(false);
                }
            }
        }

        if (path is not null)
        {
            await WaitForReleaseAsync(path).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _engine.StateChanged -= OnEngineState;
        _engine.QueueChanged -= OnEngineQueue;
        _engine.PlaybackError -= OnEngineError;
        await _stop.CancelAsync().ConfigureAwait(false);
        _wakeup.Writer.TryComplete();
        try
        {
            await Task.WhenAll(_dispatcher, _heartbeatLoop).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _stop.Dispose();
    }

    private async Task WarmQueueAsync(CancellationToken ct, Guid? except = null)
    {
        try
        {
            await _resolver.WarmAsync(_engine.GetQueue().Items.Select(i => i.TrackId).Where(id => id != except), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Warming is an optimization; the engine resolves misses itself.
            _logger.LogDebug(ex, "Could not warm the track cache.");
        }
    }

    /// <summary>Waits until the engine (decoder voices) has closed the file, detected by opening it exclusively.</summary>
    private async Task WaitForReleaseAsync(string path)
    {
        var deadline = _clock.GetUtcNow() + _releaseTimeout;
        while (true)
        {
            try
            {
                await _engine.SyncAsync(CancellationToken.None).ConfigureAwait(false);
                using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                }

                return;
            }
            catch (FileNotFoundException)
            {
                return;
            }
            catch (DirectoryNotFoundException)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (_clock.GetUtcNow() >= deadline)
                {
                    _logger.LogWarning("A removed track's file is still in use after {Timeout}; deleting it may fail.", _releaseTimeout);
                    return;
                }

                await Task.Delay(25).ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                return;
            }
        }
    }

    private void OnEngineState(object? sender, PlaybackStateDto state)
    {
        Volatile.Write(ref _stateDirty, 1);
        _wakeup.Writer.TryWrite(true);
    }

    private void OnEngineQueue(object? sender, QueueDto queue)
    {
        Volatile.Write(ref _queueDirty, 1);
        _wakeup.Writer.TryWrite(true);
    }

    private void OnEngineError(object? sender, PlaybackErrorEventArgs e) =>
        // The exception can contain file paths: logs only. Clients see e.Message via the state's ErrorMessage.
        _logger.LogWarning(e.Exception, "Playback problem for track {TrackId}: {Message}", e.TrackId, e.Message);

    private async Task DispatchAsync()
    {
        var ct = _stop.Token;
        try
        {
            while (await _wakeup.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                _wakeup.Reader.TryRead(out _);
                if (Interlocked.Exchange(ref _queueDirty, 0) == 1)
                {
                    Raise(QueueChanged, _engine.GetQueue());
                }

                if (Interlocked.Exchange(ref _stateDirty, 0) == 1)
                {
                    Raise(StateChanged, GetState());
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    private async Task HeartbeatAsync()
    {
        var ct = _stop.Token;
        using var timer = new PeriodicTimer(_heartbeat, _clock);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                if (_engine.GetState().Status is PlaybackStatus.Playing or PlaybackStatus.Crossfading)
                {
                    Volatile.Write(ref _stateDirty, 1);
                    _wakeup.Writer.TryWrite(true);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    private void Raise<T>(Action<T>? handler, T value)
    {
        if (handler is null)
        {
            return;
        }

        foreach (var subscriber in handler.GetInvocationList().Cast<Action<T>>())
        {
            try
            {
                subscriber(value);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "A playback event subscriber failed.");
            }
        }
    }
}
