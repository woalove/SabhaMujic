using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SabhaMujic.Client;
using SabhaMujic.Contracts;
using SabhaMujic.Desktop.ViewModels.Infrastructure;

namespace SabhaMujic.Desktop.ViewModels;

/// <summary>
/// Now-playing bar and mini-player. The service is the source of truth; this view model
/// <list type="bullet">
/// <item>drops events whose <see cref="PlaybackStateDto.Revision"/> is not newer than the last applied one,</item>
/// <item>interpolates the position locally between server updates from <see cref="PlaybackStateDto.Timestamp"/>,</item>
/// <item>never moves the seek slider while the user drags it, nor until the resulting seek has been acknowledged,</item>
/// <item>debounces volume/crossfade slider changes and ignores server echoes of those values while a local change is pending.</item>
/// </list>
/// </summary>
public sealed partial class NowPlayingViewModel : ViewModelBase
{
    public static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(250);
    public static readonly TimeSpan SliderSendDelay = TimeSpan.FromMilliseconds(150);
    public const double MaxCrossfadeSeconds = 12;

    private readonly ISabhaClient _client;
    private readonly UiServices _ui;
    private readonly TrackInfoCache _tracks;
    private readonly Debouncer _volumeSender;
    private readonly Debouncer _crossfadeSender;

    private long? _lastRevision;
    private double _anchorPosition;
    private DateTimeOffset _anchorTime;
    private bool _seekPending;
    private bool _applyingServerValues;
    private bool _volumeDirty;
    private bool _crossfadeDirty;
    private IDisposable? _ticker;

    public NowPlayingViewModel(ISabhaClient client, UiServices ui, TrackInfoCache tracks)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
        _tracks = tracks ?? throw new ArgumentNullException(nameof(tracks));
        _volumeSender = new Debouncer(ui.Scheduler, SliderSendDelay);
        _crossfadeSender = new Debouncer(ui.Scheduler, SliderSendDelay);

