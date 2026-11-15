using SabhaMujic.Contracts;
using SabhaMujic.Core.Playback;
using SabhaMujic.Server.Application;

namespace SabhaMujic.RemoteE2EHost;

/// <summary>
/// A playback engine stand-in for the e2e host: a real <see cref="PlaybackQueue"/>, a simulated position clock, track
/// ends (advance / Smart Loop wrap / repeat queue), a Crossfading phase near the end and the ~5 s heartbeat. No audio.
/// </summary>
public sealed class ScriptedPlaybackBackend : IPlaybackBackend, IDisposable
{
    private readonly Lock _gate = new();
    private readonly PlaybackQueue _queue = new();
    private readonly Func<Guid, double> _duration;
    private readonly TimeProvider _clock;
    private readonly Timer _timer;
    private PlaybackStatus _status = PlaybackStatus.Idle;
    private double _anchorPosition;
    private DateTimeOffset _anchorTime;
    private float _volume = 0.8f;
    private bool _muted;
    private LoopMode _loopMode = LoopMode.Off;
    private double _crossfade = 6;
    private long _revision;
    private DateTimeOffset _lastHeartbeat;

    public ScriptedPlaybackBackend(Func<Guid, double> duration, TimeProvider? clock = null)
    {
        _duration = duration;
        _clock = clock ?? TimeProvider.System;
        _anchorTime = _clock.GetUtcNow();
        _timer = new Timer(_ => Tick(), null, TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(250));
    }

    public event Action<PlaybackStateDto>? StateChanged;

    public event Action<QueueDto>? QueueChanged;

    public PlaybackStatus Status
    {
        get
        {
            lock (_gate)
            {
                return _status;
            }
        }
    }

    public PlaybackStateDto GetState()
    {
        lock (_gate)
        {
            return Snapshot();
        }
    }

    public QueueDto GetQueue()
    {
        lock (_gate)
        {
            return _queue.Snapshot();
        }
    }

    public Task PlayTrackAsync(Guid trackId, CancellationToken ct) => Command(() =>
    {
        var added = _queue.Enqueue([trackId], playNext: true);
        _queue.JumpTo(added[0].QueueItemId);
        StartCurrent();
    }, queueChanged: true);

    public Task PlayQueueItemAsync(Guid queueItemId, CancellationToken ct) => Command(() =>
    {
        if (!_queue.JumpTo(queueItemId))
        {
            throw new SabhaMujicException(ErrorCode.NotFound, "No such queue item.");
        }

        StartCurrent();
    }, queueChanged: true);

    public Task PauseAsync(CancellationToken ct) => Command(() =>
    {
        if (_status is PlaybackStatus.Playing or PlaybackStatus.Crossfading)
        {
            Anchor(Position());
            _status = PlaybackStatus.Paused;
        }
    });

    public Task ResumeAsync(CancellationToken ct) => Command(() =>
    {
        if (_status == PlaybackStatus.Paused)
        {
            Anchor(_anchorPosition);
            _status = PlaybackStatus.Playing;
        }
        else if (_status is PlaybackStatus.Stopped or PlaybackStatus.Idle or PlaybackStatus.Error && _queue.Count > 0)
        {
            if (_queue.Current is null)
            {
                _queue.MoveNext(wrap: true);
            }

            StartCurrent();
        }
    }, queueChanged: true);

    public Task StopAsync(CancellationToken ct) => Command(() =>
    {
        _status = PlaybackStatus.Stopped;
        Anchor(0);
    });

    public Task NextAsync(CancellationToken ct) => Command(() =>
    {
        if (_queue.MoveNext(wrap: _loopMode == LoopMode.RepeatQueue) is null)
        {
            _status = PlaybackStatus.Stopped;
            Anchor(0);
            return;
        }

        StartCurrent();
    }, queueChanged: true);

    public Task PreviousAsync(CancellationToken ct) => Command(() =>
    {
        if (Position() <= 3)
        {
            _queue.MovePrevious();
        }

        StartCurrent();
    }, queueChanged: true);

    public Task SeekAsync(double positionSeconds, CancellationToken ct) => Command(() =>
        Anchor(Math.Clamp(positionSeconds, 0, CurrentDuration())));

    public Task SetVolumeAsync(float volume, CancellationToken ct) => Command(() => _volume = volume);

    public Task SetMutedAsync(bool muted, CancellationToken ct) => Command(() => _muted = muted);

    public Task SetLoopModeAsync(LoopMode mode, CancellationToken ct) => Command(() => _loopMode = mode);

    public Task SetCrossfadeAsync(double seconds, CancellationToken ct) => Command(() => _crossfade = seconds);

    public Task<QueueDto> EnqueueAsync(IReadOnlyList<Guid> trackIds, bool playNext, CancellationToken ct) =>
        QueueCommand(q => q.Enqueue(trackIds, playNext));

    public Task<QueueDto> ReplaceQueueAsync(IReadOnlyList<Guid> trackIds, CancellationToken ct) => QueueCommand(q =>
    {
        q.ReplaceAll(trackIds);
        _status = PlaybackStatus.Stopped;
        Anchor(0);
    });

