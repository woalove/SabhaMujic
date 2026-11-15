using SabhaMujic.Client;
using SabhaMujic.Contracts;

namespace SabhaMujic.Desktop.Tests.Fakes;

/// <summary>One recorded API call.</summary>
public sealed record Call(string Method, params object?[] Args);

/// <summary>
/// Scripted <see cref="ISabhaClient"/> for view-model tests: records every API call and returns whatever the
/// test configured. Not a simulation (that is <c>DemoSabhaClient</c>'s job).
/// </summary>
public sealed class FakeSabhaClient : ISabhaClient
{
    public FakeSabhaClient()
    {
        Auth = new FakeAuth(this);
        Library = new FakeLibrary(this);
        Playback = new FakePlayback(this);
        Playlists = new FakePlaylists(this);
        Admin = new FakeAdmin(this);
    }

    public List<Call> Calls { get; } = [];

    public ConnectionState State { get; private set; } = ConnectionState.Connecting;

    public SessionDto? Session { get; private set; }

    public FakeAuth Auth { get; }

    public FakeLibrary Library { get; }

    public FakePlayback Playback { get; }

    public FakePlaylists Playlists { get; }

    public FakeAdmin Admin { get; }

    IAuthApi ISabhaClient.Auth => Auth;

    ILibraryApi ISabhaClient.Library => Library;

    IPlaybackApi ISabhaClient.Playback => Playback;

    IPlaylistApi ISabhaClient.Playlists => Playlists;

    IAdminApi ISabhaClient.Admin => Admin;

    public ClientEventHub Events { get; } = new();

    public event EventHandler<ConnectionState>? ConnectionStateChanged;

    public event EventHandler<SessionDto?>? SessionChanged;

    /// <summary>What ConnectAsync does; defaults to completing immediately without changing state.</summary>
    public Func<CancellationToken, Task> OnConnect { get; set; } = _ => Task.CompletedTask;

    public Task ConnectAsync(CancellationToken ct = default) => OnConnect(ct);

    public void SetConnectionState(ConnectionState state)
    {
        State = state;
        ConnectionStateChanged?.Invoke(this, state);
    }

    public void SetSession(SessionDto? session)
    {
        Session = session;
        SessionChanged?.Invoke(this, session);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public IEnumerable<Call> CallsTo(string method) => Calls.Where(c => c.Method == method);

    internal void Record(string method, params object?[] args) => Calls.Add(new Call(method, args));

    public static SessionDto MakeSession(Role role, string user = "user") =>
        new($"token-{user}-{role}", user, role, DateTimeOffset.UtcNow.AddHours(1));
}

public sealed class FakeAuth(FakeSabhaClient c) : IAuthApi
{
    public Func<string, string, SessionDto> OnLogin { get; set; } =
        (user, _) => FakeSabhaClient.MakeSession(Role.Volunteer, user);

    public Task<HelloResponse> HelloAsync(int clientContractVersion, CancellationToken ct = default)
    {
        c.Record(nameof(HelloAsync), clientContractVersion);
        return Task.FromResult(new HelloResponse(ContractVersion.Current, "test", "TEST"));
    }

    public Task<SessionDto> LoginAsync(string userName, string secret, CancellationToken ct = default)
    {
        c.Record(nameof(LoginAsync), userName, secret);
        return Invoke(() => OnLogin(userName, secret));
    }

    public Task<SessionDto> ResumeSessionAsync(string token, CancellationToken ct = default)
    {
        c.Record(nameof(ResumeSessionAsync), token);
        return Task.FromException<SessionDto>(new SabhaMujicException(ErrorCode.Unauthenticated, "no"));
    }

    public Task LogoutAsync(CancellationToken ct = default)
    {
        c.Record(nameof(LogoutAsync));
        return Task.CompletedTask;
    }

    internal static Task<T> Invoke<T>(Func<T> f)
    {
        try
        {
            return Task.FromResult(f());
        }
        catch (Exception ex)
        {
            return Task.FromException<T>(ex);
        }
    }
}

public sealed class FakeLibrary(FakeSabhaClient c) : ILibraryApi
{
    public List<CategoryDto> Categories { get; } = [];

    public List<TrackDto> Tracks { get; } = [];

    /// <summary>When set, GetTracksAsync awaits this before answering (to test out-of-order responses).</summary>
    public Func<TrackQuery, Task>? Gate { get; set; }

    public Task<IReadOnlyList<CategoryDto>> GetCategoriesAsync(CancellationToken ct = default)
    {
        c.Record(nameof(GetCategoriesAsync));
        return Task.FromResult<IReadOnlyList<CategoryDto>>(Categories.ToArray());
    }

