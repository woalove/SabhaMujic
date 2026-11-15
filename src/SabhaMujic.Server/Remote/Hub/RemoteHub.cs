using Microsoft.AspNetCore.SignalR;
using SabhaMujic.Contracts;
using SabhaMujic.Server.Api;

namespace SabhaMujic.Server.Remote.Hub;

/// <summary>
/// The remote's SignalR hub at <see cref="Endpoints.RemoteHubPath"/>. Each connection owns one <see cref="ApiSession"/>
/// (bound from the device token when it connects); every method is a one-line forward to it, so the
/// <see cref="Core.Security.Authorizer"/> decides access exactly as on the pipe. Method names are the contract method
/// names without "Async" (e.g. <c>Pause</c> → <see cref="IPlaybackApi.PauseAsync"/>). Server-to-client events use the
/// <see cref="IClientEvents"/> method names. Errors arrive as a <see cref="HubException"/> whose message is
/// <c>{"code":"…","message":"…"}</c>.
/// </summary>
public sealed class RemoteHub(RemoteConnections connections) : Microsoft.AspNetCore.SignalR.Hub
{
    private ApiSession S => connections.SessionFor(Context.ConnectionId);

    private CancellationToken Ct => Context.ConnectionAborted;

    public override Task OnConnectedAsync() => connections.AttachAsync(Context);

    public override Task OnDisconnectedAsync(Exception? exception) => connections.DetachAsync(Context.ConnectionId);

    // ------------------------------------------------------------------ IPlaybackApi
    public Task<PlaybackStateDto> GetState() => S.GetStateAsync(Ct);

    public Task<QueueDto> GetQueue() => S.GetQueueAsync(Ct);

    public Task PlayTrack(Guid trackId) => S.PlayTrackAsync(trackId, Ct);

    public Task PlayQueueItem(Guid queueItemId) => S.PlayQueueItemAsync(queueItemId, Ct);

    public Task Pause() => S.PauseAsync(Ct);

    public Task Resume() => S.ResumeAsync(Ct);

    public Task Stop() => S.StopAsync(Ct);

    public Task Next() => S.NextAsync(Ct);

    public Task Previous() => S.PreviousAsync(Ct);

    public Task Seek(double positionSeconds) => S.SeekAsync(positionSeconds, Ct);

    public Task SetVolume(float volume) => S.SetVolumeAsync(volume, Ct);

    public Task SetMuted(bool muted) => S.SetMutedAsync(muted, Ct);

    public Task SetLoopMode(LoopMode mode) => S.SetLoopModeAsync(mode, Ct);

    public Task SetCrossfade(double seconds) => S.SetCrossfadeAsync(seconds, Ct);

    public Task<QueueDto> Enqueue(IReadOnlyList<Guid> trackIds, bool playNext) => S.EnqueueAsync(trackIds, playNext, Ct);

    public Task<QueueDto> EnqueuePlaylist(Guid playlistId, bool replaceQueue) => S.EnqueuePlaylistAsync(playlistId, replaceQueue, Ct);

    public Task<QueueDto> RemoveFromQueue(Guid queueItemId) => S.RemoveFromQueueAsync(queueItemId, Ct);

    public Task<QueueDto> MoveQueueItem(Guid queueItemId, int newIndex) => S.MoveQueueItemAsync(queueItemId, newIndex, Ct);

    public Task<QueueDto> ClearQueue() => S.ClearQueueAsync(Ct);

    // ------------------------------------------------------------------ ILibraryApi
    public Task<IReadOnlyList<CategoryDto>> GetCategories() => S.GetCategoriesAsync(Ct);

    public Task<PagedResult<TrackDto>> GetTracks(TrackQuery query) => S.GetTracksAsync(query, Ct);

    public Task<TrackDto> GetTrack(Guid trackId) => S.GetTrackAsync(trackId, Ct);

    public Task<IReadOnlyList<TrackDto>> GetTracksByIds(IReadOnlyList<Guid> trackIds) => S.GetTracksByIdsAsync(trackIds, Ct);

    // ------------------------------------------------------------------ IPlaylistApi
    public Task<IReadOnlyList<PlaylistDto>> GetPlaylists() => S.GetPlaylistsAsync(Ct);

    public Task<PlaylistDto> GetPlaylist(Guid playlistId) => S.GetPlaylistAsync(playlistId, Ct);

    public Task<PlaylistDto> CreatePlaylist(string name, IReadOnlyList<Guid>? trackIds) => S.CreatePlaylistAsync(name, trackIds, Ct);

    public Task<PlaylistDto> RenamePlaylist(Guid playlistId, string name) => S.RenamePlaylistAsync(playlistId, name, Ct);

    public Task<PlaylistDto> SetPlaylistTracks(Guid playlistId, IReadOnlyList<Guid> trackIds) => S.SetPlaylistTracksAsync(playlistId, trackIds, Ct);

    public Task DeletePlaylist(Guid playlistId) => S.DeletePlaylistAsync(playlistId, Ct);

    // ------------------------------------------------------------------ IAdminApi (the subset the phone needs; uploads are HTTP)
    public Task<CategoryDto> CreateCategory(string name) => S.CreateCategoryAsync(name, Ct);
}
