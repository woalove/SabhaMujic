using System.Security.Cryptography;
using SabhaMujic.Client;
using SabhaMujic.Contracts;

namespace SabhaMujic.Desktop.ViewModels.Demo;

/// <summary>
/// <b>DEVELOPMENT / DEMO TOOL — NOT PRODUCTION BEHAVIOUR.</b>
/// An in-memory, self-consistent fake of the SabhaMujic service, used only when the desktop app is started
/// with <c>--demo</c> (to try the UI before the real service/pipe client exists, and for screenshots).
/// It plays no audio: the playback position is simulated from the clock. Nothing is persisted, files are not
/// stored (only hashed, for duplicate detection), and the authorization here is a simplified imitation of the
/// real role matrix in <c>SabhaMujic.Core.Security.Authorizer</c>.
/// Demo accounts: <c>admin</c> / <c>1234</c> (Admin) and <c>sevak</c> / <c>1111</c> (Volunteer).
/// </summary>
public sealed class DemoSabhaClient : ISabhaClient
{
    public const string AdminUser = "admin";
    public const string AdminSecret = "1234";
    public const string VolunteerUser = "sevak";
    public const string VolunteerSecret = "1111";

    private static readonly TimeSpan TickPeriod = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(3);

    private readonly object _gate = new();
    private readonly TimeProvider _time;
    private readonly bool _autoTick;
    private readonly List<Action> _outbox = [];

    private readonly List<DemoUser> _users = [];
    private readonly List<DemoCategory> _categories = [];
    private readonly Dictionary<Guid, TrackDto> _tracks = [];
    private readonly Dictionary<Guid, PlaylistDto> _playlists = [];
    private readonly List<QueueItemDto> _queue = [];
    private readonly Dictionary<Guid, DemoUpload> _uploads = [];
    private readonly Dictionary<string, Guid> _contentHashes = [];
    private readonly List<DeviceDto> _devices = [];

    private int _currentIndex = -1;
    private long _queueRevision;
    private long _stateRevision;
    private PlaybackStatus _status = PlaybackStatus.Idle;
    private double _anchorPosition;
    private DateTimeOffset _anchorTime;
    private float _volume = 0.8f;
    private bool _muted;
    private LoopMode _loopMode = LoopMode.Off;
    private double _crossfadeSeconds = 6;
    private int _failedLogins;
    private ITimer? _ticker;
    private ITimer? _reconnectTimer;

    public DemoSabhaClient(TimeProvider? time = null, bool autoTick = true)
    {
        _time = time ?? TimeProvider.System;
        _autoTick = autoTick;
        Auth = new AuthApi(this);
        Library = new LibraryApi(this);
        Playback = new PlaybackApi(this);
        Playlists = new PlaylistApi(this);
        Admin = new AdminApi(this);
        Seed();
    }

    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;

    public SessionDto? Session { get; private set; }

    public IAuthApi Auth { get; }

    public ILibraryApi Library { get; }

    public IPlaybackApi Playback { get; }

    public IPlaylistApi Playlists { get; }

    public IAdminApi Admin { get; }

    public ClientEventHub Events { get; } = new();

    public event EventHandler<ConnectionState>? ConnectionStateChanged;