    public async Task<PagedResult<TrackDto>> GetTracksAsync(TrackQuery query, CancellationToken ct = default)
    {
        c.Record(nameof(GetTracksAsync), query);
        if (Gate is not null)
        {
            await Gate(query);
        }

        var items = Tracks.Where(t =>
            (query.CategoryId is null || t.CategoryId == query.CategoryId) &&
            (query.Search is null || t.Title.Contains(query.Search, StringComparison.OrdinalIgnoreCase))).ToArray();
        return new PagedResult<TrackDto>(items.Skip(query.Skip).Take(query.Take).ToArray(), items.Length);
    }

    public Task<TrackDto> GetTrackAsync(Guid trackId, CancellationToken ct = default)
    {
        c.Record(nameof(GetTrackAsync), trackId);
        var track = Tracks.FirstOrDefault(t => t.Id == trackId);
        return track is null
            ? Task.FromException<TrackDto>(new SabhaMujicException(ErrorCode.NotFound, "gone"))
            : Task.FromResult(track);
    }

    public Task<IReadOnlyList<TrackDto>> GetTracksByIdsAsync(IReadOnlyList<Guid> trackIds, CancellationToken ct = default)
    {
        c.Record(nameof(GetTracksByIdsAsync), trackIds);
        IReadOnlyList<TrackDto> found = trackIds.Select(id => Tracks.FirstOrDefault(t => t.Id == id)).OfType<TrackDto>().ToList();
        return Task.FromResult(found);
    }

    public TrackDto AddTrack(string title, Guid? categoryId = null, double duration = 200)
    {
        var track = new TrackDto(Guid.NewGuid(), title, "Artist", categoryId, duration, AnalysisStatus.Ready, null, DateTimeOffset.UtcNow);
        Tracks.Add(track);
        return track;
    }
}

public sealed class FakePlayback(FakeSabhaClient c) : IPlaybackApi
{
    public PlaybackStateDto State { get; set; } = new(
        PlaybackStatus.Idle, null, null, null, 0, 0, 1, false, LoopMode.Off, 6, null, 0, DateTimeOffset.UtcNow);

    public QueueDto Queue { get; set; } = new([], -1, 0);

    /// <summary>Result of queue-mutating calls; defaults to <see cref="Queue"/>.</summary>
    public Func<QueueDto>? QueueResult { get; set; }

    public Exception? FailWith { get; set; }

    public Task<PlaybackStateDto> GetStateAsync(CancellationToken ct = default) => R(nameof(GetStateAsync), State);

    public Task<QueueDto> GetQueueAsync(CancellationToken ct = default) => R(nameof(GetQueueAsync), Queue);

    public Task PlayTrackAsync(Guid trackId, CancellationToken ct = default) => R(nameof(PlayTrackAsync), true, trackId);

    public Task PlayQueueItemAsync(Guid queueItemId, CancellationToken ct = default) => R(nameof(PlayQueueItemAsync), true, queueItemId);

    public Task PauseAsync(CancellationToken ct = default) => R(nameof(PauseAsync), true);

    public Task ResumeAsync(CancellationToken ct = default) => R(nameof(ResumeAsync), true);

    public Task StopAsync(CancellationToken ct = default) => R(nameof(StopAsync), true);

    public Task NextAsync(CancellationToken ct = default) => R(nameof(NextAsync), true);

    public Task PreviousAsync(CancellationToken ct = default) => R(nameof(PreviousAsync), true);

    /// <summary>When set, SeekAsync waits for this task (to test events arriving before the reply).</summary>
    public TaskCompletionSource? SeekGate { get; set; }

    public async Task SeekAsync(double positionSeconds, CancellationToken ct = default)
    {
        await R(nameof(SeekAsync), true, positionSeconds);
        if (SeekGate is not null)
        {
            await SeekGate.Task;
        }
    }

    public Task SetVolumeAsync(float volume, CancellationToken ct = default) => R(nameof(SetVolumeAsync), true, volume);

    public Task SetMutedAsync(bool muted, CancellationToken ct = default) => R(nameof(SetMutedAsync), true, muted);

    public Task SetLoopModeAsync(LoopMode mode, CancellationToken ct = default) => R(nameof(SetLoopModeAsync), true, mode);

    public Task SetCrossfadeAsync(double seconds, CancellationToken ct = default) => R(nameof(SetCrossfadeAsync), true, seconds);

    public Task<QueueDto> EnqueueAsync(IReadOnlyList<Guid> trackIds, bool playNext, CancellationToken ct = default) =>
        R(nameof(EnqueueAsync), NextQueue(), trackIds.ToArray(), playNext);

    public Task<QueueDto> EnqueuePlaylistAsync(Guid playlistId, bool replaceQueue, CancellationToken ct = default) =>
        R(nameof(EnqueuePlaylistAsync), NextQueue(), playlistId, replaceQueue);

