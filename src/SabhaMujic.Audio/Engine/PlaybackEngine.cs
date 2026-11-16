using System.Collections.Concurrent;
using SabhaMujic.Audio.Analysis;
using SabhaMujic.Audio.Decoding;
using SabhaMujic.Audio.Mixing;
using SabhaMujic.Audio.Output;
using SabhaMujic.Contracts;
using SabhaMujic.Core.Playback;

namespace SabhaMujic.Audio.Engine;

/// <summary>
/// The authoritative player: queue, state machine, Smart Loop and crossfade scheduling over a
/// <see cref="CrossfadeMixer"/> rendered by an <see cref="IAudioOutput"/>.
/// <para>
/// Threading: every command is queued to one control thread and executed in order (the returned task completes when
/// it has been applied and the resulting state published). Files are resolved, opened and pre-buffered on that thread
/// and decoded ahead by a <see cref="VoiceFeeder"/> thread, so the output's render thread only mixes memory buffers.
/// <see cref="GetState"/> and <see cref="GetQueue"/> are lock-free snapshots callable from any thread.
/// Events are raised on the control thread; handlers must be quick and must not wait for engine commands.
/// </para>
/// No authorization happens here; the Server layer authorizes before calling.
/// The engine does not own (dispose) the output.
/// </summary>
public sealed class PlaybackEngine : IDisposable
{
    private const int MaxCachedAnalyses = 256;

    private readonly IAudioOutput _output;
    private readonly IAudioDecoderFactory _decoders;
    private readonly ITrackResolver _resolver;
    private readonly PlaybackEngineOptions _options;
    private readonly CrossfadeMixer _mixer = new();
    private readonly VoiceFeeder _feeder = new();
    private readonly BlockingCollection<EngineCommand> _commands = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _analysisGate = new(1, 1);
    private readonly Thread _controlThread;

    // Control-thread state.
    private readonly PlaybackStateMachine _machine = new();
    private readonly PlaybackQueue _queue = new();
    private readonly HashSet<PlaybackVoice> _liveVoices = [];
    private readonly Dictionary<Guid, TrackAnalysis> _analysisCache = [];
    private readonly HashSet<Guid> _analysisInFlight = [];
    private readonly HashSet<Guid> _analysisFailed = [];
    private readonly HashSet<Guid> _skipItems = [];
    private float _volume = 1f;
    private bool _muted;
    private LoopMode _loopMode = LoopMode.Off;
    private double _crossfadeSeconds;
    private string? _error;
    private string? _deviceError;
    private TrackSession? _current;
    private Planned? _planned;
    private PlaybackVoice? _planBlockedFor;
    private bool _crossfadeEndedWhilePaused;
    private int _consecutiveFailures;
    private long _stateRevision;
    private StateCore? _lastCore;
    private PlaybackVoice? _lastPublishedVoice;
    private long _lastQueueRevision = -1;
    private (Guid QueueItemId, long Frame)? _pendingResume;

    // Published snapshots (any thread).
    private volatile Snapshot _snapshot;
    private volatile QueueDto _queueSnapshot;
    private volatile bool _disposed;
    private long _prebufferTimeouts;

    public PlaybackEngine(IAudioOutput output, IAudioDecoderFactory decoders, ITrackResolver resolver, PlaybackEngineOptions? options = null)
    {
        _output = output ?? throw new ArgumentNullException(nameof(output));
        _decoders = decoders ?? throw new ArgumentNullException(nameof(decoders));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _options = options ?? new PlaybackEngineOptions();
        _crossfadeSeconds = Math.Clamp(_options.DefaultCrossfadeSeconds, 0, 12);

        var core = BuildCore();
        _lastCore = core;
        _snapshot = new Snapshot(core, null, 0);
        _queueSnapshot = _queue.Snapshot();
        _lastQueueRevision = _queue.Revision;

        _output.DeviceLost += OnDeviceLost;
        _output.DeviceRestored += OnDeviceRestored;
        _output.Init(_mixer);
        _output.Play();

        _controlThread = new Thread(ControlLoop) { IsBackground = true, Name = "SabhaMujic playback control" };
        _controlThread.Start();
    }

    /// <summary>Raised (on the control thread) whenever the published state changes; <see cref="PlaybackStateDto.Revision"/> increases each time.</summary>
    public event EventHandler<PlaybackStateDto>? StateChanged;

    /// <summary>Raised (on the control thread) whenever the queue changes; <see cref="QueueDto.Revision"/> increases each time.</summary>
    public event EventHandler<QueueDto>? QueueChanged;

    /// <summary>A track could not be played (it was skipped) or analysed.</summary>
    public event EventHandler<PlaybackErrorEventArgs>? PlaybackError;

    /// <summary>A background analysis finished; store it so the next resolve returns it.</summary>
    public event EventHandler<TrackAnalyzedEventArgs>? TrackAnalyzed;

    /// <summary>The mixer, for diagnostics and tests.</summary>
    public CrossfadeMixer Mixer => _mixer;

    /// <summary>Voices that started before their pre-buffer filled within the timeout (slow storage). Diagnostics.</summary>
    public long PrebufferTimeouts => Interlocked.Read(ref _prebufferTimeouts);

    // ---------------------------------------------------------------- queries

    public PlaybackStateDto GetState() => ToDto(_snapshot);

    public QueueDto GetQueue() => _queueSnapshot;

    public Task<PlaybackStateDto> GetStateAsync(CancellationToken ct = default) => Task.FromResult(GetState());

    public Task<QueueDto> GetQueueAsync(CancellationToken ct = default) => Task.FromResult(GetQueue());

    // ---------------------------------------------------------------- commands (mirror IPlaybackApi)

    /// <summary>
    /// Inserts the track after the current item and starts it (crossfading if something is playing). If the track
    /// cannot be opened while something is playing, the music carries on, the inserted item is removed again and the
    /// error is reported in the state.
    /// </summary>
    public Task PlayTrackAsync(Guid trackId, CancellationToken ct = default) => Post(() =>
    {
        _ = Resolve(trackId) ?? throw new SabhaMujicException(ErrorCode.NotFound, "Track not found.");
        var item = _queue.Enqueue([trackId], playNext: true)[0];
        if (StartItem(item, crossfade: true, userInitiated: true, onlyThisItem: true) == StartOutcome.KeptCurrent)
        {
            _queue.Remove(item.QueueItemId);
            OnQueueEdited();
        }
    }, ct);

