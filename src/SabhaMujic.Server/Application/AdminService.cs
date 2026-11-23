using System.Globalization;
using Microsoft.Extensions.Logging;
using SabhaMujic.Contracts;
using SabhaMujic.Core.Security;
using SabhaMujic.Data.Repositories;
using SabhaMujic.Data.Storage;

namespace SabhaMujic.Server.Application;

/// <summary>Library, account and device management (admin-only per the role matrix). Every change is audited.</summary>
public sealed class AdminService
{
    private readonly ICategoryRepository _categories;
    private readonly ITrackRepository _tracks;
    private readonly ITrackAnalysisRepository _analysis;
    private readonly IUserRepository _users;
    private readonly ISessionRepository _sessions;
    private readonly IDeviceRepository _devices;
    private readonly ProtectedStore _store;
    private readonly IPlaybackBackend _playback;
    private readonly IAnalysisQueue _analysisQueue;
    private readonly IRemoteUrlProvider _urls;
    private readonly AuthService _auth;
    private readonly EventBroadcaster _events;
    private readonly AuditWriter _audit;
    private readonly ServerOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger _logger;

    public AdminService(ICategoryRepository categories, ITrackRepository tracks, ITrackAnalysisRepository analysis, IUserRepository users,
        ISessionRepository sessions, IDeviceRepository devices, ProtectedStore store,
        IPlaybackBackend playback, IAnalysisQueue analysisQueue, IRemoteUrlProvider urls, AuthService auth, EventBroadcaster events,
        AuditWriter audit, ServerOptions options, TimeProvider clock, ILogger<AdminService> logger)
    {
        _categories = categories;
        _tracks = tracks;
        _analysis = analysis;
        _users = users;
        _sessions = sessions;
        _devices = devices;
        _store = store;
        _playback = playback;
        _analysisQueue = analysisQueue;
        _urls = urls;
        _auth = auth;
        _events = events;
        _audit = audit;
        _options = options;
        _clock = clock;
        _logger = logger;
    }

    // ------------------------------------------------------------------ categories

    public async Task<CategoryDto> CreateCategoryAsync(CallerContext caller, string name, CancellationToken ct)
    {
        var category = await _categories.CreateAsync(name, ct).ConfigureAwait(false);
        await CategoriesChangedAsync(caller, "category.create", category.Id, category.Name).ConfigureAwait(false);
        return category;
    }

    public async Task<CategoryDto> RenameCategoryAsync(CallerContext caller, Guid categoryId, string name, CancellationToken ct)
    {
        var category = await _categories.RenameAsync(categoryId, name, ct).ConfigureAwait(false) ?? throw Errors.CategoryNotFound();
        await CategoriesChangedAsync(caller, "category.rename", categoryId, category.Name).ConfigureAwait(false);
        return category;
    }

    public async Task ReorderCategoriesAsync(CallerContext caller, IReadOnlyList<Guid> orderedCategoryIds, CancellationToken ct)
    {
        Errors.RequireNotNull(orderedCategoryIds, "orderedCategoryIds");
        await _categories.ReorderAsync(orderedCategoryIds, ct).ConfigureAwait(false);
        await CategoriesChangedAsync(caller, "category.reorder", null, null).ConfigureAwait(false);
    }

    /// <summary>
    /// Sets the category's tracks (other tags of those tracks stay). Every track whose tags changed gets a
    /// <see cref="LibraryChangeKind.TrackUpdated"/>, like <see cref="UpdateTrackAsync"/>, plus one
    /// <see cref="LibraryChangeKind.CategoriesChanged"/> for the new count. Tags do not affect playback, so the engine is
    /// not involved.
    /// </summary>
    public async Task<CategoryDto> SetCategoryTracksAsync(CallerContext caller, Guid categoryId, IReadOnlyList<Guid> trackIds, CancellationToken ct)
    {
        Errors.RequireNotNull(trackIds, "trackIds");
        var (category, changed) = await _categories.SetTracksAsync(categoryId, trackIds, ct).ConfigureAwait(false)
            ?? throw Errors.CategoryNotFound();
        foreach (var trackId in changed)
        {
            _events.PublishLibraryChanged(new LibraryChangedDto(LibraryChangeKind.TrackUpdated, trackId));
        }

        await CategoriesChangedAsync(caller, "category.set_tracks", categoryId, $"tracks={category.TrackCount} changed={changed.Count}")
            .ConfigureAwait(false);
        return category;
    }