    public Task<QueueDto> RemoveFromQueueAsync(Guid queueItemId, CancellationToken ct) => QueueCommand(q =>
    {
        var wasCurrent = q.Current?.QueueItemId == queueItemId;
        if (!q.Remove(queueItemId))
        {
            throw new SabhaMujicException(ErrorCode.NotFound, "No such queue item.");
        }

        if (wasCurrent)
        {
            _status = q.Current is null ? PlaybackStatus.Stopped : _status;
            Anchor(0);
        }
    });

    public Task<QueueDto> MoveQueueItemAsync(Guid queueItemId, int newIndex, CancellationToken ct) => QueueCommand(q =>
    {
        if (!q.Move(queueItemId, newIndex))
        {
            throw new SabhaMujicException(ErrorCode.NotFound, "No such queue item.");
        }
    });

    public Task<QueueDto> ClearQueueAsync(CancellationToken ct) => QueueCommand(q => q.ClearUpcomingAndHistory());

    public Task OnTrackChangedAsync(Guid trackId, LibraryChangeKind kind, CancellationToken ct)
    {
        if (kind != LibraryChangeKind.TrackRemoved)
        {
            return Task.CompletedTask;
        }

        return QueueCommand(q =>
        {
            foreach (var item in q.Snapshot().Items.Where(i => i.TrackId == trackId))
            {
                q.Remove(item.QueueItemId);
            }
        });
    }

    public void Dispose() => _timer.Dispose();

    private void Tick()
    {
        PlaybackStateDto? state = null;
        QueueDto? queue = null;
        lock (_gate)
        {
            if (_status is not (PlaybackStatus.Playing or PlaybackStatus.Crossfading))
            {
                return;
            }

            var now = _clock.GetUtcNow();
            var duration = CurrentDuration();
            var position = Position();
            var hasNext = _queue.PeekNext(_loopMode == LoopMode.RepeatQueue) is not null;
            if (position >= duration)
            {
                if (_loopMode == LoopMode.SmartLoop)
                {
                    Anchor(0); // Smart Loop wrap
                }
                else if (_queue.MoveNext(_loopMode == LoopMode.RepeatQueue) is not null)
                {
                    StartCurrent();
                    queue = _queue.Snapshot();
                }
                else
                {
                    _status = PlaybackStatus.Stopped;
                    Anchor(0);
                }

                state = Raise();
            }
            else
            {
                var crossfading = _loopMode != LoopMode.SmartLoop && hasNext && duration - position <= _crossfade && _crossfade > 0;
                var target = crossfading ? PlaybackStatus.Crossfading : PlaybackStatus.Playing;
                if (target != _status)
                {
                    _status = target;
                    state = Raise();
                }
                else if (now - _lastHeartbeat >= TimeSpan.FromSeconds(5))
                {
                    state = Raise();
                }
            }
        }

        if (queue is not null)
        {
            QueueChanged?.Invoke(queue);
        }

        if (state is not null)
        {
            StateChanged?.Invoke(state);
        }
    }

    private Task Command(Action apply, bool queueChanged = false)
    {
        PlaybackStateDto state;
        QueueDto? queue = null;
        lock (_gate)
        {
            var revision = _queue.Revision;
            apply();
            state = Raise();
            if (queueChanged && _queue.Revision != revision)
            {
                queue = _queue.Snapshot();
            }
        }

        if (queue is not null)
        {
            QueueChanged?.Invoke(queue);
        }

        StateChanged?.Invoke(state);
        return Task.CompletedTask;
    }

    private Task<QueueDto> QueueCommand(Action<PlaybackQueue> apply)
    {
        QueueDto queue;
        PlaybackStateDto state;
        lock (_gate)
        {
            apply(_queue);
            queue = _queue.Snapshot();
            state = Raise();
        }

        QueueChanged?.Invoke(queue);
        StateChanged?.Invoke(state);
        return Task.FromResult(queue);
    }

    private void StartCurrent()
    {
        if (_queue.Current is null)
        {
            _status = PlaybackStatus.Stopped;
            Anchor(0);
            return;
        }

        _status = PlaybackStatus.Playing;
        Anchor(0);
    }

    private void Anchor(double position)
    {
        _anchorPosition = position;
        _anchorTime = _clock.GetUtcNow();
    }

    private double Position() => _status is PlaybackStatus.Playing or PlaybackStatus.Crossfading
        ? Math.Min(_anchorPosition + (_clock.GetUtcNow() - _anchorTime).TotalSeconds, CurrentDuration())
        : _anchorPosition;

    private double CurrentDuration() => _queue.Current is { } current ? _duration(current.TrackId) : 0;

    private PlaybackStateDto Raise()
    {
        _revision++;
        _lastHeartbeat = _clock.GetUtcNow();
        return Snapshot();
    }

    private PlaybackStateDto Snapshot() => new(_status, _queue.Current?.TrackId, _queue.Current?.QueueItemId,
        _queue.PeekNext(_loopMode == LoopMode.RepeatQueue)?.TrackId, Position(), CurrentDuration(), _volume, _muted, _loopMode, _crossfade, null,
        _revision, _clock.GetUtcNow());
}
