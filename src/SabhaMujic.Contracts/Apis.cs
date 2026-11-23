namespace SabhaMujic.Contracts;

// RPC surface exposed by the service. The desktop uses these over the named pipe (StreamJsonRpc);
// the remote uses equivalent HTTP endpoints/SignalR methods backed by the same application services.
// Pipe connections are stateful: LoginAsync binds the session to the connection.
// Access (contract v2): on the desktop pipe anyone may use the player (library, playback, queue, playlists) without
// signing in; administration needs an administrator sign-in, which ends after 15 minutes without activity. The iPhone
// remote is for administrators only: every remote operation needs an administrator session.
// Limits: chunk sizes and upload sizes are enforced server-side (see AdminLimits).

public interface IAuthApi
{
    /// <summary>Handshake. Throws <see cref="ErrorCode.IncompatibleClient"/> when contract versions differ.</summary>
    Task<HelloResponse> HelloAsync(int clientContractVersion, CancellationToken ct = default);

    /// <summary>
    /// Signs in with a user name and password (desktop: binds the session to the connection, and it ends after 15 minutes
    /// without activity). <see cref="ErrorCode.Unauthenticated"/> for a wrong name or password,
    /// <see cref="ErrorCode.RateLimited"/> after repeated failures.
    /// </summary>
    Task<SessionDto> LoginAsync(string userName, string secret, CancellationToken ct = default);

    /// <summary>Re-attach an existing session (e.g. after the desktop reconnects).</summary>
    Task<SessionDto> ResumeSessionAsync(string token, CancellationToken ct = default);

    Task LogoutAsync(CancellationToken ct = default);

    /// <summary>True while no administrator accounts exist (fresh install). Anonymous.</summary>
    Task<bool> IsSetupRequiredAsync(CancellationToken ct = default);

    /// <summary>
    /// Creates the first administrator account (password of at least 8 characters) and signs in as it. Only succeeds
    /// while no accounts exist AND the caller is a local Windows administrator connected over the desktop pipe (never
    /// from the remote). Otherwise <see cref="ErrorCode.Forbidden"/> / <see cref="ErrorCode.Conflict"/>.
    /// </summary>
    Task<SessionDto> CompleteInitialSetupAsync(string adminUserName, string secret, CancellationToken ct = default);

    /// <summary>
    /// Local recovery when every administrator password is forgotten. Same gate as first-run setup: the caller must be a
    /// local Windows administrator running elevated on the desktop pipe (never the remote), otherwise
    /// <see cref="ErrorCode.Forbidden"/>. Sets a new password (at least 8 characters) for the administrator
    /// <paramref name="adminUserName"/> (all of that account's sessions are signed out), or creates a new administrator
    /// with that name when no account has it, and signs in as it. <see cref="ErrorCode.Conflict"/> when the name belongs
    /// to a non-administrator account; <see cref="ErrorCode.RateLimited"/> after repeated attempts. Audited.
    /// </summary>
    Task<SessionDto> ResetAdministratorAsync(string adminUserName, string newSecret, CancellationToken ct = default);
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

    /// <summary>
    /// Sets which tracks have this category (tag): exactly <paramref name="trackIds"/> afterwards. Each track's other
    /// categories are left alone. <see cref="ErrorCode.NotFound"/> for an unknown category,
    /// <see cref="ErrorCode.InvalidArgument"/> when a track id is unknown (nothing changes then).
    /// </summary>
    Task<CategoryDto> SetCategoryTracksAsync(Guid categoryId, IReadOnlyList<Guid> trackIds, CancellationToken ct = default);

    /// <summary>Deletes the category (tag); it is removed from every track that had it.</summary>
    Task DeleteCategoryAsync(Guid categoryId, CancellationToken ct = default);

    Task<TrackDto> UpdateTrackAsync(TrackUpdateDto update, CancellationToken ct = default);

    Task DeleteTrackAsync(Guid trackId, CancellationToken ct = default);

    /// <summary>Set a manual loop region, or pass null to return to the automatic analysis.</summary>
    Task<TrackDto> SetLoopOverrideAsync(Guid trackId, LoopRegionDto? region, CancellationToken ct = default);

    Task ReanalyzeTrackAsync(Guid trackId, CancellationToken ct = default);

    // Accounts (administrators; volunteers need no account in contract v2).
    Task<IReadOnlyList<UserDto>> GetUsersAsync(CancellationToken ct = default);

    /// <summary>
    /// Creates an administrator with a password of at least 8 characters. <paramref name="role"/> must be
    /// <see cref="Role.Admin"/> (<see cref="ErrorCode.InvalidArgument"/> otherwise).
    /// </summary>
    Task<UserDto> CreateUserAsync(string userName, Role role, string secret, CancellationToken ct = default);

    /// <summary>Sets a new password (at least 8 characters); the account's other sessions are signed out.</summary>
    Task SetUserSecretAsync(Guid userId, string secret, CancellationToken ct = default);

    Task DeleteUserAsync(Guid userId, CancellationToken ct = default);

    // Phones (remote).
    /// <summary>Phones remembered at remote sign-in ("Remember this phone").</summary>
    Task<IReadOnlyList<DeviceDto>> GetDevicesAsync(CancellationToken ct = default);

    /// <summary>What the desktop's "Set up a phone" panel shows: setup page, remote address, CA fingerprint.</summary>
    Task<PhoneSetupInfoDto> GetPhoneSetupInfoAsync(CancellationToken ct = default);

    /// <summary>Forgets a remembered phone; it is signed out at once.</summary>
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

    /// <summary>
    /// The session was revoked or expired (a desktop administrator session after 15 minutes without activity). The
    /// connection continues signed out: on the desktop that is open (volunteer) use; the remote must sign in again.
    /// </summary>
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
