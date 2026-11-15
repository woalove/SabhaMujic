using SabhaMujic.Contracts;
using SabhaMujic.Core.Playback;
using SabhaMujic.Core.Security;
using SabhaMujic.Data.Repositories;

namespace SabhaMujic.Server.Application;

/// <summary>Read-only library browsing (any signed-in user).</summary>
public sealed class LibraryService(ICategoryRepository categories, ITrackRepository tracks)
{
    public Task<IReadOnlyList<CategoryDto>> GetCategoriesAsync(CallerContext caller, CancellationToken ct) => categories.ListAsync(ct);

    public Task<PagedResult<TrackDto>> GetTracksAsync(CallerContext caller, TrackQuery? query, CancellationToken ct) =>
        tracks.QueryAsync(query ?? new TrackQuery(), ct);

    public async Task<TrackDto> GetTrackAsync(CallerContext caller, Guid trackId, CancellationToken ct) =>
        await tracks.GetAsync(trackId, ct).ConfigureAwait(false) ?? throw Errors.TrackNotFound();
}

/// <summary>Playlists (any signed-in user may edit, per the role matrix).</summary>
public sealed class PlaylistService(IPlaylistRepository playlists, EventBroadcaster events)
{
    public Task<IReadOnlyList<PlaylistDto>> GetPlaylistsAsync(CallerContext caller, CancellationToken ct) => playlists.ListAsync(ct);

    public async Task<PlaylistDto> GetPlaylistAsync(CallerContext caller, Guid playlistId, CancellationToken ct) =>
        await playlists.GetAsync(playlistId, ct).ConfigureAwait(false) ?? throw Errors.PlaylistNotFound();

    public async Task<PlaylistDto> CreatePlaylistAsync(CallerContext caller, string name, IReadOnlyList<Guid>? trackIds, CancellationToken ct)
    {
        var playlist = await playlists.CreateAsync(name, trackIds, caller.UserId, ct).ConfigureAwait(false);
        Changed(playlist.Id);
        return playlist;
    }

    public async Task<PlaylistDto> RenamePlaylistAsync(CallerContext caller, Guid playlistId, string name, CancellationToken ct)
    {
        var playlist = await playlists.RenameAsync(playlistId, name, ct).ConfigureAwait(false) ?? throw Errors.PlaylistNotFound();
        Changed(playlistId);
        return playlist;
    }

    public async Task<PlaylistDto> SetPlaylistTracksAsync(CallerContext caller, Guid playlistId, IReadOnlyList<Guid> trackIds, CancellationToken ct)
    {
        Errors.RequireNotNull(trackIds, "trackIds");
        var playlist = await playlists.SetTracksAsync(playlistId, trackIds, ct).ConfigureAwait(false) ?? throw Errors.PlaylistNotFound();
        Changed(playlistId);
        return playlist;
    }

    public async Task DeletePlaylistAsync(CallerContext caller, Guid playlistId, CancellationToken ct)
    {
        if (!await playlists.DeleteAsync(playlistId, ct).ConfigureAwait(false))
        {
            throw Errors.PlaylistNotFound();
        }

        Changed(playlistId);
    }

    private void Changed(Guid playlistId) => events.PublishLibraryChanged(new LibraryChangedDto(LibraryChangeKind.PlaylistsChanged, playlistId));
}

/// <summary>Validates playback commands, then forwards them to the <see cref="IPlaybackBackend"/>.</summary>
public sealed class PlaybackService(IPlaybackBackend backend, ITrackRepository tracks, IPlaylistRepository playlists)
{
    public const double MaxCrossfadeSeconds = 12;

    public Task<PlaybackStateDto> GetStateAsync(CallerContext caller, CancellationToken ct) => Task.FromResult(backend.GetState());

    public Task<QueueDto> GetQueueAsync(CallerContext caller, CancellationToken ct) => Task.FromResult(backend.GetQueue());

    public async Task PlayTrackAsync(CallerContext caller, Guid trackId, CancellationToken ct)
    {
        await RequireTracksAsync([trackId], ct).ConfigureAwait(false);
        await backend.PlayTrackAsync(trackId, ct).ConfigureAwait(false);
    }

    public Task PlayQueueItemAsync(CallerContext caller, Guid queueItemId, CancellationToken ct) => backend.PlayQueueItemAsync(queueItemId, ct);

    public Task PauseAsync(CallerContext caller, CancellationToken ct) => backend.PauseAsync(ct);

    public Task ResumeAsync(CallerContext caller, CancellationToken ct) => backend.ResumeAsync(ct);

    public Task StopAsync(CallerContext caller, CancellationToken ct) => backend.StopAsync(ct);

    public Task NextAsync(CallerContext caller, CancellationToken ct) => backend.NextAsync(ct);

