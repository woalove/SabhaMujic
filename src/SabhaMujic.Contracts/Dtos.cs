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

public sealed record TrackDto(
    Guid Id,
    string Title,
    string? Artist,
    Guid? CategoryId,
    double DurationSeconds,
    AnalysisStatus Analysis,
    LoopRegionDto? Loop,
    DateTimeOffset AddedAt);

public sealed record TrackQuery(Guid? CategoryId = null, string? Search = null, int Skip = 0, int Take = 200);

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Total);

public sealed record PlaylistDto(Guid Id, string Name, IReadOnlyList<Guid> TrackIds, DateTimeOffset UpdatedAt);

public sealed record QueueItemDto(Guid QueueItemId, Guid TrackId);

/// <summary>Queue snapshot. <see cref="Revision"/> increases on every change so clients can drop stale events.</summary>
public sealed record QueueDto(IReadOnlyList<QueueItemDto> Items, int CurrentIndex, long Revision);

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
    DateTimeOffset Timestamp);

public sealed record UserDto(Guid Id, string UserName, Role Role, DateTimeOffset CreatedAt);

public sealed record DeviceDto(Guid Id, string Name, Role Role, DateTimeOffset PairedAt, DateTimeOffset? LastSeenAt);

/// <summary>One-time pairing code shown by the desktop as text + QR (<see cref="PairingUrl"/>).</summary>
public sealed record PairingCodeDto(string Code, string PairingUrl, string BootstrapUrl, Role Role, DateTimeOffset ExpiresAt);

public sealed record ImportMetadata(string Title, string? Artist, Guid? CategoryId);

public sealed record ImportResultDto(Guid TrackId, bool WasDuplicate);

public sealed record ImportProgressDto(Guid UploadId, string FileName, long BytesReceived, long TotalBytes, string Stage);

public sealed record TrackUpdateDto(Guid TrackId, string Title, string? Artist, Guid? CategoryId);

public enum LibraryChangeKind
{
    TrackAdded = 0,
    TrackUpdated = 1,
    TrackRemoved = 2,
    CategoriesChanged = 3,
    PlaylistsChanged = 4,
    AnalysisUpdated = 5,
}

public sealed record LibraryChangedDto(LibraryChangeKind Kind, Guid? EntityId);