    public event EventHandler<SessionDto?>? SessionChanged;

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        SetState(ConnectionState.Connecting);
        await Task.Yield();
        ct.ThrowIfCancellationRequested();
        SetState(ConnectionState.Connected);
        if (_autoTick)
        {
            lock (_gate)
            {
                _ticker ??= _time.CreateTimer(_ => Pump(), null, TickPeriod, TickPeriod);
            }
        }
    }

    /// <summary>Demo aid: pretends the pipe dropped; reconnects (and resumes the session) after 3 seconds.</summary>
    public void SimulateConnectionLoss()
    {
        SetState(ConnectionState.Reconnecting);
        lock (_gate)
        {
            _reconnectTimer?.Dispose();
            _reconnectTimer = _time.CreateTimer(_ => SetState(ConnectionState.Connected), null, ReconnectDelay, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>
    /// Advances the simulation to the current time: track end → next track (with a crossfade window), Smart Loop
    /// wrap-around, finishing pending analyses. Called every second by a timer; tests call it directly.
    /// </summary>
    public void Pump()
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            foreach (var track in _tracks.Values.Where(t => t.Analysis is AnalysisStatus.Pending or AnalysisStatus.Analyzing).ToArray())
            {
                var analyzed = track with
                {
                    Analysis = AnalysisStatus.Ready,
                    Loop = track.Loop is { IsManualOverride: true } manual ? manual : AutoLoop(track.DurationSeconds),
                };
                _tracks[track.Id] = analyzed;
                EmitLibrary(LibraryChangeKind.AnalysisUpdated, track.Id);
            }

            if (_status is PlaybackStatus.Playing or PlaybackStatus.Crossfading && CurrentTrack() is { } current)
            {
                AdvancePlayback(current, now);
                EmitState();
            }
        }

        Flush();
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _ticker?.Dispose();
            _reconnectTimer?.Dispose();
            _ticker = _reconnectTimer = null;
        }

        SetState(ConnectionState.Disconnected);
        return ValueTask.CompletedTask;
    }

    // ---------------------------------------------------------------- simulation core (call under _gate)

    private void AdvancePlayback(TrackDto current, DateTimeOffset now)
    {
        var position = Position(now);
        var duration = current.DurationSeconds;

        if (_loopMode == LoopMode.SmartLoop)
        {
            var loop = current.Loop ?? new LoopRegionDto(0, duration, 0.2, 0, false);
            if (position >= loop.EndSeconds && loop.EndSeconds > loop.StartSeconds)
            {
                var length = loop.EndSeconds - loop.StartSeconds;
                _anchorPosition = loop.StartSeconds + ((position - loop.EndSeconds) % length);
                _anchorTime = now;
            }

            _status = PlaybackStatus.Playing;
            return;
        }

        var next = NextIndex();
        if (next >= 0 && _crossfadeSeconds > 0 && position >= duration - _crossfadeSeconds && position < duration)
        {
            _status = PlaybackStatus.Crossfading;
        }

        if (position < duration)
        {
            return;
        }

        if (next < 0)
        {
            _status = PlaybackStatus.Stopped;
            _anchorPosition = 0;
            _anchorTime = now;
            return;
        }

        // The next track started fading in `crossfade` seconds before the end.
        var overshoot = position - duration;
        _currentIndex = next;
        _status = PlaybackStatus.Playing;
        _anchorPosition = Math.Min(overshoot + _crossfadeSeconds, CurrentTrack()?.DurationSeconds ?? 0);
        _anchorTime = now;
        TouchQueue();
    }

    private double Position(DateTimeOffset now)
    {
        if (_status is PlaybackStatus.Playing or PlaybackStatus.Crossfading)
        {
            var position = _anchorPosition + Math.Max(0, (now - _anchorTime).TotalSeconds);
            return Math.Min(position, (CurrentTrack()?.DurationSeconds ?? position) + 3600);
        }

        return _anchorPosition;
    }

    private TrackDto? CurrentTrack() =>
        _currentIndex >= 0 && _currentIndex < _queue.Count && _tracks.TryGetValue(_queue[_currentIndex].TrackId, out var t) ? t : null;

    private int NextIndex()
    {
        if (_queue.Count == 0)
        {
            return -1;
        }

        if (_currentIndex + 1 < _queue.Count)
        {
            return _currentIndex + 1;
        }

        return _loopMode == LoopMode.RepeatQueue ? 0 : -1;
    }

    private void StartCurrent()
    {
        if (CurrentTrack() is null)
        {
            _status = PlaybackStatus.Stopped;
            _anchorPosition = 0;
        }
        else
        {
            _status = PlaybackStatus.Playing;
            _anchorPosition = 0;
        }

        _anchorTime = _time.GetUtcNow();
    }

    private PlaybackStateDto Snapshot()
    {
        var now = _time.GetUtcNow();
        var current = CurrentTrack();
        var next = NextIndex();
        return new PlaybackStateDto(
            _status,
            current?.Id,
            current is null ? null : _queue[_currentIndex].QueueItemId,
            next >= 0 ? _queue[next].TrackId : null,
            current is null ? 0 : Math.Min(Position(now), current.DurationSeconds),
            current?.DurationSeconds ?? 0,
            _volume,
            _muted,
            _loopMode,
            _crossfadeSeconds,
            null,
            _stateRevision,
            now);
    }

    private QueueDto QueueSnapshot() => new(_queue.ToArray(), _currentIndex, _queueRevision);

    private void TouchQueue()
    {
        _queueRevision++;
        var snapshot = QueueSnapshot();
        _outbox.Add(() => Events.QueueChanged(snapshot));
    }

    private void EmitState()
    {
        _stateRevision++;
        var snapshot = Snapshot();
        _outbox.Add(() => Events.PlaybackStateChanged(snapshot));
    }

    private void EmitLibrary(LibraryChangeKind kind, Guid? id)
    {
        var change = new LibraryChangedDto(kind, id);
        _outbox.Add(() => Events.LibraryChanged(change));
    }

    /// <summary>Raises queued events outside the lock, in order.</summary>
    private void Flush()
    {
        Action[] pending;
        lock (_gate)
        {
            pending = [.. _outbox];
            _outbox.Clear();
        }

        foreach (var raise in pending)
        {
            raise();
        }
    }

    private T Do<T>(Func<T> action, bool admin = false, bool authenticated = true)
    {
        try
        {
            lock (_gate)
            {
                if (State != ConnectionState.Connected)
                {
                    throw new IOException("Not connected (demo).");
                }

                if (authenticated && Session is null)
                {
                    throw new SabhaMujicException(ErrorCode.Unauthenticated, "Not signed in.");
                }

                if (admin && Session?.Role != Role.Admin)
                {
                    throw new SabhaMujicException(ErrorCode.Forbidden, "Admins only.");
                }

                return action();
            }
        }
        finally
        {
            Flush();
        }
    }

    private Task<T> Run<T>(Func<T> action, bool admin = false, bool authenticated = true)
    {
        try
        {
            return Task.FromResult(Do(action, admin, authenticated));
        }
        catch (Exception ex)
        {
            return Task.FromException<T>(ex);
        }
    }

    private Task Run(Action action, bool admin = false) => Run<bool>(() =>
    {
        action();
        return true;
    }, admin);

    private void SetState(ConnectionState state)
    {
        lock (_gate)
        {
            if (State == state)
            {
                return;
            }

            State = state;
        }

        ConnectionStateChanged?.Invoke(this, state);
    }

    private void SetSession(SessionDto? session)
    {
        Session = session;
        _outbox.Add(() => SessionChanged?.Invoke(this, session));
    }

    private int QueueIndexOf(Guid queueItemId) => _queue.FindIndex(i => i.QueueItemId == queueItemId);

    private DemoCategory CategoryOrThrow(Guid id) =>
        _categories.FirstOrDefault(c => c.Id == id) ?? throw new SabhaMujicException(ErrorCode.NotFound, "No such category.");

    private TrackDto TrackOrThrow(Guid id) =>
        _tracks.TryGetValue(id, out var t) ? t : throw new SabhaMujicException(ErrorCode.NotFound, "No such track.");

    private PlaylistDto PlaylistOrThrow(Guid id) =>
        _playlists.TryGetValue(id, out var p) ? p : throw new SabhaMujicException(ErrorCode.NotFound, "No such playlist.");

    private static string RequireName(string? name, string what) => string.IsNullOrWhiteSpace(name)
        ? throw new SabhaMujicException(ErrorCode.InvalidArgument, $"{what} can't be empty.")
        : name.Trim();

    private static LoopRegionDto AutoLoop(double duration) =>
        new(Math.Round(duration * 0.1, 3), Math.Round(duration * 0.9, 3), 0.2, 0.82, false);

    private void Seed()
    {
        var now = _time.GetUtcNow();
        _users.Add(new DemoUser(Guid.NewGuid(), AdminUser, Role.Admin, AdminSecret, now));
        _users.Add(new DemoUser(Guid.NewGuid(), VolunteerUser, Role.Volunteer, VolunteerSecret, now));

        string[] categoryNames = ["Dhun", "Kirtan", "Aarti", "Instrumental"];
        for (var i = 0; i < categoryNames.Length; i++)
        {
            _categories.Add(new DemoCategory(Guid.NewGuid(), categoryNames[i], i));
        }

        void AddTrack(string title, string? artist, int category, double seconds)
        {
            var id = Guid.NewGuid();
            _tracks[id] = new TrackDto(id, title, artist, _categories[category].Id, seconds, AnalysisStatus.Ready, AutoLoop(seconds), now);
        }

        AddTrack("Demo Dhun – Morning", "Demo Ensemble", 0, 245);
        AddTrack("Demo Dhun – Evening", "Demo Ensemble", 0, 318);
        AddTrack("Demo Dhun – Slow", null, 0, 402);
        AddTrack("Demo Kirtan 1", "Demo Choir", 1, 276);
        AddTrack("Demo Kirtan 2", "Demo Choir", 1, 333);
        AddTrack("Demo Kirtan 3", "Demo Choir", 1, 190);
        AddTrack("Demo Aarti – Sandhya", "Demo Choir", 2, 214);
        AddTrack("Demo Aarti – Mangla", "Demo Choir", 2, 228);
        AddTrack("Demo Instrumental – Flute", "Demo Players", 3, 365);
        AddTrack("Demo Instrumental – Sitar", "Demo Players", 3, 421);
        var uncategorized = Guid.NewGuid();
        _tracks[uncategorized] = new TrackDto(uncategorized, "Demo Uncategorized Track", null, null, 180, AnalysisStatus.Ready, AutoLoop(180), now);

        var playlistId = Guid.NewGuid();
        _playlists[playlistId] = new PlaylistDto(
            playlistId,
            "Demo Sunday Sabha",
            _tracks.Values.Where(t => t.CategoryId == _categories[0].Id || t.CategoryId == _categories[2].Id).Select(t => t.Id).ToArray(),
            now);

        _devices.Add(new DeviceDto(Guid.NewGuid(), "Demo iPhone", Role.Volunteer, now.AddDays(-7), now.AddHours(-2)));
    }

    private sealed record DemoUser(Guid Id, string UserName, Role Role, string Secret, DateTimeOffset CreatedAt);

    private sealed record DemoCategory(Guid Id, string Name, int SortOrder);

    private sealed class DemoUpload(string fileName, long totalBytes)
    {
        public string FileName { get; } = fileName;

        public long TotalBytes { get; } = totalBytes;

        public long Received { get; set; }

        public IncrementalHash Hash { get; } = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    }

    // ---------------------------------------------------------------- APIs

    private sealed class AuthApi(DemoSabhaClient s) : IAuthApi
    {
        public Task<HelloResponse> HelloAsync(int clientContractVersion, CancellationToken ct = default) =>
            s.Run(() => clientContractVersion == ContractVersion.Current
                ? new HelloResponse(ContractVersion.Current, "demo", "DEMO")
                : throw new SabhaMujicException(ErrorCode.IncompatibleClient, "Contract version mismatch."), authenticated: false);

        public Task<SessionDto> LoginAsync(string userName, string secret, CancellationToken ct = default) => s.Run(() =>
        {
            if (s._failedLogins >= 5)
            {
                s._failedLogins = 0; // the demo "rate limit" resets after reporting once
                throw new SabhaMujicException(ErrorCode.RateLimited, "Too many attempts.");
            }

            var user = s._users.FirstOrDefault(u => string.Equals(u.UserName, userName?.Trim(), StringComparison.OrdinalIgnoreCase));
            if (user is null || user.Secret != secret)
            {
                s._failedLogins++;
                throw new SabhaMujicException(ErrorCode.Unauthenticated, "Wrong user name or PIN.");
            }

            s._failedLogins = 0;
            var session = new SessionDto(
                Convert.ToHexString(RandomNumberGenerator.GetBytes(16)),
                user.UserName,
                user.Role,
                s._time.GetUtcNow().AddHours(12));
            s.SetSession(session);
            return session;
        }, authenticated: false);

        public Task<SessionDto> ResumeSessionAsync(string token, CancellationToken ct = default) => s.Run(() =>
            s.Session is { } session && session.Token == token
                ? session
                : throw new SabhaMujicException(ErrorCode.Unauthenticated, "Session expired."), authenticated: false);

        public Task LogoutAsync(CancellationToken ct = default) => s.Run<bool>(() =>
        {
            s.SetSession(null);
            return true;
        }, authenticated: false);

        public Task<bool> IsSetupRequiredAsync(CancellationToken ct = default) => s.Run(() => s._users.Count == 0, authenticated: false);

        /// <summary>The demo is always "elevated"; it ships with accounts, so this normally reports Conflict.</summary>
        public Task<SessionDto> CompleteInitialSetupAsync(string adminUserName, string secret, CancellationToken ct = default) => s.Run(() =>
        {
            if (s._users.Count > 0)
            {
                throw new SabhaMujicException(ErrorCode.Conflict, "SabhaMujic is already set up.");
            }

            if (string.IsNullOrWhiteSpace(adminUserName) || (secret?.Length ?? 0) < 4)
            {
                throw new SabhaMujicException(ErrorCode.InvalidArgument, "A name and a PIN of at least 4 characters are required.");
            }

            var now = s._time.GetUtcNow();
            s._users.Add(new DemoUser(Guid.NewGuid(), adminUserName.Trim(), Role.Admin, secret!, now));
            var session = new SessionDto(Convert.ToHexString(RandomNumberGenerator.GetBytes(16)), adminUserName.Trim(), Role.Admin, now.AddHours(12));
            s.SetSession(session);
            return session;
        }, authenticated: false);

        /// <summary>
        /// The demo is always "elevated": resets the named admin's PIN, or adds a new admin with that name. A volunteer's name
        /// reports Conflict, like the service.
        /// </summary>
        public Task<SessionDto> ResetAdministratorAsync(string adminUserName, string newSecret, CancellationToken ct = default) => s.Run(() =>
        {
            var name = adminUserName?.Trim() ?? "";
            if (name.Length == 0 || (newSecret?.Length ?? 0) < 4)
            {
                throw new SabhaMujicException(ErrorCode.InvalidArgument, "A name and a PIN of at least 4 characters are required.");
            }

            var now = s._time.GetUtcNow();
            var index = s._users.FindIndex(u => string.Equals(u.UserName, name, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                s._users.Add(new DemoUser(Guid.NewGuid(), name, Role.Admin, newSecret!, now));
            }
            else if (s._users[index].Role != Role.Admin)
            {
                throw new SabhaMujicException(ErrorCode.Conflict, $"'{s._users[index].UserName}' is not an administrator.");
            }
            else
            {
                name = s._users[index].UserName;
                s._users[index] = s._users[index] with { Secret = newSecret! };
            }

            s._failedLogins = 0;
            var session = new SessionDto(Convert.ToHexString(RandomNumberGenerator.GetBytes(16)), name, Role.Admin, now.AddHours(12));
            s.SetSession(session);
            return session;
        }, authenticated: false);
    }

    private sealed class LibraryApi(DemoSabhaClient s) : ILibraryApi
    {
        public Task<IReadOnlyList<CategoryDto>> GetCategoriesAsync(CancellationToken ct = default) =>
            s.Run<IReadOnlyList<CategoryDto>>(() => s._categories
                .OrderBy(c => c.SortOrder)
                .Select(c => new CategoryDto(c.Id, c.Name, c.SortOrder, s._tracks.Values.Count(t => t.CategoryId == c.Id)))
                .ToArray());

        public Task<PagedResult<TrackDto>> GetTracksAsync(TrackQuery query, CancellationToken ct = default) => s.Run(() =>
        {
            ArgumentNullException.ThrowIfNull(query);
            IEnumerable<TrackDto> items = s._tracks.Values;
            if (query.CategoryId is { } category)
            {
                items = items.Where(t => t.CategoryId == category);
            }

            if (!string.IsNullOrWhiteSpace(query.Search))
            {
                var term = query.Search.Trim();
                items = items.Where(t =>
                    t.Title.Contains(term, StringComparison.CurrentCultureIgnoreCase) ||
                    (t.Artist?.Contains(term, StringComparison.CurrentCultureIgnoreCase) ?? false));
            }

            var all = items.OrderBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase).ToArray();
            var page = all.Skip(Math.Max(0, query.Skip)).Take(Math.Clamp(query.Take, 0, 1000)).ToArray();
            return new PagedResult<TrackDto>(page, all.Length);
        });

        public Task<TrackDto> GetTrackAsync(Guid trackId, CancellationToken ct = default) => s.Run(() => s.TrackOrThrow(trackId));

        public Task<IReadOnlyList<TrackDto>> GetTracksByIdsAsync(IReadOnlyList<Guid> trackIds, CancellationToken ct = default) =>
            s.Run(() => (IReadOnlyList<TrackDto>)trackIds
                .Where(s._tracks.ContainsKey)
                .Select(id => s._tracks[id])
                .ToList());
    }

    private sealed class PlaybackApi(DemoSabhaClient s) : IPlaybackApi
    {
        public Task<PlaybackStateDto> GetStateAsync(CancellationToken ct = default) => s.Run(s.Snapshot);

        public Task<QueueDto> GetQueueAsync(CancellationToken ct = default) => s.Run(s.QueueSnapshot);

        public Task PlayTrackAsync(Guid trackId, CancellationToken ct = default) => s.Run(() =>
        {
            s.TrackOrThrow(trackId);
            var insertAt = s._currentIndex >= 0 ? s._currentIndex + 1 : s._queue.Count;
            s._queue.Insert(insertAt, new QueueItemDto(Guid.NewGuid(), trackId));
            s._currentIndex = insertAt;
            s.StartCurrent();
            s.TouchQueue();
            s.EmitState();
        });

        public Task PlayQueueItemAsync(Guid queueItemId, CancellationToken ct = default) => s.Run(() =>
        {
            var index = s.QueueIndexOf(queueItemId);
            if (index < 0)
            {
                throw new SabhaMujicException(ErrorCode.NotFound, "Not in the queue.");
            }

            s._currentIndex = index;
            s.StartCurrent();
            s.TouchQueue();
            s.EmitState();
        });

        public Task PauseAsync(CancellationToken ct = default) => s.Run(() =>
        {
            if (s._status is PlaybackStatus.Playing or PlaybackStatus.Crossfading)
            {
                s._anchorPosition = s.Position(s._time.GetUtcNow());
                s._anchorTime = s._time.GetUtcNow();
                s._status = PlaybackStatus.Paused;
                s.EmitState();
            }
        });

        public Task ResumeAsync(CancellationToken ct = default) => s.Run(() =>
        {
            if (s._status == PlaybackStatus.Paused)
            {
                s._anchorTime = s._time.GetUtcNow();
                s._status = PlaybackStatus.Playing;
                s.EmitState();
            }
        });

        public Task StopAsync(CancellationToken ct = default) => s.Run(() =>
        {
            s._status = s.CurrentTrack() is null ? PlaybackStatus.Idle : PlaybackStatus.Stopped;
            s._anchorPosition = 0;
            s._anchorTime = s._time.GetUtcNow();
            s.EmitState();
        });

        public Task NextAsync(CancellationToken ct = default) => s.Run(() =>
        {
            var next = s.NextIndex();
            if (next < 0)
            {
                s._status = PlaybackStatus.Stopped;
                s._anchorPosition = 0;
            }
            else
            {
                s._currentIndex = next;
                s.StartCurrent();
                s.TouchQueue();
            }

            s.EmitState();
        });

        public Task PreviousAsync(CancellationToken ct = default) => s.Run(() =>
        {
            if (s.Position(s._time.GetUtcNow()) <= 3 && s._currentIndex > 0)
            {
                s._currentIndex--;
                s.TouchQueue();
            }

            if (s.CurrentTrack() is not null)
            {
                s.StartCurrent();
            }

            s.EmitState();
        });

        public Task SeekAsync(double positionSeconds, CancellationToken ct = default) => s.Run(() =>
        {
            var track = s.CurrentTrack() ?? throw new SabhaMujicException(ErrorCode.Conflict, "Nothing is playing.");
            if (!double.IsFinite(positionSeconds))
            {
                throw new SabhaMujicException(ErrorCode.InvalidArgument, "Invalid position.");
            }

            s._anchorPosition = Math.Clamp(positionSeconds, 0, track.DurationSeconds);
            s._anchorTime = s._time.GetUtcNow();
            if (s._status == PlaybackStatus.Crossfading)
            {
                s._status = PlaybackStatus.Playing;
            }

            s.EmitState();
        });

        public Task SetVolumeAsync(float volume, CancellationToken ct = default) => s.Run(() =>
        {
            if (!float.IsFinite(volume) || volume < 0 || volume > 1)
            {
                throw new SabhaMujicException(ErrorCode.InvalidArgument, "Volume must be between 0 and 1.");
            }

            s._volume = volume;
            s.EmitState();
        });

        public Task SetMutedAsync(bool muted, CancellationToken ct = default) => s.Run(() =>
        {
            s._muted = muted;
            s.EmitState();
        });

        public Task SetLoopModeAsync(LoopMode mode, CancellationToken ct = default) => s.Run(() =>
        {
            if (!Enum.IsDefined(mode))
            {
                throw new SabhaMujicException(ErrorCode.InvalidArgument, "Unknown loop mode.");
            }

            s._loopMode = mode;
            s.EmitState();
        });

        public Task SetCrossfadeAsync(double seconds, CancellationToken ct = default) => s.Run(() =>
        {
            if (!double.IsFinite(seconds) || seconds < 0 || seconds > 12)
            {
                throw new SabhaMujicException(ErrorCode.InvalidArgument, "Crossfade must be between 0 and 12 seconds.");
            }

            s._crossfadeSeconds = seconds;
            s.EmitState();
        });

        public Task<QueueDto> EnqueueAsync(IReadOnlyList<Guid> trackIds, bool playNext, CancellationToken ct = default) => s.Run(() =>
        {
            ArgumentNullException.ThrowIfNull(trackIds);
            foreach (var id in trackIds)
            {
                s.TrackOrThrow(id);
            }

            var insertAt = playNext && s._currentIndex >= 0 ? s._currentIndex + 1 : s._queue.Count;
            s._queue.InsertRange(insertAt, trackIds.Select(id => new QueueItemDto(Guid.NewGuid(), id)));
            s.TouchQueue();
            s.EmitState(); // NextTrackId may have changed
            return s.QueueSnapshot();
        });

        public Task<QueueDto> EnqueuePlaylistAsync(Guid playlistId, bool replaceQueue, CancellationToken ct = default) => s.Run(() =>
        {
            var playlist = s.PlaylistOrThrow(playlistId);
            var items = playlist.TrackIds.Where(s._tracks.ContainsKey).Select(id => new QueueItemDto(Guid.NewGuid(), id)).ToArray();
            if (replaceQueue)
            {
                var wasPlaying = s._status is PlaybackStatus.Playing or PlaybackStatus.Crossfading;
                s._queue.Clear();
                s._queue.AddRange(items);
                s._currentIndex = s._queue.Count > 0 ? 0 : -1;
                if (wasPlaying)
                {
                    s.StartCurrent();
                }
                else
                {
                    s._status = s._queue.Count > 0 ? PlaybackStatus.Stopped : PlaybackStatus.Idle;
                    s._anchorPosition = 0;
                }
            }
            else
            {
                s._queue.AddRange(items);
            }

            s.TouchQueue();
            s.EmitState();
            return s.QueueSnapshot();
        });

        public Task<QueueDto> RemoveFromQueueAsync(Guid queueItemId, CancellationToken ct = default) => s.Run(() =>
        {
            var index = s.QueueIndexOf(queueItemId);
            if (index < 0)
            {
                throw new SabhaMujicException(ErrorCode.NotFound, "Not in the queue.");
            }

            s._queue.RemoveAt(index);
            if (index < s._currentIndex)
            {
                s._currentIndex--;
            }
            else if (index == s._currentIndex)
            {
                // The playing item was removed: continue with the item that took its place.
                if (s._currentIndex >= s._queue.Count)
                {
                    s._currentIndex = s._queue.Count - 1;
                }

                var wasPlaying = s._status is PlaybackStatus.Playing or PlaybackStatus.Crossfading;
                if (s._currentIndex < 0)
                {
                    s._status = PlaybackStatus.Idle;
                    s._anchorPosition = 0;
                }
                else if (wasPlaying)
                {
                    s.StartCurrent();
                }
                else
                {
                    s._status = PlaybackStatus.Stopped;
                    s._anchorPosition = 0;
                }
            }

            s.TouchQueue();
            s.EmitState();
            return s.QueueSnapshot();
        });

        public Task<QueueDto> MoveQueueItemAsync(Guid queueItemId, int newIndex, CancellationToken ct = default) => s.Run(() =>
        {
            var from = s.QueueIndexOf(queueItemId);
            if (from < 0)
            {
                throw new SabhaMujicException(ErrorCode.NotFound, "Not in the queue.");
            }

            newIndex = Math.Clamp(newIndex, 0, s._queue.Count - 1);
            if (from != newIndex)
            {
                var currentId = s._currentIndex >= 0 ? s._queue[s._currentIndex].QueueItemId : (Guid?)null;
                var item = s._queue[from];
                s._queue.RemoveAt(from);
                s._queue.Insert(newIndex, item);
                s._currentIndex = currentId is null ? -1 : s.QueueIndexOf(currentId.Value);
                s.TouchQueue();
                s.EmitState();
            }

            return s.QueueSnapshot();
        });

        public Task<QueueDto> ClearQueueAsync(CancellationToken ct = default) => s.Run(() =>
        {
            var current = s._currentIndex >= 0 ? s._queue[s._currentIndex] : null;
            s._queue.Clear();
            if (current is not null)
            {
                s._queue.Add(current);
            }

            s._currentIndex = current is null ? -1 : 0;
            s.TouchQueue();
            s.EmitState();
            return s.QueueSnapshot();
        });
    }

    private sealed class PlaylistApi(DemoSabhaClient s) : IPlaylistApi
    {
        public Task<IReadOnlyList<PlaylistDto>> GetPlaylistsAsync(CancellationToken ct = default) =>
            s.Run<IReadOnlyList<PlaylistDto>>(() => s._playlists.Values.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToArray());

        public Task<PlaylistDto> GetPlaylistAsync(Guid playlistId, CancellationToken ct = default) =>
            s.Run(() => s.PlaylistOrThrow(playlistId));

        public Task<PlaylistDto> CreatePlaylistAsync(string name, IReadOnlyList<Guid>? trackIds = null, CancellationToken ct = default) => s.Run(() =>
        {
            var ids = ValidTracks(trackIds ?? []);
            var playlist = new PlaylistDto(Guid.NewGuid(), RequireName(name, "Playlist name"), ids, s._time.GetUtcNow());
            s._playlists[playlist.Id] = playlist;
            s.EmitLibrary(LibraryChangeKind.PlaylistsChanged, playlist.Id);
            return playlist;
        });

        public Task<PlaylistDto> RenamePlaylistAsync(Guid playlistId, string name, CancellationToken ct = default) => s.Run(() =>
        {
            var updated = s.PlaylistOrThrow(playlistId) with { Name = RequireName(name, "Playlist name"), UpdatedAt = s._time.GetUtcNow() };
            s._playlists[playlistId] = updated;
            s.EmitLibrary(LibraryChangeKind.PlaylistsChanged, playlistId);
            return updated;
        });

        public Task<PlaylistDto> SetPlaylistTracksAsync(Guid playlistId, IReadOnlyList<Guid> trackIds, CancellationToken ct = default) => s.Run(() =>
        {
            var updated = s.PlaylistOrThrow(playlistId) with { TrackIds = ValidTracks(trackIds), UpdatedAt = s._time.GetUtcNow() };
            s._playlists[playlistId] = updated;
            s.EmitLibrary(LibraryChangeKind.PlaylistsChanged, playlistId);
            return updated;
        });

        public Task DeletePlaylistAsync(Guid playlistId, CancellationToken ct = default) => s.Run(() =>
        {
            s.PlaylistOrThrow(playlistId);
            s._playlists.Remove(playlistId);
            s.EmitLibrary(LibraryChangeKind.PlaylistsChanged, playlistId);
        });

        private Guid[] ValidTracks(IReadOnlyList<Guid> ids)
        {
            foreach (var id in ids)
            {
                s.TrackOrThrow(id);
            }

            return [.. ids];
        }
    }

    private sealed class AdminApi(DemoSabhaClient s) : IAdminApi
    {
        public Task<Guid> BeginImportAsync(string fileName, long totalBytes, CancellationToken ct = default) => s.Run(() =>
        {
            if (!AdminLimits.AllowedExtensions.Contains(Path.GetExtension(fileName ?? "")))
            {
                throw new SabhaMujicException(ErrorCode.UnsupportedFormat, "Unsupported file type.");
            }

            if (totalBytes <= 0)
            {
                throw new SabhaMujicException(ErrorCode.InvalidArgument, "The file is empty.");
            }

            if (totalBytes > AdminLimits.MaxUploadBytes)
            {
                throw new SabhaMujicException(ErrorCode.TooLarge, "File too large.");
            }

            if (s._uploads.Count >= AdminLimits.MaxConcurrentUploadsPerSession)
            {
                throw new SabhaMujicException(ErrorCode.Conflict, "Too many uploads at once.");
            }

            var id = Guid.NewGuid();
            s._uploads[id] = new DemoUpload(fileName!, totalBytes);
            return id;
        }, admin: true);

        public Task UploadChunkAsync(Guid uploadId, long offset, byte[] data, CancellationToken ct = default) => s.Run(() =>
        {
            ArgumentNullException.ThrowIfNull(data);
            var upload = Upload(uploadId);
            if (data.Length == 0 || data.Length > AdminLimits.MaxChunkBytes)
            {
                throw new SabhaMujicException(ErrorCode.TooLarge, "Chunk too large.");
            }

            if (offset != upload.Received || offset + data.Length > upload.TotalBytes)
            {
                throw new SabhaMujicException(ErrorCode.InvalidArgument, $"Expected a chunk at offset {upload.Received}.");
            }

            upload.Hash.AppendData(data);
            upload.Received += data.Length;
            var progress = new ImportProgressDto(uploadId, upload.FileName, upload.Received, upload.TotalBytes, "Uploading");
            s._outbox.Add(() => s.Events.ImportProgress(progress));
        }, admin: true);

        public Task<ImportResultDto> CompleteImportAsync(Guid uploadId, ImportMetadata metadata, CancellationToken ct = default) => s.Run(() =>
        {
            ArgumentNullException.ThrowIfNull(metadata);
            var upload = Upload(uploadId);
            if (upload.Received != upload.TotalBytes)
            {
                throw new SabhaMujicException(ErrorCode.InvalidArgument, "The upload is incomplete.");
            }

            var title = RequireName(metadata.Title, "Title");
            if (metadata.CategoryId is { } c)
            {
                s.CategoryOrThrow(c);
            }

            s._uploads.Remove(uploadId);
            var hash = Convert.ToHexString(upload.Hash.GetHashAndReset());
            upload.Hash.Dispose();
            if (s._contentHashes.TryGetValue(hash, out var existing) && s._tracks.ContainsKey(existing))
            {
                return new ImportResultDto(existing, WasDuplicate: true);
            }

            // Fake duration: assume ~128 kbit/s audio.
            var duration = Math.Clamp(upload.TotalBytes / 16_000.0, 30, 3600);
            var id = Guid.NewGuid();
            s._tracks[id] = new TrackDto(
                id,
                title,
                string.IsNullOrWhiteSpace(metadata.Artist) ? null : metadata.Artist.Trim(),
                metadata.CategoryId,
                Math.Round(duration, 1),
                AnalysisStatus.Analyzing,
                null,
                s._time.GetUtcNow());
            s._contentHashes[hash] = id;
            var progress = new ImportProgressDto(uploadId, upload.FileName, upload.TotalBytes, upload.TotalBytes, "Analyzing");
            s._outbox.Add(() => s.Events.ImportProgress(progress));
            s.EmitLibrary(LibraryChangeKind.TrackAdded, id);
            return new ImportResultDto(id, WasDuplicate: false);
        }, admin: true);

        public Task CancelImportAsync(Guid uploadId, CancellationToken ct = default) => s.Run(() =>
        {
            if (s._uploads.Remove(uploadId, out var upload))
            {
                upload.Hash.Dispose();
            }
        }, admin: true);

        public Task<CategoryDto> CreateCategoryAsync(string name, CancellationToken ct = default) => s.Run(() =>
        {
            var trimmed = UniqueCategoryName(name, null);
            var category = new DemoCategory(Guid.NewGuid(), trimmed, s._categories.Count == 0 ? 0 : s._categories.Max(c => c.SortOrder) + 1);
            s._categories.Add(category);
            s.EmitLibrary(LibraryChangeKind.CategoriesChanged, category.Id);
            return new CategoryDto(category.Id, category.Name, category.SortOrder, 0);
        }, admin: true);

        public Task<CategoryDto> RenameCategoryAsync(Guid categoryId, string name, CancellationToken ct = default) => s.Run(() =>
        {
            var category = s.CategoryOrThrow(categoryId);
            var renamed = category with { Name = UniqueCategoryName(name, categoryId) };
            s._categories[s._categories.IndexOf(category)] = renamed;
            s.EmitLibrary(LibraryChangeKind.CategoriesChanged, categoryId);
            return new CategoryDto(renamed.Id, renamed.Name, renamed.SortOrder, s._tracks.Values.Count(t => t.CategoryId == categoryId));
        }, admin: true);

        public Task ReorderCategoriesAsync(IReadOnlyList<Guid> orderedCategoryIds, CancellationToken ct = default) => s.Run(() =>
        {
            ArgumentNullException.ThrowIfNull(orderedCategoryIds);
            if (orderedCategoryIds.Count != s._categories.Count ||
                !orderedCategoryIds.ToHashSet().SetEquals(s._categories.Select(c => c.Id)))
            {
                throw new SabhaMujicException(ErrorCode.InvalidArgument, "The new order must list every category exactly once.");
            }

            var reordered = orderedCategoryIds.Select((id, i) => s.CategoryOrThrow(id) with { SortOrder = i }).ToArray();
            s._categories.Clear();
            s._categories.AddRange(reordered);
            s.EmitLibrary(LibraryChangeKind.CategoriesChanged, null);
        }, admin: true);

        public Task DeleteCategoryAsync(Guid categoryId, CancellationToken ct = default) => s.Run(() =>
        {
            s._categories.Remove(s.CategoryOrThrow(categoryId));
            foreach (var track in s._tracks.Values.Where(t => t.CategoryId == categoryId).ToArray())
            {
                s._tracks[track.Id] = track with { CategoryId = null };
            }

            s.EmitLibrary(LibraryChangeKind.CategoriesChanged, categoryId);
        }, admin: true);

        public Task<TrackDto> UpdateTrackAsync(TrackUpdateDto update, CancellationToken ct = default) => s.Run(() =>
        {
            ArgumentNullException.ThrowIfNull(update);
            var track = s.TrackOrThrow(update.TrackId);
            if (update.CategoryId is { } c)
            {
                s.CategoryOrThrow(c);
            }

            var updated = track with
            {
                Title = RequireName(update.Title, "Title"),
                Artist = string.IsNullOrWhiteSpace(update.Artist) ? null : update.Artist.Trim(),
                CategoryId = update.CategoryId,
            };
            s._tracks[track.Id] = updated;
            s.EmitLibrary(LibraryChangeKind.TrackUpdated, track.Id);
            return updated;
        }, admin: true);

        public Task DeleteTrackAsync(Guid trackId, CancellationToken ct = default) => s.Run(() =>
        {
            s.TrackOrThrow(trackId);
            var currentItem = s._currentIndex >= 0 ? s._queue[s._currentIndex] : null;
            s._tracks.Remove(trackId);
            foreach (var key in s._contentHashes.Where(kv => kv.Value == trackId).Select(kv => kv.Key).ToArray())
            {
                s._contentHashes.Remove(key);
            }

            foreach (var playlist in s._playlists.Values.Where(p => p.TrackIds.Contains(trackId)).ToArray())
            {
                s._playlists[playlist.Id] = playlist with { TrackIds = playlist.TrackIds.Where(id => id != trackId).ToArray() };
                s.EmitLibrary(LibraryChangeKind.PlaylistsChanged, playlist.Id);
            }

            if (s._queue.RemoveAll(i => i.TrackId == trackId) > 0)
            {
                s._currentIndex = currentItem is null ? -1 : s.QueueIndexOf(currentItem.QueueItemId);
                if (currentItem is not null && s._currentIndex < 0)
                {
                    s._status = PlaybackStatus.Stopped;
                    s._anchorPosition = 0;
                }

                s.TouchQueue();
                s.EmitState();
            }

            s.EmitLibrary(LibraryChangeKind.TrackRemoved, trackId);
        }, admin: true);

        public Task<TrackDto> SetLoopOverrideAsync(Guid trackId, LoopRegionDto? region, CancellationToken ct = default) => s.Run(() =>
        {
            var track = s.TrackOrThrow(trackId);
            if (region is not null &&
                (region.StartSeconds < 0 || region.EndSeconds <= region.StartSeconds || region.EndSeconds > track.DurationSeconds ||
                 region.CrossfadeSeconds < 0 || region.CrossfadeSeconds > (region.EndSeconds - region.StartSeconds) / 2))
            {
                throw new SabhaMujicException(ErrorCode.InvalidArgument, "Invalid loop region.");
            }

            var updated = track with
            {
                Loop = region is null ? AutoLoop(track.DurationSeconds) : region with { IsManualOverride = true },
            };
            s._tracks[trackId] = updated;
            s.EmitLibrary(LibraryChangeKind.AnalysisUpdated, trackId);
            return updated;
        }, admin: true);

        public Task ReanalyzeTrackAsync(Guid trackId, CancellationToken ct = default) => s.Run(() =>
        {
            s._tracks[trackId] = s.TrackOrThrow(trackId) with { Analysis = AnalysisStatus.Analyzing };
            s.EmitLibrary(LibraryChangeKind.AnalysisUpdated, trackId);
        }, admin: true);

        public Task<IReadOnlyList<UserDto>> GetUsersAsync(CancellationToken ct = default) =>
            s.Run<IReadOnlyList<UserDto>>(() => s._users.Select(u => new UserDto(u.Id, u.UserName, u.Role, u.CreatedAt)).ToArray(), admin: true);

        public Task<UserDto> CreateUserAsync(string userName, Role role, string secret, CancellationToken ct = default) => s.Run(() =>
        {
            var name = RequireName(userName, "User name");
            if (s._users.Any(u => string.Equals(u.UserName, name, StringComparison.OrdinalIgnoreCase)))
            {
                throw new SabhaMujicException(ErrorCode.Conflict, $"A user called \"{name}\" already exists.");
            }

            RequireSecret(secret);
            var user = new DemoUser(Guid.NewGuid(), name, role, secret, s._time.GetUtcNow());
            s._users.Add(user);
            return new UserDto(user.Id, user.UserName, user.Role, user.CreatedAt);
        }, admin: true);

        public Task SetUserSecretAsync(Guid userId, string secret, CancellationToken ct = default) => s.Run(() =>
        {
            RequireSecret(secret);
            var user = User(userId);
            s._users[s._users.IndexOf(user)] = user with { Secret = secret };
        }, admin: true);

        public Task DeleteUserAsync(Guid userId, CancellationToken ct = default) => s.Run(() =>
        {
            var user = User(userId);
            if (user.Role == Role.Admin && s._users.Count(u => u.Role == Role.Admin) == 1)
            {
                throw new SabhaMujicException(ErrorCode.Conflict, "The last admin can't be deleted.");
            }

            s._users.Remove(user);
        }, admin: true);

        public Task<IReadOnlyList<DeviceDto>> GetDevicesAsync(CancellationToken ct = default) =>
            s.Run<IReadOnlyList<DeviceDto>>(() => s._devices.ToArray(), admin: true);

        public Task<PairingCodeDto> CreatePairingCodeAsync(Role role, CancellationToken ct = default) => s.Run(() =>
        {
            var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
            return new PairingCodeDto(
                code,
                $"https://demo.sabhamujic.invalid:{Endpoints.RemoteHttpsPort}/pair#{code}",
                $"http://demo.sabhamujic.invalid:{Endpoints.BootstrapHttpPort}/",
                role,
                s._time.GetUtcNow().AddMinutes(5))
            {
                // A fixed, made-up fingerprint so the demo shows the comparison panel.
                CaFingerprint = string.Join(":", Enumerable.Range(0, 32).Select(i => ((i * 37 + 11) % 256).ToString("X2", System.Globalization.CultureInfo.InvariantCulture))),
            };
        }, admin: true);

        public Task RevokeDeviceAsync(Guid deviceId, CancellationToken ct = default) => s.Run(() =>
        {
            if (s._devices.RemoveAll(d => d.Id == deviceId) == 0)
            {
                throw new SabhaMujicException(ErrorCode.NotFound, "No such device.");
            }
        }, admin: true);

        private DemoUpload Upload(Guid id) =>
            s._uploads.TryGetValue(id, out var u) ? u : throw new SabhaMujicException(ErrorCode.NotFound, "No such upload.");

        private DemoUser User(Guid id) =>
            s._users.FirstOrDefault(u => u.Id == id) ?? throw new SabhaMujicException(ErrorCode.NotFound, "No such user.");

        private string UniqueCategoryName(string name, Guid? self)
        {
            var trimmed = RequireName(name, "Category name");
            if (s._categories.Any(c => c.Id != self && string.Equals(c.Name, trimmed, StringComparison.CurrentCultureIgnoreCase)))
            {
                throw new SabhaMujicException(ErrorCode.Conflict, $"A category called \"{trimmed}\" already exists.");
            }

            return trimmed;
        }

        private static void RequireSecret(string secret)
        {
            if (string.IsNullOrEmpty(secret) || secret.Length < 4)
            {
                throw new SabhaMujicException(ErrorCode.InvalidArgument, "The PIN/password must be at least 4 characters.");
            }
        }
    }
}
