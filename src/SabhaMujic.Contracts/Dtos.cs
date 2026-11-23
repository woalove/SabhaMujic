namespace SabhaMujic.Contracts;

// Times are expressed in seconds (double) so the same DTOs serialize naturally for the JS remote.

public sealed record HelloResponse(int ContractVersion, string ServerVersion, string MachineName);

public sealed record SessionDto(string Token, string UserName, Role Role, DateTimeOffset ExpiresAt);

public sealed record CategoryDto(Guid Id, string Name, int SortOrder, int TrackCount);

public sealed record LoopRegionDto(
    double StartSeconds,
    double EndSeconds,
    double CrossfadeSeconds,
    double Confidence,
    bool IsManualOverride);

/// <summary>A library track. <see cref="CategoryIds"/> are its tags (categories), in category sort order.</summary>
public sealed record TrackDto(
    Guid Id,
    string Title,
    string? Artist,
    IReadOnlyList<Guid> CategoryIds,
    double DurationSeconds,
    AnalysisStatus Analysis,
    LoopRegionDto? Loop,
    DateTimeOffset AddedAt);

/// <summary>Track search. <see cref="CategoryId"/> keeps the tracks tagged with that category.</summary>
public sealed record TrackQuery(Guid? CategoryId = null, string? Search = null, int Skip = 0, int Take = 200);

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Total);

public sealed record PlaylistDto(Guid Id, string Name, IReadOnlyList<Guid> TrackIds, DateTimeOffset UpdatedAt);

public sealed record QueueItemDto(Guid QueueItemId, Guid TrackId);

/// <summary>Queue snapshot. <see cref="Revision"/> increases on every change so clients can drop stale events.</summary>
public sealed record QueueDto(IReadOnlyList<QueueItemDto> Items, int CurrentIndex, long Revision)
{
    /// <summary>Changes every time the service starts. Clients reset revision tracking when it changes.</summary>
    public Guid ServerEpoch { get; init; }
}

public sealed record PlaybackStateDto(
    PlaybackStatus Status,
    Guid? CurrentTrackId,
    Guid? CurrentQueueItemId,
    Guid? NextTrackId,
    double PositionSeconds,
    double DurationSeconds,
    float Volume,
    bool Muted,
    LoopMode LoopMode,
    double CrossfadeSeconds,
    string? ErrorMessage,
    long Revision,
    DateTimeOffset Timestamp)
{
    /// <summary>Changes every time the service starts. Clients reset revision tracking when it changes.</summary>
    public Guid ServerEpoch { get; init; }
}

public sealed record UserDto(Guid Id, string UserName, Role Role, DateTimeOffset CreatedAt);

/// <summary>
/// A phone remembered at remote sign-in ("Remember this phone"). <see cref="PairedAt"/> is when it was remembered;
/// revoking it signs the phone out.
/// </summary>
public sealed record DeviceDto(Guid Id, string Name, Role Role, DateTimeOffset PairedAt, DateTimeOffset? LastSeenAt);

/// <summary>
/// What the desktop "Set up a phone" panel shows. The phone opens <see cref="BootstrapUrl"/> (plain HTTP on the local
/// network) to install the SabhaMujic certificate and read the instructions, then <see cref="RemoteUrl"/> (HTTPS) and
/// signs in as an administrator.
/// </summary>
/// <param name="BootstrapUrl">The setup page (also as a QR code on the desktop).</param>
/// <param name="RemoteUrl">The remote's HTTPS address.</param>
/// <param name="CaFingerprint">
/// SHA-256 fingerprint of the service's local CA (colon-separated upper-case hex), to compare with the one on the phone's
/// setup page. Null when the remote is not running.
/// </param>
/// <param name="IsRemoteRunning">False when the remote is disabled or failed to start (the URLs are then informational).</param>
public sealed record PhoneSetupInfoDto(string BootstrapUrl, string RemoteUrl, string? CaFingerprint, bool IsRemoteRunning);

/// <summary>Metadata for a new track. <see cref="CategoryIds"/> are its tags (null or empty: none).</summary>
public sealed record ImportMetadata(string Title, string? Artist, IReadOnlyList<Guid>? CategoryIds);

public sealed record ImportResultDto(Guid TrackId, bool WasDuplicate);

public sealed record ImportProgressDto(Guid UploadId, string FileName, long BytesReceived, long TotalBytes, string Stage);

/// <summary>Values used for <see cref="ImportProgressDto.Stage"/>.</summary>
public static class ImportStages
{
    public const string Uploading = "uploading";
    public const string Validating = "validating";
    public const string Storing = "storing";
    public const string Analyzing = "analyzing";
    public const string Done = "done";
    public const string Failed = "failed";
}

/// <summary>New title, artist and tags. <see cref="CategoryIds"/> replaces the track's tags (empty: none).</summary>
public sealed record TrackUpdateDto(Guid TrackId, string Title, string? Artist, IReadOnlyList<Guid> CategoryIds);

public enum LibraryChangeKind
{
    TrackAdded = 0,
    TrackUpdated = 1,
    TrackRemoved = 2,
    CategoriesChanged = 3,
    PlaylistsChanged = 4,
    AnalysisUpdated = 5,
    /// <summary>Admin-only data; only delivered to admin sessions.</summary>
    UsersChanged = 6,
    /// <summary>Admin-only data; only delivered to admin sessions.</summary>
    DevicesChanged = 7,
}

public sealed record LibraryChangedDto(LibraryChangeKind Kind, Guid? EntityId);