    /// <summary>Starts a queue item. If it cannot be opened while something is playing, the music carries on (error in the state).</summary>
    public Task PlayQueueItemAsync(Guid queueItemId, CancellationToken ct = default) => Post(() =>
    {
        var item = FindItem(queueItemId) ?? throw new SabhaMujicException(ErrorCode.NotFound, "Queue item not found.");
        StartItem(item, crossfade: true, userInitiated: true, onlyThisItem: true);
    }, ct);

    public Task PauseAsync(CancellationToken ct = default) => Post(() =>
    {
        if (_machine.State is PlaybackStatus.Playing or PlaybackStatus.Crossfading)
        {
            _mixer.SetPaused(true);
            _machine.TryFire(PlaybackTrigger.Pause);
        }
    }, ct);

    /// <summary>Resumes from pause; when stopped, starts the current (or first) queue item.</summary>
    public Task ResumeAsync(CancellationToken ct = default) => Post(() =>
    {
        if (_machine.State == PlaybackStatus.Paused)
        {
            _mixer.SetPaused(false);
            _machine.TryFire(PlaybackTrigger.Resume);
            if (_crossfadeEndedWhilePaused && _machine.State == PlaybackStatus.Crossfading)
            {
                _machine.TryFire(PlaybackTrigger.CrossfadeComplete);
            }

            _crossfadeEndedWhilePaused = false;
            return;
        }

        if (_machine.State is PlaybackStatus.Idle or PlaybackStatus.Stopped or PlaybackStatus.Error)
        {
            var item = _queue.Current ?? _queue.Snapshot().Items.FirstOrDefault();
            if (item is not null)
            {
                long? resumeAt = _pendingResume is { } pending && pending.QueueItemId == item.QueueItemId ? pending.Frame : null;
                StartItem(item, crossfade: false, userInitiated: true, startFrame: resumeAt);
            }
        }
    }, ct);

    public Task StopAsync(CancellationToken ct = default) => Post(StopPlayback, ct);

    public Task NextAsync(CancellationToken ct = default) => Post(() =>
    {
        bool wrap = _loopMode == LoopMode.RepeatQueue;
        if (!IsActive)
        {
            var target = _queue.PeekNext(wrap);
            if (target is not null)
            {
                _queue.JumpTo(target.QueueItemId);
            }

            return;
        }

        var next = PeekAfter(_current?.QueueItemId, wrap);
        if (next is null)
        {
            StopPlayback();
            return;
        }

        StartItem(next, crossfade: true, userInitiated: true);
    }, ct);

    /// <summary>Restarts the current track if more than 3 s have played, otherwise goes to the previous item.</summary>
    public Task PreviousAsync(CancellationToken ct = default) => Post(() =>
    {
        var current = _current;
        if (IsActive && current is not null
            && current.Voice.Position - current.TrimStart > AudioFormat.SecondsToFrames(_options.PreviousRestartThresholdSeconds))
        {
            RestartCurrent(current.TrimStart);
            return;
        }

        var items = _queue.Snapshot().Items;
        int index = current is not null ? IndexOf(items, current.QueueItemId) : _queue.CurrentIndex;
        QueueItemDto? previous = index > 0
            ? items[index - 1]
            : index == 0 && _loopMode == LoopMode.RepeatQueue && items.Count > 1 ? items[^1] : null;

        if (!IsActive)
        {
            if (previous is not null)
            {
                _queue.JumpTo(previous.QueueItemId);
            }

            return;
        }

        if (previous is null)
        {
            if (current is not null)
            {
                RestartCurrent(current.TrimStart);
            }

            return;
        }

        StartItem(previous, crossfade: true, userInitiated: true);
    }, ct);

    public Task SeekAsync(double positionSeconds, CancellationToken ct = default) => Post(() =>
    {
        if (!double.IsFinite(positionSeconds) || positionSeconds < 0)
        {
            throw new SabhaMujicException(ErrorCode.InvalidArgument, "The position must be a non-negative number of seconds.");
        }

        if (!IsActive || _current is null)
        {
            return;
        }

        long frame = Math.Min(AudioFormat.SecondsToFrames(positionSeconds), Math.Max(0, _current.TrimEnd - 1));
        RestartCurrent(frame);
    }, ct);

    /// <summary>Volume in [0, 1]; applied as gain = volume² (a simple perceptual taper).</summary>
    public Task SetVolumeAsync(float volume, CancellationToken ct = default) => Post(() =>
    {
        if (!float.IsFinite(volume) || volume < 0 || volume > 1)
        {
            throw new SabhaMujicException(ErrorCode.InvalidArgument, "Volume must be between 0 and 1.");
        }

        _volume = volume;
        ApplyGain();
    }, ct);

    public Task SetMutedAsync(bool muted, CancellationToken ct = default) => Post(() =>
    {
        _muted = muted;
        ApplyGain();
    }, ct);

    public Task SetLoopModeAsync(LoopMode mode, CancellationToken ct = default) => Post(() =>
    {
        if (!Enum.IsDefined(mode))
        {
            throw new SabhaMujicException(ErrorCode.InvalidArgument, "Unknown loop mode.");
        }

        if (_loopMode != mode)
        {
            _loopMode = mode;
            InvalidatePlan();
        }
    }, ct);

    /// <summary>Crossfade between queue items in seconds, [0, 12].</summary>
    public Task SetCrossfadeAsync(double seconds, CancellationToken ct = default) => Post(() =>
    {
        if (!double.IsFinite(seconds) || seconds < 0 || seconds > 12)
        {
            throw new SabhaMujicException(ErrorCode.InvalidArgument, "The crossfade must be between 0 and 12 seconds.");
        }

        _crossfadeSeconds = seconds;
        if (_planned is { Kind: TransitionKind.QueueAdvance })
        {
            InvalidatePlan();
        }
    }, ct);

    public Task<QueueDto> EnqueueAsync(IReadOnlyList<Guid> trackIds, bool playNext, CancellationToken ct = default) => Post(() =>
    {
        ArgumentNullException.ThrowIfNull(trackIds);
        _queue.Enqueue(trackIds, playNext);
        OnQueueEdited();
        return _queue.Snapshot();
    }, ct);