    public Task<QueueDto> RemoveFromQueueAsync(Guid queueItemId, CancellationToken ct = default) =>
        R(nameof(RemoveFromQueueAsync), NextQueue(), queueItemId);

    public Task<QueueDto> MoveQueueItemAsync(Guid queueItemId, int newIndex, CancellationToken ct = default) =>
        R(nameof(MoveQueueItemAsync), NextQueue(), queueItemId, newIndex);

    public Task<QueueDto> ClearQueueAsync(CancellationToken ct = default) => R(nameof(ClearQueueAsync), NextQueue());

    private QueueDto NextQueue() => QueueResult?.Invoke() ?? Queue;

    private Task<T> R<T>(string method, T result, params object?[] args)
    {
        c.Record(method, args);
        return FailWith is null ? Task.FromResult(result) : Task.FromException<T>(FailWith);
    }
}

public sealed class FakePlaylists(FakeSabhaClient c) : IPlaylistApi
{
    public Dictionary<Guid, PlaylistDto> Items { get; } = [];

    public Task<IReadOnlyList<PlaylistDto>> GetPlaylistsAsync(CancellationToken ct = default)
    {
        c.Record(nameof(GetPlaylistsAsync));
        return Task.FromResult<IReadOnlyList<PlaylistDto>>(Items.Values.ToArray());
    }

    public Task<PlaylistDto> GetPlaylistAsync(Guid playlistId, CancellationToken ct = default)
    {
        c.Record(nameof(GetPlaylistAsync), playlistId);
        return Find(playlistId);
    }

    public Task<PlaylistDto> CreatePlaylistAsync(string name, IReadOnlyList<Guid>? trackIds = null, CancellationToken ct = default)
    {
        c.Record(nameof(CreatePlaylistAsync), name);
        var p = new PlaylistDto(Guid.NewGuid(), name, trackIds?.ToArray() ?? [], DateTimeOffset.UtcNow);
        Items[p.Id] = p;
        return Task.FromResult(p);
    }

    public async Task<PlaylistDto> RenamePlaylistAsync(Guid playlistId, string name, CancellationToken ct = default)
    {
        c.Record(nameof(RenamePlaylistAsync), playlistId, name);
        var p = await Find(playlistId) with { Name = name };
        Items[p.Id] = p;
        return p;
    }

    public async Task<PlaylistDto> SetPlaylistTracksAsync(Guid playlistId, IReadOnlyList<Guid> trackIds, CancellationToken ct = default)
    {
        c.Record(nameof(SetPlaylistTracksAsync), playlistId, trackIds.ToArray());
        var p = await Find(playlistId) with { TrackIds = trackIds.ToArray() };
        Items[p.Id] = p;
        return p;
    }

    public Task DeletePlaylistAsync(Guid playlistId, CancellationToken ct = default)
    {
        c.Record(nameof(DeletePlaylistAsync), playlistId);
        Items.Remove(playlistId);
        return Task.CompletedTask;
    }

    public PlaylistDto Add(string name, params Guid[] trackIds)
    {
        var p = new PlaylistDto(Guid.NewGuid(), name, trackIds, DateTimeOffset.UtcNow);
        Items[p.Id] = p;
        return p;
    }

    private Task<PlaylistDto> Find(Guid id) => Items.TryGetValue(id, out var p)
        ? Task.FromResult(p)
        : Task.FromException<PlaylistDto>(new SabhaMujicException(ErrorCode.NotFound, "gone"));
}

public sealed class FakeAdmin(FakeSabhaClient c) : IAdminApi
{
    private int _uploads;

    /// <summary>Per-file behaviour hooks for the import tests (keyed by file name).</summary>
    public Func<string, long, Guid>? OnBegin { get; set; }

    public Func<Guid, long, byte[], Task>? OnChunk { get; set; }

    public Func<Guid, ImportMetadata, ImportResultDto>? OnComplete { get; set; }

    public List<CategoryDto> Categories { get; } = [];

    public List<UserDto> Users { get; } = [];

    public List<DeviceDto> Devices { get; } = [];

    public Func<Role, PairingCodeDto>? OnPairing { get; set; }

    public Task<Guid> BeginImportAsync(string fileName, long totalBytes, CancellationToken ct = default)
    {
        c.Record(nameof(BeginImportAsync), fileName, totalBytes);
        return FakeAuth.Invoke(() => OnBegin?.Invoke(fileName, totalBytes) ?? new Guid(++_uploads, 0, 0, new byte[8]));
    }

    public async Task UploadChunkAsync(Guid uploadId, long offset, byte[] data, CancellationToken ct = default)
    {
        c.Record(nameof(UploadChunkAsync), uploadId, offset, data);
        if (OnChunk is not null)
        {
            await OnChunk(uploadId, offset, data);
        }
    }