    public Task PreviousAsync(CallerContext caller, CancellationToken ct) => backend.PreviousAsync(ct);

    public Task SeekAsync(CallerContext caller, double positionSeconds, CancellationToken ct)
    {
        Errors.RequireRange(positionSeconds, 0, double.MaxValue, "Position");
        return backend.SeekAsync(positionSeconds, ct);
    }

    public Task SetVolumeAsync(CallerContext caller, float volume, CancellationToken ct)
    {
        Errors.RequireRange(volume, 0, 1, "Volume");
        return backend.SetVolumeAsync(volume, ct);
    }

    public Task SetMutedAsync(CallerContext caller, bool muted, CancellationToken ct) => backend.SetMutedAsync(muted, ct);

    public Task SetLoopModeAsync(CallerContext caller, LoopMode mode, CancellationToken ct)
    {
        if (!Enum.IsDefined(mode))
        {
            throw new SabhaMujicException(ErrorCode.InvalidArgument, "Unknown loop mode.");
        }

        return backend.SetLoopModeAsync(mode, ct);
    }

    public Task SetCrossfadeAsync(CallerContext caller, double seconds, CancellationToken ct)
    {
        Errors.RequireRange(seconds, 0, MaxCrossfadeSeconds, "Crossfade");
        return backend.SetCrossfadeAsync(seconds, ct);
    }

    public async Task<QueueDto> EnqueueAsync(CallerContext caller, IReadOnlyList<Guid> trackIds, bool playNext, CancellationToken ct)
    {
        Errors.RequireNotNull(trackIds, "trackIds");
        RequireQueueSize(trackIds.Count);
        await RequireTracksAsync(trackIds, ct).ConfigureAwait(false);
        return await backend.EnqueueAsync(trackIds, playNext, ct).ConfigureAwait(false);
    }

    public async Task<QueueDto> EnqueuePlaylistAsync(CallerContext caller, Guid playlistId, bool replaceQueue, CancellationToken ct)
    {
        var playlist = await playlists.GetAsync(playlistId, ct).ConfigureAwait(false) ?? throw Errors.PlaylistNotFound();
        RequireQueueSize(playlist.TrackIds.Count);
        return replaceQueue
            ? await backend.ReplaceQueueAsync(playlist.TrackIds, ct).ConfigureAwait(false)
            : await backend.EnqueueAsync(playlist.TrackIds, playNext: false, ct).ConfigureAwait(false);
    }

    public Task<QueueDto> RemoveFromQueueAsync(CallerContext caller, Guid queueItemId, CancellationToken ct) =>
        backend.RemoveFromQueueAsync(queueItemId, ct);

    public Task<QueueDto> MoveQueueItemAsync(CallerContext caller, Guid queueItemId, int newIndex, CancellationToken ct)
    {
        if (newIndex < 0)
        {
            throw new SabhaMujicException(ErrorCode.InvalidArgument, "The new index must not be negative.");
        }

        return backend.MoveQueueItemAsync(queueItemId, newIndex, ct);
    }

    public Task<QueueDto> ClearQueueAsync(CallerContext caller, CancellationToken ct) => backend.ClearQueueAsync(ct);

    private static void RequireQueueSize(int count)
    {
        if (count > PlaybackQueue.MaxItems)
        {
            throw new SabhaMujicException(ErrorCode.InvalidArgument, $"The queue is limited to {PlaybackQueue.MaxItems} items.");
        }
    }

    private async Task RequireTracksAsync(IReadOnlyList<Guid> trackIds, CancellationToken ct)
    {
        foreach (var id in trackIds.Distinct())
        {
            if (await tracks.GetAsync(id, ct).ConfigureAwait(false) is null)
            {
                throw Errors.TrackNotFound();
            }
        }
    }
}

internal static class Errors
{
    public static SabhaMujicException TrackNotFound() => new(ErrorCode.NotFound, "The track does not exist.");

    public static SabhaMujicException PlaylistNotFound() => new(ErrorCode.NotFound, "The playlist does not exist.");

    public static SabhaMujicException CategoryNotFound() => new(ErrorCode.NotFound, "The category does not exist.");

    public static SabhaMujicException UserNotFound() => new(ErrorCode.NotFound, "The user does not exist.");

    public static SabhaMujicException DeviceNotFound() => new(ErrorCode.NotFound, "The device does not exist.");

    public static void RequireRange(double value, double min, double max, string field)
    {
        if (!double.IsFinite(value) || value < min || value > max)
        {
            throw new SabhaMujicException(ErrorCode.InvalidArgument, $"{field} must be between {min} and {max}.");
        }
    }

    public static void RequireNotNull(object? value, string field)
    {
        if (value is null)
        {
            throw new SabhaMujicException(ErrorCode.InvalidArgument, $"{field} is required.");
        }
    }
}