    /// <summary>
    /// Replaces the whole queue. With <paramref name="startPlayback"/> the first item starts (crossfading if something
    /// is playing); otherwise the current track plays on and the new queue follows it.
    /// </summary>
    public Task<QueueDto> ReplaceQueueAsync(IReadOnlyList<Guid> trackIds, bool startPlayback, CancellationToken ct = default) => Post(() =>
    {
        ArgumentNullException.ThrowIfNull(trackIds);
        _queue.ReplaceAll(trackIds);
        OnQueueEdited();
        if (startPlayback && _queue.Count > 0)
        {
            StartItem(_queue.Snapshot().Items[0], crossfade: true, userInitiated: true);
        }

        return _queue.Snapshot();
    }, ct);

    /// <summary>Removes an item. Removing the playing item moves on to the next one (or stops).</summary>
    public Task<QueueDto> RemoveFromQueueAsync(Guid queueItemId, CancellationToken ct = default) => Post(() =>
    {
        bool removingCurrent = IsActive && _current?.QueueItemId == queueItemId;
        var next = removingCurrent ? PeekAfter(queueItemId, _loopMode == LoopMode.RepeatQueue) : null;
        if (!_queue.Remove(queueItemId))
        {
            throw new SabhaMujicException(ErrorCode.NotFound, "Queue item not found.");
        }

        OnQueueEdited();
        if (removingCurrent)
        {
            if (next is not null && next.QueueItemId != queueItemId)
            {
                StartItem(next, crossfade: true, userInitiated: true);
            }
            else
            {
                StopPlayback();
            }
        }

        return _queue.Snapshot();
    }, ct);

    /// <summary>
    /// Removes every queue item of a track (e.g. it was deleted from the library) in one step. If it is playing, playback
    /// moves on to the next item of another track with a hard cut (no crossfade out of a file that is going away), or
    /// stops. The crossfade setting is not touched.
    /// </summary>
    public Task<QueueDto> RemoveTrackAsync(Guid trackId, CancellationToken ct = default) => Post(() =>
    {
        var items = _queue.Snapshot().Items;
        var doomed = items.Where(i => i.TrackId == trackId).Select(i => i.QueueItemId).ToHashSet();
        if (doomed.Count == 0)
        {
            return _queue.Snapshot();
        }

        bool removingCurrent = IsActive && _current is not null && doomed.Contains(_current.QueueItemId);
        QueueItemDto? next = null;
        if (removingCurrent)
        {
            int index = IndexOf(items, _current!.QueueItemId);
            bool wrap = _loopMode == LoopMode.RepeatQueue;
            for (int step = 1; step < items.Count; step++)
            {
                int candidate = index + step;
                if (candidate >= items.Count)
                {
                    if (!wrap)
                    {
                        break;
                    }

                    candidate %= items.Count;
                }

                if (!doomed.Contains(items[candidate].QueueItemId))
                {
                    next = items[candidate];
                    break;
                }
            }
        }

        foreach (var id in doomed)
        {
            _queue.Remove(id);
        }

        OnQueueEdited();
        if (removingCurrent)
        {
            if (next is not null)
            {
                StartItem(next, crossfade: false, userInitiated: true);
            }
            else
            {
                StopPlayback();
            }
        }

        return _queue.Snapshot();
    }, ct);

    public Task<QueueDto> MoveQueueItemAsync(Guid queueItemId, int newIndex, CancellationToken ct = default) => Post(() =>
    {
        if (!_queue.Move(queueItemId, newIndex))
        {
            throw new SabhaMujicException(ErrorCode.NotFound, "Queue item not found.");
        }

        OnQueueEdited();
        return _queue.Snapshot();
    }, ct);

    /// <summary>Removes all items except the one currently playing.</summary>
    public Task<QueueDto> ClearQueueAsync(CancellationToken ct = default) => Post(() =>
    {
        _queue.ClearUpcomingAndHistory();
        OnQueueEdited();
        return _queue.Snapshot();
    }, ct);

    /// <summary>
    /// Re-resolves a track after its analysis or admin loop override changed and applies the result to the live session:
    /// a scheduled loop seam (or queue advance into another copy of the track) that has not started yet is rebuilt from the
    /// new analysis; one already crossfading finishes as planned and the following seam uses the new region. Trims of the
    /// voice that is already playing are not moved.
    /// </summary>
    public Task RefreshTrackAsync(Guid trackId, CancellationToken ct = default) => Post(() =>
    {
        _analysisCache.Remove(trackId);
        _analysisFailed.Remove(trackId);
        bool affected = _current?.TrackId == trackId || _planned?.Session.TrackId == trackId;
        if (!affected)
        {
            return;
        }

        var resolved = Resolve(trackId);
        if (resolved?.Analysis is { } analysis && _current is { } current && current.TrackId == trackId)
        {
            current.Analysis = analysis;
        }

        InvalidatePlan();
    }, ct);

    /// <summary>
    /// Restores a saved queue at startup: the same queue item ids, <paramref name="currentIndex"/> selected, playback
    /// stopped. With <paramref name="positionSeconds"/>, the next <see cref="ResumeAsync"/> of the current item continues
    /// there (with the usual short fade-in) instead of at the start. Conflict while something is playing.
    /// </summary>
    public Task<QueueDto> RestoreQueueAsync(IReadOnlyList<QueueItemDto> items, int currentIndex, double? positionSeconds = null,
        CancellationToken ct = default) => Post(() =>
    {
        ArgumentNullException.ThrowIfNull(items);
        if (IsActive)
        {
            throw new SabhaMujicException(ErrorCode.Conflict, "The queue can only be restored while playback is stopped.");
        }

        if (positionSeconds is { } p && (!double.IsFinite(p) || p < 0))
        {
            throw new SabhaMujicException(ErrorCode.InvalidArgument, "The position must be a non-negative number of seconds.");
        }

        _queue.Restore(items, currentIndex);
        _pendingResume = positionSeconds is > 0 && _queue.Current is { } current
            ? (current.QueueItemId, AudioFormat.SecondsToFrames(positionSeconds.Value))
            : null;
        OnQueueEdited();
        return _queue.Snapshot();
    }, ct);