    public async Task DeleteCategoryAsync(CallerContext caller, Guid categoryId, CancellationToken ct)
    {
        if (!await _categories.DeleteAsync(categoryId, ct).ConfigureAwait(false))
        {
            throw Errors.CategoryNotFound();
        }

        await CategoriesChangedAsync(caller, "category.delete", categoryId, null).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ tracks

    public async Task<TrackDto> UpdateTrackAsync(CallerContext caller, TrackUpdateDto update, CancellationToken ct)
    {
        Errors.RequireNotNull(update, "update");
        var track = await _tracks.UpdateMetadataAsync(update, ct).ConfigureAwait(false) ?? throw Errors.TrackNotFound();
        await _playback.OnTrackChangedAsync(track.Id, LibraryChangeKind.TrackUpdated, ct).ConfigureAwait(false);
        _events.PublishLibraryChanged(new LibraryChangedDto(LibraryChangeKind.TrackUpdated, track.Id));
        await _audit.WriteAsync(caller, "track.update", "track:" + track.Id, track.Title).ConfigureAwait(false);
        return track;
    }

    public async Task DeleteTrackAsync(CallerContext caller, Guid trackId, CancellationToken ct)
    {
        var fileKey = await _tracks.DeleteAsync(trackId, ct).ConfigureAwait(false) ?? throw Errors.TrackNotFound();
        // Past this point the track is gone; finish the clean-up even if the caller disconnects.
        await _playback.OnTrackChangedAsync(trackId, LibraryChangeKind.TrackRemoved, CancellationToken.None).ConfigureAwait(false);
        try
        {
            await _store.DeleteAsync(fileKey, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not delete the stored file of deleted track {TrackId}.", trackId);
        }

        _events.PublishLibraryChanged(new LibraryChangedDto(LibraryChangeKind.TrackRemoved, trackId));
        await _audit.WriteAsync(caller, "track.delete", "track:" + trackId).ConfigureAwait(false);
    }

    public async Task<TrackDto> SetLoopOverrideAsync(CallerContext caller, Guid trackId, LoopRegionDto? region, CancellationToken ct)
    {
        var loop = region is null ? null : new LoopOverride(region.StartSeconds, region.EndSeconds, region.CrossfadeSeconds);
        await _analysis.SetLoopOverrideAsync(trackId, loop, caller.UserId, ct).ConfigureAwait(false);
        var track = await _tracks.GetAsync(trackId, ct).ConfigureAwait(false) ?? throw Errors.TrackNotFound();
        await _playback.OnTrackChangedAsync(trackId, LibraryChangeKind.TrackUpdated, ct).ConfigureAwait(false);
        _events.PublishLibraryChanged(new LibraryChangedDto(LibraryChangeKind.TrackUpdated, trackId));
        await _audit.WriteAsync(caller, region is null ? "track.loop_override.clear" : "track.loop_override.set", "track:" + trackId,
            region is null ? null : string.Create(CultureInfo.InvariantCulture,
                $"start={region.StartSeconds} end={region.EndSeconds} crossfade={region.CrossfadeSeconds}")).ConfigureAwait(false);
        return track;
    }

    public async Task ReanalyzeTrackAsync(CallerContext caller, Guid trackId, CancellationToken ct)
    {
        if (await _tracks.GetAsync(trackId, ct).ConfigureAwait(false) is null)
        {
            throw Errors.TrackNotFound();
        }

        await _analysis.UpsertAsync(new TrackAnalysis(trackId, AnalysisStatus.Pending, 0), ct).ConfigureAwait(false);
        _analysisQueue.Enqueue(trackId);
        _events.PublishLibraryChanged(new LibraryChangedDto(LibraryChangeKind.AnalysisUpdated, trackId));
        await _audit.WriteAsync(caller, "track.reanalyze", "track:" + trackId).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ accounts

    public Task<IReadOnlyList<UserDto>> GetUsersAsync(CallerContext caller, CancellationToken ct) => _users.ListAsync(ct);

    /// <summary>Creates an administrator (contract v2: volunteers need no account, so <see cref="Role.Volunteer"/> is refused).</summary>
    public async Task<UserDto> CreateUserAsync(CallerContext caller, string userName, Role role, string secret, CancellationToken ct)
    {
        if (role != Role.Admin)
        {
            throw new SabhaMujicException(ErrorCode.InvalidArgument,
                "Only administrator accounts can be created: volunteers use the SabhaMujic app without signing in.");
        }

        var user = await _users.CreateAsync(userName, role, _auth.HashSecret(secret), ct).ConfigureAwait(false);
        _events.PublishLibraryChanged(new LibraryChangedDto(LibraryChangeKind.UsersChanged, user.Id));
        await _audit.WriteAsync(caller, "user.create", "user:" + user.UserName, "role=" + role).ConfigureAwait(false);
        return user.ToDto();
    }

    /// <summary>
    /// Changes a password. Every session of that user is revoked (they must sign in again), except the caller's own
    /// session when users change their own password.
    /// </summary>
    public async Task SetUserSecretAsync(CallerContext caller, Guid userId, string secret, CancellationToken ct)
    {
        var hash = _auth.HashSecret(secret);
        var user = await _users.GetByIdAsync(userId, ct).ConfigureAwait(false) ?? throw Errors.UserNotFound();
        if (!await _users.UpdateSecretHashAsync(userId, hash, ct).ConfigureAwait(false))
        {
            throw Errors.UserNotFound();
        }

        if (caller.UserId == userId && Guid.TryParse(caller.SessionId, out var ownSession))
        {
            await _sessions.RevokeAllForUserExceptAsync(userId, ownSession, ct).ConfigureAwait(false);
            _events.EndSessions(c => c.UserId == userId && c.SessionId != caller.SessionId, "Your password was changed. Please sign in again.");
        }
        else
        {
            await _sessions.RevokeAllForUserAsync(userId, ct).ConfigureAwait(false);
            _events.EndSessions(c => c.UserId == userId, "Your password was changed. Please sign in again.");
        }

        await _audit.WriteAsync(caller, "user.set_secret", "user:" + user.UserName).ConfigureAwait(false);
    }

    public async Task DeleteUserAsync(CallerContext caller, Guid userId, CancellationToken ct)
    {
        if (caller.UserId == userId)
        {
            throw new SabhaMujicException(ErrorCode.Conflict, "You cannot delete your own account.");
        }

        var user = await _users.GetByIdAsync(userId, ct).ConfigureAwait(false) ?? throw Errors.UserNotFound();
        if (!await _users.DeleteAsync(userId, preserveLastAdmin: true, ct).ConfigureAwait(false))
        {
            throw Errors.UserNotFound();
        }

        _events.EndSessions(c => c.UserId == userId, "Your account was removed.");
        _events.PublishLibraryChanged(new LibraryChangedDto(LibraryChangeKind.UsersChanged, userId));
        await _audit.WriteAsync(caller, "user.delete", "user:" + user.UserName).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ devices

    public Task<IReadOnlyList<DeviceDto>> GetDevicesAsync(CallerContext caller, CancellationToken ct) => _devices.ListAsync(false, ct);

    public Task<PhoneSetupInfoDto> GetPhoneSetupInfoAsync(CallerContext caller, CancellationToken ct) =>
        Task.FromResult(new PhoneSetupInfoDto(_urls.GetBootstrapUrl(), _urls.GetRemoteUrl(), _urls.CaFingerprint, _urls.IsRemoteRunning));

    public async Task RevokeDeviceAsync(CallerContext caller, Guid deviceId, CancellationToken ct)
    {
        var device = await _devices.GetAsync(deviceId, ct).ConfigureAwait(false) ?? throw Errors.DeviceNotFound();
        await _devices.RevokeAsync(deviceId, ct).ConfigureAwait(false);
        _events.EndSessions(c => c.DeviceId == deviceId, "This device was removed by an administrator.");
        _events.PublishLibraryChanged(new LibraryChangedDto(LibraryChangeKind.DevicesChanged, deviceId));
        await _audit.WriteAsync(caller, "device.revoke", "device:" + deviceId, device.Name).ConfigureAwait(false);
    }

    private async Task CategoriesChangedAsync(CallerContext caller, string action, Guid? categoryId, string? details)
    {
        _events.PublishLibraryChanged(new LibraryChangedDto(LibraryChangeKind.CategoriesChanged, categoryId));
        await _audit.WriteAsync(caller, action, categoryId is null ? null : "category:" + categoryId, details).ConfigureAwait(false);
    }
}