    public Task<ImportResultDto> CompleteImportAsync(Guid uploadId, ImportMetadata metadata, CancellationToken ct = default)
    {
        c.Record(nameof(CompleteImportAsync), uploadId, metadata);
        return FakeAuth.Invoke(() => OnComplete?.Invoke(uploadId, metadata) ?? new ImportResultDto(Guid.NewGuid(), false));
    }

    public Task CancelImportAsync(Guid uploadId, CancellationToken ct = default)
    {
        c.Record(nameof(CancelImportAsync), uploadId);
        return Task.CompletedTask;
    }

    public Task<CategoryDto> CreateCategoryAsync(string name, CancellationToken ct = default)
    {
        c.Record(nameof(CreateCategoryAsync), name);
        var cat = new CategoryDto(Guid.NewGuid(), name, Categories.Count, 0);
        Categories.Add(cat);
        c.Library.Categories.Add(cat);
        return Task.FromResult(cat);
    }

    public Task<CategoryDto> RenameCategoryAsync(Guid categoryId, string name, CancellationToken ct = default)
    {
        c.Record(nameof(RenameCategoryAsync), categoryId, name);
        return Task.FromResult(new CategoryDto(categoryId, name, 0, 0));
    }

    public Task ReorderCategoriesAsync(IReadOnlyList<Guid> orderedCategoryIds, CancellationToken ct = default)
    {
        c.Record(nameof(ReorderCategoriesAsync), orderedCategoryIds.ToArray());
        return Task.CompletedTask;
    }

    public Task DeleteCategoryAsync(Guid categoryId, CancellationToken ct = default)
    {
        c.Record(nameof(DeleteCategoryAsync), categoryId);
        return Task.CompletedTask;
    }

    public Task<TrackDto> UpdateTrackAsync(TrackUpdateDto update, CancellationToken ct = default)
    {
        c.Record(nameof(UpdateTrackAsync), update);
        var t = c.Library.Tracks.First(t => t.Id == update.TrackId) with
        {
            Title = update.Title,
            Artist = update.Artist,
            CategoryId = update.CategoryId,
        };
        return Task.FromResult(t);
    }

    public Task DeleteTrackAsync(Guid trackId, CancellationToken ct = default)
    {
        c.Record(nameof(DeleteTrackAsync), trackId);
        return Task.CompletedTask;
    }

    public Task<TrackDto> SetLoopOverrideAsync(Guid trackId, LoopRegionDto? region, CancellationToken ct = default)
    {
        c.Record(nameof(SetLoopOverrideAsync), trackId, region);
        return Task.FromResult(c.Library.Tracks.First(t => t.Id == trackId) with { Loop = region });
    }

    public Task ReanalyzeTrackAsync(Guid trackId, CancellationToken ct = default)
    {
        c.Record(nameof(ReanalyzeTrackAsync), trackId);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<UserDto>> GetUsersAsync(CancellationToken ct = default)
    {
        c.Record(nameof(GetUsersAsync));
        return Task.FromResult<IReadOnlyList<UserDto>>(Users.ToArray());
    }

    public Task<UserDto> CreateUserAsync(string userName, Role role, string secret, CancellationToken ct = default)
    {
        c.Record(nameof(CreateUserAsync), userName, role, secret);
        var u = new UserDto(Guid.NewGuid(), userName, role, DateTimeOffset.UtcNow);
        Users.Add(u);
        return Task.FromResult(u);
    }

    public Task SetUserSecretAsync(Guid userId, string secret, CancellationToken ct = default)
    {
        c.Record(nameof(SetUserSecretAsync), userId, secret);
        return Task.CompletedTask;
    }

    public Task DeleteUserAsync(Guid userId, CancellationToken ct = default)
    {
        c.Record(nameof(DeleteUserAsync), userId);
        Users.RemoveAll(u => u.Id == userId);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<DeviceDto>> GetDevicesAsync(CancellationToken ct = default)
    {
        c.Record(nameof(GetDevicesAsync));
        return Task.FromResult<IReadOnlyList<DeviceDto>>(Devices.ToArray());
    }

    public Task<PairingCodeDto> CreatePairingCodeAsync(Role role, CancellationToken ct = default)
    {
        c.Record(nameof(CreatePairingCodeAsync), role);
        return Task.FromResult(OnPairing?.Invoke(role) ??
            new PairingCodeDto("123456", "https://pc.local:8443/pair#123456", "http://pc.local:8080/", role, DateTimeOffset.UtcNow.AddMinutes(5)));
    }

    public Task RevokeDeviceAsync(Guid deviceId, CancellationToken ct = default)
    {
        c.Record(nameof(RevokeDeviceAsync), deviceId);
        return Task.CompletedTask;
    }
}