    /// <summary>Completes once every earlier command and every pending render event has been processed.</summary>
    public Task SyncAsync(CancellationToken ct = default) => Post(DrainMixerEvents, ct);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _commands.CompleteAdding();
        _lifetime.Cancel();
        bool controlThreadStopped = true;
        if (Thread.CurrentThread != _controlThread)
        {
            controlThreadStopped = _controlThread.Join(TimeSpan.FromSeconds(10));
        }

        while (_commands.TryTake(out var pending))
        {
            pending.Cancel();
        }

        _output.DeviceLost -= OnDeviceLost;
        _output.DeviceRestored -= OnDeviceRestored;
        try
        {
            _output.Stop();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
        }

        // If the control thread is still running (blocked past the timeout), its voices and the non-thread-safe set are
        // left to it and the GC rather than disposed under its feet.
        if (controlThreadStopped)
        {
            foreach (var voice in _liveVoices)
            {
                voice.Dispose();
            }

            _liveVoices.Clear();
        }

        _feeder.Dispose();

        // _commands and _lifetime are deliberately not disposed: a background analysis may still try to post or
        // observe the (cancelled) token, and both are released by the GC.
    }

    // ---------------------------------------------------------------- control thread: plumbing

    private bool IsActive => _machine.State is PlaybackStatus.Playing or PlaybackStatus.Crossfading or PlaybackStatus.Paused or PlaybackStatus.Loading;

    private Task Post(Action action, CancellationToken ct) => Post<object?>(() =>
    {
        action();
        return null;
    }, ct);

    private Task<T> Post<T>(Func<T> action, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var command = new EngineCommand<T>(action, ct);
        try
        {
            _commands.Add(command);
        }
        catch (InvalidOperationException)
        {
            throw new ObjectDisposedException(nameof(PlaybackEngine));
        }

        return command.Task;
    }

    private void PostInternal(Action action)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            _commands.Add(new EngineCommand<object?>(() =>
            {
                action();
                return null;
            }, CancellationToken.None));
        }
        catch (InvalidOperationException)
        {
            // Shutting down.
        }
    }

    private void ControlLoop()
    {
        var token = _lifetime.Token;
        while (!token.IsCancellationRequested)
        {
            EngineCommand? command = null;
            try
            {
                _commands.TryTake(out command, (int)_options.ControlInterval.TotalMilliseconds, token);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (command is not null)
            {
                DrainMixerEvents();
                command.Execute();
            }

            try
            {
                Housekeeping();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                RaiseError(null, null, "Internal playback error.", ex);
            }

            command?.Complete();
        }
    }

    private void Housekeeping()
    {
        DrainMixerEvents();
        if (_current is { Healthy: false } current && !current.Voice.IsFaulted
            && current.Voice.Position - current.Voice.StartFrame >= AudioFormat.SampleRate)
        {
            current.Healthy = true;
            _consecutiveFailures = 0;
        }

        EnsurePlanned();
        Publish();
    }

    // ---------------------------------------------------------------- control thread: playback logic

    /// <summary>What <see cref="StartItem"/> did.</summary>
    private enum StartOutcome
    {
        /// <summary>A candidate opened and is playing.</summary>
        Started,

        /// <summary>Nothing could be opened, but the music that was playing carries on untouched (user request while audible).</summary>
        KeptCurrent,

        /// <summary>Nothing could be opened; playback stopped with an error (or the item was not in the queue).</summary>
        Failed,
    }

    /// <summary>
    /// Starts <paramref name="item"/>, skipping to the following items (wrapping with RepeatQueue) when it cannot be
    /// opened. A user request made while music is audible never interrupts it: the current voice keeps playing (the
    /// state is not switched to Loading) until a candidate has opened, and if none opens the music carries on with the
    /// error in the state. With <paramref name="onlyThisItem"/> (an explicitly chosen track) only that item is tried
    /// while music is audible. With nothing audible the behaviour is unchanged: skip, or stop with an error.
    /// </summary>
    private StartOutcome StartItem(QueueItemDto item, bool crossfade, bool userInitiated, long? startFrame = null, bool onlyThisItem = false)
    {
        _pendingResume = null; // a restored position applies only to the very next start
        if (userInitiated)
        {
            _error = null;
            _consecutiveFailures = 0;
        }

        bool audible = _machine.State is PlaybackStatus.Playing or PlaybackStatus.Crossfading && _mixer.CurrentVoice is not null;
        bool wrap = _loopMode == LoopMode.RepeatQueue;
        var items = _queue.Snapshot().Items;
        int start = IndexOf(items, item.QueueItemId);
        if (start < 0)
        {
            return StartOutcome.Failed;
        }

        // Something is playing (by the engine's own state: the mixer publishes its current voice only after the next
        // render block). Only when the playing item is still queued: if it was just removed (or the queue replaced), it
        // must not linger.
        bool keepCurrentOnFailure = userInitiated && _machine.State is PlaybackStatus.Playing or PlaybackStatus.Crossfading
            && _current is not null && IndexOf(items, _current.QueueItemId) >= 0;
        if (!keepCurrentOnFailure)
        {
            _machine.TryFire(PlaybackTrigger.Load);
            Publish();
        }

        int maxAttempts = keepCurrentOnFailure && onlyThisItem ? 1 : items.Count;
        int tried = 0;
        (Guid TrackId, string Reason)? lastFailure = null;
        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            int index = start + attempt;
            if (index >= items.Count)
            {
                if (!wrap)
                {
                    break;
                }

                index %= items.Count;
            }

            var candidate = items[index];
            if (keepCurrentOnFailure && candidate.QueueItemId == _current!.QueueItemId)
            {
                break; // wrapped round to what is playing: keep it
            }

            tried++;
            TrackSession session;
            try
            {
                session = attempt == 0 && startFrame is not null
                    ? OpenSession(candidate, startFrame)
                    : TakePlannedSession(candidate) ?? OpenSession(candidate, startFrame: null);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                lastFailure = (candidate.TrackId, ReportFailure(candidate, ex));
                _consecutiveFailures++;
                if (!keepCurrentOnFailure)
                {
                    _machine.TryFire(PlaybackTrigger.LoadFailed);
                }

                if (_consecutiveFailures >= items.Count)
                {
                    break;
                }

                if (!keepCurrentOnFailure)
                {
                    _machine.TryFire(PlaybackTrigger.Load);
                }

                continue;
            }

            _queue.JumpTo(candidate.QueueItemId);
            if (keepCurrentOnFailure)
            {
                _machine.TryFire(PlaybackTrigger.Load);
            }

            _machine.TryFire(PlaybackTrigger.Loaded);
            _crossfadeEndedWhilePaused = false;
            CancelPlan();
            int fade = crossfade && audible ? CrossfadeFrames(_current, session) : 0;
            var kind = fade > 0 ? TransitionKind.Crossfade : TransitionKind.Cut;
            if (fade == 0)
            {
                fade = AudioFormat.MillisecondsToFrames(_options.StartFadeMilliseconds); // de-click only
            }

            _mixer.SetPaused(false);
            _mixer.Play(session.Voice, fade, kind);
            _current = session;
            _skipItems.Clear();
            _planBlockedFor = null;
            return StartOutcome.Started;
        }

        if (keepCurrentOnFailure)
        {
            // The requested track(s) could not be opened: the music that was playing carries on, nothing else changes.
            _consecutiveFailures = 0;
            _error = lastFailure is { } failure
                ? $"Track {failure.TrackId} could not be played, so the current track keeps playing: {failure.Reason}"
                : null;
            return StartOutcome.KeptCurrent;
        }

        // Nothing could be opened.
        _mixer.Stop();
        CancelPlan();
        _current = null;
        if (_machine.State == PlaybackStatus.Loading)
        {
            _machine.TryFire(PlaybackTrigger.LoadFailed);
        }

        var summary = tried >= items.Count
            ? "None of the queued tracks could be played."
            : "No playable track was found from the selected one onwards.";
        _error = summary + (_error is null ? string.Empty : " Last error: " + _error);
        return StartOutcome.Failed;
    }

    private void StopPlayback()
    {
        _mixer.Stop();
        _mixer.SetPaused(false);
        CancelPlan();
        _current = null;
        _crossfadeEndedWhilePaused = false;
        _machine.TryFire(PlaybackTrigger.Stop); // no-op when already Idle/Stopped
    }

    /// <summary>Seek/restart within the current track with a short de-click fade (stays paused if paused).</summary>
    private void RestartCurrent(long frame)
    {
        var session = _current!;
        CancelPlan();
        PlaybackVoice voice;
        try
        {
            voice = CreateVoice(session, null, frame);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ReportFailure(session.Item, ex);
            return;
        }

        session.Voice = voice;
        _mixer.Play(voice, AudioFormat.MillisecondsToFrames(_options.SeekFadeMilliseconds), TransitionKind.Cut);
        if (_machine.State == PlaybackStatus.Crossfading)
        {
            _machine.TryFire(PlaybackTrigger.CrossfadeComplete);
        }
    }

    private void OnCurrentEnded(TrackSession session, Exception? fault)
    {
        _current = null;
        CancelPlan();
        if (fault is not null)
        {
            ReportFailure(session.Item, fault);
            _consecutiveFailures++;
            if (_consecutiveFailures >= Math.Max(1, _queue.Count))
            {
                EnterAllFailed();
                return;
            }
        }

        if (_loopMode == LoopMode.SmartLoop && fault is null)
        {
            // The seam was missed (e.g. Smart Loop was switched on after the loop end): restart at the loop start.
            var loop = EffectiveLoop(session);
            try
            {
                var voice = CreateVoice(session, null, loop?.StartFrame ?? session.TrimStart);
                session.Voice = voice;
                _current = session;
                _mixer.Play(voice, AudioFormat.MillisecondsToFrames(_options.StartFadeMilliseconds), TransitionKind.LoopRestart);
                return;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                ReportFailure(session.Item, ex);
            }
        }

        var next = PeekAfter(session.QueueItemId, _loopMode == LoopMode.RepeatQueue);
        if (next is null)
        {
            if (!_machine.TryFire(PlaybackTrigger.QueueExhausted))
            {
                _machine.TryFire(PlaybackTrigger.Stop);
            }

            return;
        }

        StartItem(next, crossfade: false, userInitiated: false);
    }

    private void EnterAllFailed()
    {
        _mixer.Stop();
        CancelPlan();
        _current = null;
        if (!_machine.TryFire(PlaybackTrigger.Fail))
        {
            _machine.TryFire(PlaybackTrigger.Stop);
        }

        _error = "None of the queued tracks could be played." + (_error is null ? string.Empty : " Last error: " + _error);
    }

    /// <summary>Schedules the next transition (queue advance, loop seam or loop restart) if none is scheduled.</summary>
    private void EnsurePlanned()
    {
        if (_planned is not null || _current is null || _machine.State is not (PlaybackStatus.Playing or PlaybackStatus.Crossfading or PlaybackStatus.Paused))
        {
            return;
        }

        var session = _current;
        var voice = session.Voice;
        if (voice.IsDisposed || voice.IsDrained || _planBlockedFor == voice)
        {
            return;
        }

        if (_loopMode == LoopMode.SmartLoop)
        {
            PlanLoop(session);
        }
        else
        {
            PlanAdvance(session);
        }

        if (_planned is null)
        {
            _planBlockedFor = voice; // nothing to schedule for this voice (end of queue or open failure)
        }
    }

    private void PlanAdvance(TrackSession session)
    {
        bool wrap = _loopMode == LoopMode.RepeatQueue;
        var items = _queue.Snapshot().Items;
        int index = IndexOf(items, session.QueueItemId);
        if (index < 0)
        {
            index = _queue.CurrentIndex;
        }

        for (int k = 1; k <= items.Count; k++)
        {
            int i = index + k;
            if (i >= items.Count)
            {
                if (!wrap)
                {
                    return;
                }

                i %= items.Count;
            }

            var candidate = items[i];
            if (_skipItems.Contains(candidate.QueueItemId))
            {
                continue;
            }

            try
            {
                var next = OpenSession(candidate, startFrame: null);
                int fade = CrossfadeFrames(session, next);
                long trigger = session.TrimEnd - fade;
                _mixer.Schedule(session.Voice, next.Voice, trigger, fade, TransitionKind.QueueAdvance);
                _planned = new Planned(next, next.Voice, TransitionKind.QueueAdvance, candidate.QueueItemId);
                return;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                ReportFailure(candidate, ex);
                _skipItems.Add(candidate.QueueItemId);
            }
        }
    }

    private void PlanLoop(TrackSession session)
    {
        var loop = EffectiveLoop(session);
        var voice = session.Voice;
        try
        {
            if (loop is not null && voice.Position <= loop.EndFrame - loop.CrossfadeFrames && voice.EndFrame >= loop.EndFrame)
            {
                var seam = CreateVoice(session, null, loop.StartFrame - loop.CrossfadeFrames);
                _mixer.Schedule(voice, seam, loop.EndFrame - loop.CrossfadeFrames, loop.CrossfadeFrames, TransitionKind.LoopSeam, loop.SeamCorrelation);
                _planned = new Planned(session, seam, TransitionKind.LoopSeam, session.QueueItemId);
            }
            else
            {
                // Past the seam (or no usable region): crossfade from the trimmed end back to the loop start.
                long restartAt = loop?.StartFrame ?? session.TrimStart;
                long fade = Math.Min(AudioFormat.SecondsToFrames(_options.Analysis.FallbackCrossfadeSeconds), (session.TrimEnd - session.TrimStart) / 4);
                var restart = CreateVoice(session, null, restartAt);
                _mixer.Schedule(voice, restart, session.TrimEnd - fade, (int)fade, TransitionKind.LoopRestart);
                _planned = new Planned(session, restart, TransitionKind.LoopRestart, session.QueueItemId);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ReportFailure(session.Item, ex);
        }
    }

    private LoopRegion? EffectiveLoop(TrackSession session)
    {
        var loop = session.Analysis?.Loop
                   ?? LoopAnalyzer.CreateFallbackLoop(session.TrimStart, session.TrimEnd, AudioFormat.SecondsToFrames(_options.Analysis.FallbackCrossfadeSeconds), 0);
        long start = Math.Clamp(loop.StartFrame, 0, session.TrimEnd);
        long end = Math.Clamp(loop.EndFrame, start, session.TrimEnd);
        if (end - start < AudioFormat.SampleRate / 2)
        {
            return null;
        }

        long fade = Math.Clamp(loop.CrossfadeFrames, 0, Math.Min(start, (end - start) / 2));
        return loop with { StartFrame = start, EndFrame = end, CrossfadeFrames = (int)fade };
    }

    private int CrossfadeFrames(TrackSession? from, TrackSession to)
    {
        long fade = AudioFormat.SecondsToFrames(_crossfadeSeconds);
        if (from is not null)
        {
            fade = Math.Min(fade, (from.TrimEnd - from.TrimStart) / 2);
        }

        fade = Math.Min(fade, (to.TrimEnd - to.TrimStart) / 2);
        return (int)Math.Max(0, fade);
    }

    /// <summary>Cancels the scheduled transition (the mixer releases its voice).</summary>
    private void CancelPlan()
    {
        if (_planned is not null)
        {
            _mixer.CancelScheduled(_planned.Voice);
            _planned = null;
        }
    }

    private void InvalidatePlan()
    {
        CancelPlan();
        _planBlockedFor = null;
        _skipItems.Clear();
    }

    private void OnQueueEdited()
    {
        if (_planned is { Kind: TransitionKind.QueueAdvance } planned)
        {
            var items = _queue.Snapshot().Items;
            var desired = _current is null ? null : NextCandidate(items, _current.QueueItemId);
            if (desired?.QueueItemId != planned.QueueItemId)
            {
                InvalidatePlan();
            }
        }
        else
        {
            _planBlockedFor = null;
            _skipItems.Clear();
        }

        if (_machine.State == PlaybackStatus.Stopped && _queue.Count == 0)
        {
            _machine.TryFire(PlaybackTrigger.Reset);
        }
    }

    private QueueItemDto? NextCandidate(IReadOnlyList<QueueItemDto> items, Guid currentItemId)
    {
        int index = IndexOf(items, currentItemId);
        if (index < 0)
        {
            index = _queue.CurrentIndex;
        }

        bool wrap = _loopMode == LoopMode.RepeatQueue;
        for (int k = 1; k <= items.Count; k++)
        {
            int i = index + k;
            if (i >= items.Count)
            {
                if (!wrap)
                {
                    return null;
                }

                i %= items.Count;
            }

            if (!_skipItems.Contains(items[i].QueueItemId))
            {
                return items[i];
            }
        }

        return null;
    }

    private TrackSession? TakePlannedSession(QueueItemDto item)
    {
        if (_planned is { Kind: TransitionKind.QueueAdvance } planned && planned.QueueItemId == item.QueueItemId
            && planned.Voice.Position == planned.Voice.StartFrame && !planned.Voice.IsFaulted)
        {
            _planned = null; // Play() with the same voice takes it over from the schedule
            return planned.Session;
        }

        return null;
    }

    private TrackSession OpenSession(QueueItemDto item, long? startFrame)
    {
        var resolved = Resolve(item.TrackId) ?? throw new SabhaMujicException(ErrorCode.NotFound, "Track not found.");
        var analysis = resolved.Analysis;
        if (analysis is null && _analysisCache.TryGetValue(item.TrackId, out var cached))
        {
            analysis = cached;
        }

        var source = _decoders.Open(resolved.Path);
        TrackSession session;
        try
        {
            session = new TrackSession(item, resolved, analysis, source.TotalFrames);
            long first = startFrame is { } requested
                ? Math.Clamp(requested, session.TrimStart, Math.Max(session.TrimStart, session.TrimEnd - AudioFormat.SampleRate / 10))
                : session.TrimStart;
            CreateVoice(session, source, first, assign: true);
        }
        catch
        {
            source.Dispose();
            throw;
        }

        if (session.Voice.IsFaulted)
        {
            var fault = session.Voice.Fault!;
            throw fault as AudioDecoderException ?? new AudioDecoderException("The audio could not be decoded.", fault);
        }

        EnsureAnalysis(session);
        return session;
    }

    private PlaybackVoice CreateVoice(TrackSession session, IAudioSource? source, long startFrame, bool assign = false)
    {
        source ??= _decoders.Open(session.Track.Path);
        PlaybackVoice voice;
        try
        {
            voice = new PlaybackVoice(source, startFrame, session.TrimEnd, _options.VoiceBufferFrames, session);
        }
        catch
        {
            source.Dispose();
            throw;
        }

        _liveVoices.Add(voice);
        if (assign)
        {
            session.Voice = voice;
        }

        _feeder.Register(voice);
        if (!voice.WaitForBuffered(_options.PrebufferFrames, _options.PrebufferTimeout))
        {
            // Slow storage (a spun-down disk): the voice starts with less than the pre-buffer and may underrun.
            Interlocked.Increment(ref _prebufferTimeouts);
        }
        if (voice.IsFaulted)
        {
            _liveVoices.Remove(voice);
            voice.Dispose();
            var fault = voice.Fault!;
            throw fault as AudioDecoderException ?? new AudioDecoderException("The audio could not be decoded.", fault);
        }

        return voice;
    }

    private ResolvedTrack? Resolve(Guid trackId) => _resolver.ResolveAsync(trackId, _lifetime.Token).GetAwaiter().GetResult();

    private void EnsureAnalysis(TrackSession session)
    {
        var trackId = session.TrackId;
        if (session.Analysis is not null || !_options.AnalyzeMissingTracks || _analysisFailed.Contains(trackId) || !_analysisInFlight.Add(trackId))
        {
            return;
        }

        string path = session.Track.Path;
        var token = _lifetime.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await _analysisGate.WaitAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                var analysis = LoopAnalyzer.Analyze(_decoders, path, _options.Analysis, token);
                PostInternal(() => OnAnalysisCompleted(trackId, analysis));
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                PostInternal(() => OnAnalysisFailed(trackId, ex));
            }
            finally
            {
                _analysisGate.Release();
            }
        }, CancellationToken.None);
    }

    private void OnAnalysisCompleted(Guid trackId, TrackAnalysis analysis)
    {
        _analysisInFlight.Remove(trackId);
        if (_analysisCache.Count >= MaxCachedAnalyses)
        {
            _analysisCache.Clear();
        }

        _analysisCache[trackId] = analysis;
        try
        {
            TrackAnalyzed?.Invoke(this, new TrackAnalyzedEventArgs(trackId, analysis));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            RaiseError(trackId, null, "A TrackAnalyzed handler failed.", ex);
        }

        bool replan = false;
        if (_current is { Analysis: null } current && current.TrackId == trackId)
        {
            current.Analysis = analysis;
            replan = true;
        }

        if (_planned is { } planned && planned.Session.TrackId == trackId && planned.Session.Analysis is null)
        {
            planned.Session.Analysis = analysis;
            replan = true;
        }

        if (replan)
        {
            InvalidatePlan();
        }
    }

    private void OnAnalysisFailed(Guid trackId, Exception ex)
    {
        _analysisInFlight.Remove(trackId);
        _analysisFailed.Add(trackId);
        RaiseError(trackId, null, "The track could not be analysed; it plays without trimming or a detected loop.", ex);
    }

    private void DrainMixerEvents()
    {
        while (_mixer.TryDequeueEvent(out var e))
        {
            HandleMixerEvent(e);
        }
    }

    private void HandleMixerEvent(MixerEvent e)
    {
        switch (e.Kind)
        {
            case MixerEventKind.VoiceReleased:
                if (_planned?.Voice == e.Voice)
                {
                    _planned = null;
                }

                if (_liveVoices.Remove(e.Voice))
                {
                    e.Voice.Dispose();
                }

                break;

            case MixerEventKind.TransitionMissed:
                if (_planned?.Voice == e.Voice)
                {
                    _planned = null;
                }

                break;

            case MixerEventKind.TransitionStarted:
                if (_planned?.Voice == e.Voice)
                {
                    _planned = null;
                }

                if (e.Voice.Tag is TrackSession session)
                {
                    if (_current != session)
                    {
                        _current = session;
                        _queue.JumpTo(session.QueueItemId);
                        _skipItems.Clear();
                    }

                    session.Voice = e.Voice;
                    _planBlockedFor = null;
                    if (e.Transition is TransitionKind.Crossfade or TransitionKind.QueueAdvance)
                    {
                        _machine.TryFire(PlaybackTrigger.BeginCrossfade);
                    }
                }

                break;

            case MixerEventKind.TransitionCompleted:
                if (e.Voice.Tag is TrackSession completed && completed == _current && completed.Voice == e.Voice)
                {
                    if (_machine.State == PlaybackStatus.Crossfading)
                    {
                        _machine.TryFire(PlaybackTrigger.CrossfadeComplete);
                    }
                    else if (_machine.State == PlaybackStatus.Paused)
                    {
                        _crossfadeEndedWhilePaused = true;
                    }
                }

                break;

            case MixerEventKind.VoiceEnded:
                if (e.Voice.Tag is TrackSession ended && ended == _current && ended.Voice == e.Voice)
                {
                    OnCurrentEnded(ended, e.Voice.IsFaulted ? e.Voice.Fault : null);
                }

                break;
        }
    }

    // ---------------------------------------------------------------- state publication

    private void ApplyGain() => _mixer.SetGain(_muted ? 0f : _volume * _volume);

    private StateCore BuildCore()
    {
        var current = _current;
        var status = _machine.State;
        bool hasTrack = current is not null && status is not (PlaybackStatus.Stopped or PlaybackStatus.Idle or PlaybackStatus.Error);
        QueueItemDto? next = null;
        if (_queue.Count > 0)
        {
            var items = _queue.Snapshot().Items;
            next = current is not null && hasTrack ? NextCandidate(items, current.QueueItemId) : _queue.PeekNext(_loopMode == LoopMode.RepeatQueue);
        }

        var queueCurrent = _queue.Current;
        string? error = _deviceError is null ? _error : _error is null ? _deviceError : _deviceError + " " + _error;
        return new StateCore(
            status,
            hasTrack ? current!.TrackId : queueCurrent?.TrackId,
            hasTrack ? current!.QueueItemId : queueCurrent?.QueueItemId,
            next?.TrackId,
            hasTrack ? AudioFormat.FramesToSeconds(current!.TotalFrames) : 0,
            _volume,
            _muted,
            _loopMode,
            _crossfadeSeconds,
            error);
    }

    private void Publish()
    {
        var core = BuildCore();
        var voice = core.Status is PlaybackStatus.Stopped or PlaybackStatus.Idle or PlaybackStatus.Error ? null : _current?.Voice;
        if (!core.Equals(_lastCore) || voice != _lastPublishedVoice)
        {
            _lastCore = core;
            _lastPublishedVoice = voice;
            _stateRevision++;
            var snapshot = new Snapshot(core, voice, _stateRevision);
            _snapshot = snapshot;
            var handler = StateChanged;
            if (handler is not null)
            {
                try
                {
                    handler(this, ToDto(snapshot));
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    RaiseError(null, null, "A StateChanged handler failed.", ex);
                }
            }
        }

        if (_queue.Revision != _lastQueueRevision)
        {
            _lastQueueRevision = _queue.Revision;
            var queue = _queue.Snapshot();
            _queueSnapshot = queue;
            var handler = QueueChanged;
            if (handler is not null)
            {
                try
                {
                    handler(this, queue);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    RaiseError(null, null, "A QueueChanged handler failed.", ex);
                }
            }
        }
    }

    private static PlaybackStateDto ToDto(Snapshot snapshot)
    {
        var core = snapshot.Core;
        double position = snapshot.Voice is { } voice ? AudioFormat.FramesToSeconds(voice.Position) : 0;
        return new PlaybackStateDto(
            core.Status,
            core.TrackId,
            core.QueueItemId,
            core.NextTrackId,
            position,
            core.DurationSeconds,
            core.Volume,
            core.Muted,
            core.LoopMode,
            core.CrossfadeSeconds,
            core.Error,
            snapshot.Revision,
            DateTimeOffset.UtcNow);
    }

    /// <summary>Records and raises a load failure; returns the client-safe reason.</summary>
    private string ReportFailure(QueueItemDto item, Exception ex)
    {
        string reason = ex switch
        {
            SabhaMujicException s => s.Message,
            AudioDecoderException d => d.Message,
            _ => "The audio file could not be opened.",
        };
        _error = $"Track {item.TrackId} could not be played and was skipped: {reason}";
        RaiseError(item.TrackId, item.QueueItemId, _error, ex);
        return reason;
    }

    private void RaiseError(Guid? trackId, Guid? queueItemId, string message, Exception? ex)
    {
        try
        {
            PlaybackError?.Invoke(this, new PlaybackErrorEventArgs(trackId, queueItemId, message, ex));
        }
        catch (Exception handlerError) when (handlerError is not OutOfMemoryException)
        {
            // A faulty handler must not break playback.
        }
    }

    private void OnDeviceLost(object? sender, AudioDeviceEventArgs e) => PostInternal(() => _deviceError = "Audio output device lost; reconnecting.");

    private void OnDeviceRestored(object? sender, AudioDeviceEventArgs e) => PostInternal(() => _deviceError = null);

    private QueueItemDto? FindItem(Guid queueItemId) => _queue.Snapshot().Items.FirstOrDefault(i => i.QueueItemId == queueItemId);

    private QueueItemDto? PeekAfter(Guid? queueItemId, bool wrap)
    {
        var items = _queue.Snapshot().Items;
        int index = queueItemId is { } id ? IndexOf(items, id) : -1;
        if (index < 0)
        {
            index = _queue.CurrentIndex;
        }

        int next = index + 1;
        if (next < items.Count)
        {
            return items[next];
        }

        return wrap && items.Count > 0 ? items[0] : null;
    }

    private static int IndexOf(IReadOnlyList<QueueItemDto> items, Guid queueItemId)
    {
        for (int i = 0; i < items.Count; i++)
        {
            if (items[i].QueueItemId == queueItemId)
            {
                return i;
            }
        }

        return -1;
    }

    // ---------------------------------------------------------------- nested types

    private sealed class TrackSession(QueueItemDto item, ResolvedTrack track, TrackAnalysis? analysis, long totalFrames)
    {
        public QueueItemDto Item { get; } = item;

        public Guid QueueItemId => Item.QueueItemId;

        public Guid TrackId => Item.TrackId;

        public ResolvedTrack Track { get; } = track;

        public TrackAnalysis? Analysis { get; set; } = analysis;

        public long TotalFrames { get; } = totalFrames;

        public PlaybackVoice Voice { get; set; } = null!;

        public bool Healthy { get; set; }

        public long TrimStart => Analysis is { } a ? Math.Clamp(a.LeadingTrimFrame, 0, TotalFrames) : 0;

        public long TrimEnd => Analysis is { } a ? Math.Clamp(a.TrailingTrimFrame, TrimStart, TotalFrames) : TotalFrames;
    }

    private sealed record Planned(TrackSession Session, PlaybackVoice Voice, TransitionKind Kind, Guid QueueItemId);

    private sealed record StateCore(
        PlaybackStatus Status,
        Guid? TrackId,
        Guid? QueueItemId,
        Guid? NextTrackId,
        double DurationSeconds,
        float Volume,
        bool Muted,
        LoopMode LoopMode,
        double CrossfadeSeconds,
        string? Error);

    private sealed record Snapshot(StateCore Core, PlaybackVoice? Voice, long Revision);

    private abstract class EngineCommand
    {
        public abstract void Execute();

        public abstract void Complete();

        public abstract void Cancel();
    }

    private sealed class EngineCommand<T>(Func<T> action, CancellationToken ct) : EngineCommand
    {
        private readonly TaskCompletionSource<T> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Exception? _exception;
        private T? _result;
        private bool _cancelled;

        public Task<T> Task => _tcs.Task;

        public override void Execute()
        {
            if (ct.IsCancellationRequested)
            {
                _cancelled = true;
                return;
            }

            try
            {
                _result = action();
            }
            catch (Exception ex)
            {
                _exception = ex;
            }
        }

        public override void Complete()
        {
            if (_cancelled)
            {
                _tcs.TrySetCanceled(ct);
            }
            else if (_exception is not null)
            {
                _tcs.TrySetException(_exception);
            }
            else
            {
                _tcs.TrySetResult(_result!);
            }
        }

        public override void Cancel() => _tcs.TrySetCanceled();
    }
}
