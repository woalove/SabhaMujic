namespace SabhaMujic.Contracts;

// RPC surface exposed by the service. The desktop uses these over the named pipe (StreamJsonRpc);
// the remote uses equivalent HTTP endpoints/SignalR methods backed by the same application services.
// Pipe connections are stateful: LoginAsync binds the session to the connection.
// Limits: chunk sizes and upload sizes are enforced server-side (see AdminLimits).

public interface IAuthApi
{
    /// <summary>Handshake. Throws <see cref="ErrorCode.IncompatibleClient"/> when contract versions differ.</summary>
    Task<HelloResponse> HelloAsync(int clientContractVersion, CancellationToken ct = default);

    Task<SessionDto> LoginAsync(string userName, string secret, CancellationToken ct = default);

    /// <summary>Re-attach an existing session (e.g. after the desktop reconnects).</summary>
    Task<SessionDto> ResumeSessionAsync(string token, CancellationToken ct = default);

    Task LogoutAsync(CancellationToken ct = default);
}

public interface ILibraryApi
{
    Task<IReadOnlyList<CategoryDto>> GetCategoriesAsync(CancellationToken ct = default);

    Task<PagedResult<TrackDto>> GetTracksAsync(TrackQuery query, CancellationToken ct = default);

    Task<TrackDto> GetTrackAsync(Guid trackId, CancellationToken ct = default);

    /// <summary>
    /// Batch lookup (e.g. to show titles for queue/playlist items). Unknown ids are omitted; order follows the request.
    /// At most <see cref="LibraryLimits.MaxBatchLookup"/> ids.
    /// </summary>
    Task<IReadOnlyList<TrackDto>> GetTracksByIdsAsync(IReadOnlyList<Guid> trackIds, CancellationToken ct = default);
}

public static class LibraryLimits
{
    public const int MaxBatchLookup = 500;
    public const int MaxPageSize = 500;
}

public interface IPlaybackApi
{
    Task<PlaybackStateDto> GetStateAsync(CancellationToken ct = default);

    Task<QueueDto> GetQueueAsync(CancellationToken ct = default);

    /// <summary>Insert the track after the current item and start it (crossfading if something is playing).</summary>
    Task PlayTrackAsync(Guid trackId, CancellationToken ct = default);

    Task PlayQueueItemAsync(Guid queueItemId, CancellationToken ct = default);

    Task PauseAsync(CancellationToken ct = default);

    /// <summary>
    /// Resumes when Paused. When Stopped/Idle/Error, starts the current queue item (or the first item);
    /// a no-op when the queue is empty.
    /// </summary>
    Task ResumeAsync(CancellationToken ct = default);

    Task StopAsync(CancellationToken ct = default);

    Task NextAsync(CancellationToken ct = default);

    Task PreviousAsync(CancellationToken ct = default);

    Task SeekAsync(double positionSeconds, CancellationToken ct = default);

    /// <summary>Volume in [0, 1].</summary>
    Task SetVolumeAsync(float volume, CancellationToken ct = default);

    Task SetMutedAsync(bool muted, CancellationToken ct = default);

    Task SetLoopModeAsync(LoopMode mode, CancellationToken ct = default);

    /// <summary>Crossfade between queue items in seconds, [0, 12].</summary>
    Task SetCrossfadeAsync(double seconds, CancellationToken ct = default);

    Task<QueueDto> EnqueueAsync(IReadOnlyList<Guid> trackIds, bool playNext, CancellationToken ct = default);

    Task<QueueDto> EnqueuePlaylistAsync(Guid playlistId, bool replaceQueue, CancellationToken ct = default);

    Task<QueueDto> RemoveFromQueueAsync(Guid queueItemId, CancellationToken ct = default);

    /// <summary><paramref name="newIndex"/> is the item's final position after the move (clamped to the queue bounds).</summary>
    Task<QueueDto> MoveQueueItemAsync(Guid queueItemId, int newIndex, CancellationToken ct = default);

    /// <summary>Removes all items except the one currently playing.</summary>
    Task<QueueDto> ClearQueueAsync(CancellationToken ct = default);
}

public interface IPlaylistApi
{
    Task<IReadOnlyList<PlaylistDto>> GetPlaylistsAsync(CancellationToken ct = default);