        _client.Events.PlaybackStateReceived += state => _ui.Dispatcher.Post(() => ApplyState(state));
        _tracks.Invalidated += id =>
        {
            if (id == CurrentTrackId || id == NextTrackId)
            {
                Run(UpdateTrackInfoAsync);
            }
        };
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPlaying), nameof(IsCrossfading), nameof(StatusText), nameof(PlayPauseText))]
    public partial PlaybackStatus Status { get; private set; }

    public bool IsPlaying => Status is PlaybackStatus.Playing or PlaybackStatus.Crossfading;

    public bool IsCrossfading => Status == PlaybackStatus.Crossfading;

    public string PlayPauseText => IsPlaying ? "Pause" : "Play";

    public string StatusText => Status switch
    {
        PlaybackStatus.Idle => "Idle",
        PlaybackStatus.Loading => "Loading…",
        PlaybackStatus.Playing => "Playing",
        PlaybackStatus.Paused => "Paused",
        PlaybackStatus.Crossfading => "Crossfading",
        PlaybackStatus.Stopped => "Stopped",
        PlaybackStatus.Error => "Error",
        _ => Status.ToString(),
    };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTrack))]
    public partial Guid? CurrentTrackId { get; private set; }

    public bool HasTrack => CurrentTrackId is not null;

    [ObservableProperty]
    public partial Guid? NextTrackId { get; private set; }

    [ObservableProperty]
    public partial Guid? CurrentQueueItemId { get; private set; }

    [ObservableProperty]
    public partial string Title { get; private set; } = "Nothing playing";

    [ObservableProperty]
    public partial string? Artist { get; private set; }

    [ObservableProperty]
    public partial string? NextTitle { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DurationText), nameof(RemainingText))]
    public partial double Duration { get; private set; }

    /// <summary>Best estimate of the service's playback position (interpolated).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PositionText), nameof(RemainingText))]
    public partial double Position { get; private set; }

    /// <summary>Bound two-way to the seek slider. Only server-driven while the user is not seeking.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PositionText), nameof(RemainingText))]
    public partial double SliderPosition { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PositionText), nameof(RemainingText))]
    public partial bool IsSeeking { get; private set; }

    public string PositionText => Formatting.Time(IsSeeking ? SliderPosition : Position);

    public string DurationText => Formatting.Time(Duration);

    public string RemainingText => "-" + Formatting.Time(Math.Max(0, Duration - (IsSeeking ? SliderPosition : Position)));

    /// <summary>Volume in [0, 1].</summary>
    [ObservableProperty]
    public partial double Volume { get; set; } = 1;

    [ObservableProperty]
    public partial bool Muted { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LoopModeText))]
    public partial LoopMode LoopMode { get; private set; }

    public string LoopModeText => LoopMode switch
    {
        LoopMode.Off => "Loop: Off",
        LoopMode.SmartLoop => "Smart Loop",
        LoopMode.RepeatQueue => "Repeat queue",
        _ => LoopMode.ToString(),
    };

    /// <summary>Crossfade between queue items, [0, 12] seconds.</summary>
    [ObservableProperty]
    public partial double CrossfadeSeconds { get; set; }

    /// <summary>Error reported by the service's playback engine (e.g. a file failed to decode).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPlaybackError))]
    public partial string? PlaybackError { get; private set; }

    public bool HasPlaybackError => !string.IsNullOrEmpty(PlaybackError);

    /// <summary>Revision of the last applied state (null before the first one).</summary>
    public long? LastRevision => _lastRevision;

    /// <summary>
    /// Applies a server state unless it is stale (revision not newer than the last applied one).
    /// <paramref name="force"/> accepts any revision; used after (re)connecting, because a restarted
    /// service starts counting again.
    /// </summary>
    public void ApplyState(PlaybackStateDto state, bool force = false)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!force && _lastRevision is { } last && state.Revision <= last)
        {
            return;
        }

        _lastRevision = state.Revision;
        var trackChanged = CurrentTrackId != state.CurrentTrackId || NextTrackId != state.NextTrackId;

        _applyingServerValues = true;
        try
        {
            Status = state.Status;
            CurrentTrackId = state.CurrentTrackId;
            CurrentQueueItemId = state.CurrentQueueItemId;
            NextTrackId = state.NextTrackId;
            Duration = Math.Max(0, state.DurationSeconds);
            Muted = state.Muted;
            LoopMode = state.LoopMode;
            PlaybackError = state.ErrorMessage;
            if (!_volumeDirty)
            {
                Volume = Math.Clamp(state.Volume, 0f, 1f);
            }

            if (!_crossfadeDirty)
            {
                CrossfadeSeconds = Math.Clamp(state.CrossfadeSeconds, 0, MaxCrossfadeSeconds);
            }

            if (!_seekPending)
            {
                _anchorPosition = Math.Max(0, state.PositionSeconds);
                _anchorTime = state.Timestamp;
                UpdatePosition();
            }
        }
        finally
        {
            _applyingServerValues = false;
        }

        UpdateTicker();
        if (trackChanged || force)
        {
            Run(UpdateTrackInfoAsync);
        }
    }

    /// <summary>Fetches the full state and applies it regardless of revision.</summary>
    public Task<bool> RefreshAsync() => TryAsync(async () =>
    {
        var state = await _client.Playback.GetStateAsync().ConfigureAwait(true);
        ApplyState(state, force: true);
    });

    /// <summary>Clears everything (signed out).</summary>
    public void Reset()
    {
        _lastRevision = null;
        _ticker?.Dispose();
        _ticker = null;
        _volumeSender.Cancel();
        _crossfadeSender.Cancel();
        _volumeDirty = _crossfadeDirty = _seekPending = false;
        _applyingServerValues = true;
        try
        {
            Status = PlaybackStatus.Idle;
            CurrentTrackId = NextTrackId = CurrentQueueItemId = null;
            Title = "Nothing playing";
            Artist = NextTitle = PlaybackError = ErrorMessage = null;
            Duration = Position = SliderPosition = 0;
            IsSeeking = false;
        }
        finally
        {
            _applyingServerValues = false;
        }
    }

    /// <summary>Called by the view when the user presses the seek slider.</summary>
    public void BeginSeek() => IsSeeking = true;

    /// <summary>Called by the view when the user releases the seek slider: seeks to <see cref="SliderPosition"/>.</summary>
    public Task EndSeek()
    {
        if (!IsSeeking)
        {
            return Task.CompletedTask;
        }

        IsSeeking = false;
        return SeekToAsync(SliderPosition);
    }

    [RelayCommand]
    private Task SeekAsync(double seconds) => SeekToAsync(seconds);

    private async Task SeekToAsync(double seconds)
    {
        if (!HasTrack)
        {
            return;
        }

        var target = Duration > 0 ? Math.Clamp(seconds, 0, Duration) : Math.Max(0, seconds);

        // Show the target immediately and keep interpolating from it until the service acknowledges the seek;
        // events produced before the seek was processed arrive before the reply and must not pull the slider back.
        _seekPending = true;
        _anchorPosition = target;
        _anchorTime = _ui.Scheduler.UtcNow;
        UpdatePosition();
        try
        {
            await TryAsync(() => _client.Playback.SeekAsync(target)).ConfigureAwait(true);
        }
        finally
        {
            _seekPending = false;
        }
    }

    [RelayCommand]
    private Task PlayPauseAsync() => TryAsync(async () =>
    {
        switch (Status)
        {
            case PlaybackStatus.Playing or PlaybackStatus.Crossfading:
                await _client.Playback.PauseAsync().ConfigureAwait(true);
                break;
            case PlaybackStatus.Loading:
                // The state machine has no Pause transition from Loading; wait for the track to start.
                break;
            case PlaybackStatus.Paused:
                await _client.Playback.ResumeAsync().ConfigureAwait(true);
                break;
            default:
                // Stopped / Idle / Error: (re)start the current queue item, or the first one.
                var itemId = CurrentQueueItemId;
                if (itemId is null)
                {
                    var queue = await _client.Playback.GetQueueAsync().ConfigureAwait(true);
                    if (queue.Items.Count == 0)
                    {
                        StatusMessage = "The queue is empty. Add tracks from the library first.";
                        return;
                    }

                    itemId = queue.Items[Math.Clamp(queue.CurrentIndex, 0, queue.Items.Count - 1)].QueueItemId;
                }

                await _client.Playback.PlayQueueItemAsync(itemId.Value).ConfigureAwait(true);
                break;
        }
    });

    [RelayCommand]
    private Task StopAsync() => TryAsync(() => _client.Playback.StopAsync());

    [RelayCommand]
    private Task NextAsync() => TryAsync(() => _client.Playback.NextAsync());

    [RelayCommand]
    private Task PreviousAsync() => TryAsync(() => _client.Playback.PreviousAsync());

    [RelayCommand]
    private Task ToggleMuteAsync()
    {
        var target = !Muted;
        _applyingServerValues = true;
        Muted = target;
        _applyingServerValues = false;
        return TryAsync(() => _client.Playback.SetMutedAsync(target));
    }

    [RelayCommand]
    private Task CycleLoopModeAsync() => SetLoopModeAsync(LoopMode switch
    {
        LoopMode.Off => LoopMode.SmartLoop,
        LoopMode.SmartLoop => LoopMode.RepeatQueue,
        _ => LoopMode.Off,
    });

    [RelayCommand]
    private Task SetLoopModeAsync(LoopMode mode)
    {
        LoopMode = mode;
        return TryAsync(() => _client.Playback.SetLoopModeAsync(mode));
    }

    partial void OnVolumeChanged(double value)
    {
        if (_applyingServerValues)
        {
            return;
        }

        _volumeDirty = true;
        _volumeSender.Trigger(() => Run(async () =>
        {
            try
            {
                await _client.Playback.SetVolumeAsync((float)Math.Clamp(Volume, 0, 1)).ConfigureAwait(true);
            }
            finally
            {
                _volumeDirty = _volumeSender.IsPending;
            }
        }));
    }

    partial void OnCrossfadeSecondsChanged(double value)
    {
        if (_applyingServerValues)
        {
            return;
        }

        if (value < 0 || value > MaxCrossfadeSeconds || double.IsNaN(value))
        {
            CrossfadeSeconds = double.IsNaN(value) ? 0 : Math.Clamp(value, 0, MaxCrossfadeSeconds);
            return;
        }

        _crossfadeDirty = true;
        _crossfadeSender.Trigger(() => Run(async () =>
        {
            try
            {
                await _client.Playback.SetCrossfadeAsync(CrossfadeSeconds).ConfigureAwait(true);
            }
            finally
            {
                _crossfadeDirty = _crossfadeSender.IsPending;
            }
        }));
    }

    private void Tick() => UpdatePosition();

    private void UpdatePosition()
    {
        var position = _anchorPosition;
        if (IsPlaying || _seekPending)
        {
            var elapsed = (_ui.Scheduler.UtcNow - _anchorTime).TotalSeconds;
            if (elapsed > 0 && IsPlaying)
            {
                position += elapsed;
            }
        }

        if (Duration > 0)
        {
            position = Math.Min(position, Duration);
        }

        Position = Math.Max(0, position);
        if (!IsSeeking)
        {
            SliderPosition = Position;
        }
    }

    private void UpdateTicker()
    {
        if (IsPlaying)
        {
            _ticker ??= _ui.Scheduler.SchedulePeriodic(TickInterval, Tick);
        }
        else
        {
            _ticker?.Dispose();
            _ticker = null;
        }
    }

    private async Task UpdateTrackInfoAsync()
    {
        var currentId = CurrentTrackId;
        var nextId = NextTrackId;
        if (currentId is null)
        {
            Title = "Nothing playing";
            Artist = null;
        }
        else
        {
            var track = await _tracks.GetAsync(currentId.Value).ConfigureAwait(true);
            if (CurrentTrackId == currentId)
            {
                Title = track?.Title ?? "Unknown track";
                Artist = track?.Artist;
            }
        }

        if (nextId is null)
        {
            NextTitle = null;
        }
        else
        {
            var next = await _tracks.GetAsync(nextId.Value).ConfigureAwait(true);
            if (NextTrackId == nextId)
            {
                NextTitle = next?.Title ?? "Unknown track";
            }
        }
    }
}