    Task<PlaylistDto> GetPlaylistAsync(Guid playlistId, CancellationToken ct = default);

    Task<PlaylistDto> CreatePlaylistAsync(string name, IReadOnlyList<Guid>? trackIds = null, CancellationToken ct = default);

    Task<PlaylistDto> RenamePlaylistAsync(Guid playlistId, string name, CancellationToken ct = default);

    Task<PlaylistDto> SetPlaylistTracksAsync(Guid playlistId, IReadOnlyList<Guid> trackIds, CancellationToken ct = default);

    Task DeletePlaylistAsync(Guid playlistId, CancellationToken ct = default);
}

public interface IAdminApi
{
    // Import (chunked upload: the client never writes into the protected store itself).
    Task<Guid> BeginImportAsync(string fileName, long totalBytes, CancellationToken ct = default);

    Task UploadChunkAsync(Guid uploadId, long offset, byte[] data, CancellationToken ct = default);

    Task<ImportResultDto> CompleteImportAsync(Guid uploadId, ImportMetadata metadata, CancellationToken ct = default);

    Task CancelImportAsync(Guid uploadId, CancellationToken ct = default);

    // Library management.
    Task<CategoryDto> CreateCategoryAsync(string name, CancellationToken ct = default);

    Task<CategoryDto> RenameCategoryAsync(Guid categoryId, string name, CancellationToken ct = default);

    Task ReorderCategoriesAsync(IReadOnlyList<Guid> orderedCategoryIds, CancellationToken ct = default);

    /// <summary>Deletes the category; its tracks become uncategorized.</summary>
    Task DeleteCategoryAsync(Guid categoryId, CancellationToken ct = default);

    Task<TrackDto> UpdateTrackAsync(TrackUpdateDto update, CancellationToken ct = default);

    Task DeleteTrackAsync(Guid trackId, CancellationToken ct = default);

    /// <summary>Set a manual loop region, or pass null to return to the automatic analysis.</summary>
    Task<TrackDto> SetLoopOverrideAsync(Guid trackId, LoopRegionDto? region, CancellationToken ct = default);

    Task ReanalyzeTrackAsync(Guid trackId, CancellationToken ct = default);

    // Accounts.
    Task<IReadOnlyList<UserDto>> GetUsersAsync(CancellationToken ct = default);

    Task<UserDto> CreateUserAsync(string userName, Role role, string secret, CancellationToken ct = default);

    Task SetUserSecretAsync(Guid userId, string secret, CancellationToken ct = default);

    Task DeleteUserAsync(Guid userId, CancellationToken ct = default);

    // Remote devices.
    Task<IReadOnlyList<DeviceDto>> GetDevicesAsync(CancellationToken ct = default);

    Task<PairingCodeDto> CreatePairingCodeAsync(Role role, CancellationToken ct = default);

    Task RevokeDeviceAsync(Guid deviceId, CancellationToken ct = default);
}

/// <summary>
/// Server-to-client notifications. Desktop clients register an implementation as a StreamJsonRpc target;
/// the SignalR hub invokes client methods with the same names.
/// </summary>
/// Delivery rules: the service pushes <c>PlaybackStateChanged</c> on every status change, track change, seek,
/// Smart Loop wrap, and setting change, plus a periodic heartbeat (about every 5 s) while playing; clients
/// interpolate position between pushes using <see cref="PlaybackStateDto.Timestamp"/>.
public interface IClientEvents
{
    void PlaybackStateChanged(PlaybackStateDto state);

    void QueueChanged(QueueDto queue);

    void LibraryChanged(LibraryChangedDto change);

    void ImportProgress(ImportProgressDto progress);

    /// <summary>The session was revoked or expired; the client must log in again.</summary>
    void SessionEnded(string reason);
}

public static class AdminLimits
{
    public const long MaxUploadBytes = 1L * 1024 * 1024 * 1024; // 1 GiB
    public const int MaxChunkBytes = 1024 * 1024;               // 1 MiB
    public const int MaxConcurrentUploadsPerSession = 2;

    public static readonly IReadOnlySet<string> AllowedExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".mp3", ".wav", ".m4a", ".aac", ".flac", ".wma" };
}
