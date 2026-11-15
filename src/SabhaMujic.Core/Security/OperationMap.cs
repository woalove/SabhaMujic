using System.Collections.Frozen;
using SabhaMujic.Contracts;

namespace SabhaMujic.Core.Security;

/// <summary>
/// Maps each contract RPC method ("Interface.Method") to its <see cref="Operation"/>.
/// A test enforces that every method on every contract interface is mapped.
/// </summary>
public static class OperationMap
{
    public static readonly FrozenDictionary<string, Operation> ByMethod = new Dictionary<string, Operation>
    {
        [$"{nameof(IAuthApi)}.{nameof(IAuthApi.HelloAsync)}"] = Operation.Hello,
        [$"{nameof(IAuthApi)}.{nameof(IAuthApi.LoginAsync)}"] = Operation.Login,
        [$"{nameof(IAuthApi)}.{nameof(IAuthApi.ResumeSessionAsync)}"] = Operation.ResumeSession,
        [$"{nameof(IAuthApi)}.{nameof(IAuthApi.LogoutAsync)}"] = Operation.Logout,

        [$"{nameof(ILibraryApi)}.{nameof(ILibraryApi.GetCategoriesAsync)}"] = Operation.BrowseLibrary,
        [$"{nameof(ILibraryApi)}.{nameof(ILibraryApi.GetTracksAsync)}"] = Operation.BrowseLibrary,
        [$"{nameof(ILibraryApi)}.{nameof(ILibraryApi.GetTrackAsync)}"] = Operation.BrowseLibrary,
        [$"{nameof(ILibraryApi)}.{nameof(ILibraryApi.GetTracksByIdsAsync)}"] = Operation.BrowseLibrary,

        [$"{nameof(IPlaybackApi)}.{nameof(IPlaybackApi.GetStateAsync)}"] = Operation.ReadPlayback,
        [$"{nameof(IPlaybackApi)}.{nameof(IPlaybackApi.GetQueueAsync)}"] = Operation.ReadPlayback,
        [$"{nameof(IPlaybackApi)}.{nameof(IPlaybackApi.PlayTrackAsync)}"] = Operation.ControlPlayback,
        [$"{nameof(IPlaybackApi)}.{nameof(IPlaybackApi.PlayQueueItemAsync)}"] = Operation.ControlPlayback,
        [$"{nameof(IPlaybackApi)}.{nameof(IPlaybackApi.PauseAsync)}"] = Operation.ControlPlayback,
        [$"{nameof(IPlaybackApi)}.{nameof(IPlaybackApi.ResumeAsync)}"] = Operation.ControlPlayback,
        [$"{nameof(IPlaybackApi)}.{nameof(IPlaybackApi.StopAsync)}"] = Operation.ControlPlayback,
        [$"{nameof(IPlaybackApi)}.{nameof(IPlaybackApi.NextAsync)}"] = Operation.ControlPlayback,
        [$"{nameof(IPlaybackApi)}.{nameof(IPlaybackApi.PreviousAsync)}"] = Operation.ControlPlayback,
        [$"{nameof(IPlaybackApi)}.{nameof(IPlaybackApi.SeekAsync)}"] = Operation.ControlPlayback,
        [$"{nameof(IPlaybackApi)}.{nameof(IPlaybackApi.SetVolumeAsync)}"] = Operation.ControlPlayback,
        [$"{nameof(IPlaybackApi)}.{nameof(IPlaybackApi.SetMutedAsync)}"] = Operation.ControlPlayback,
        [$"{nameof(IPlaybackApi)}.{nameof(IPlaybackApi.SetLoopModeAsync)}"] = Operation.ControlPlayback,
        [$"{nameof(IPlaybackApi)}.{nameof(IPlaybackApi.SetCrossfadeAsync)}"] = Operation.ControlPlayback,
        [$"{nameof(IPlaybackApi)}.{nameof(IPlaybackApi.EnqueueAsync)}"] = Operation.EditQueue,
        [$"{nameof(IPlaybackApi)}.{nameof(IPlaybackApi.EnqueuePlaylistAsync)}"] = Operation.EditQueue,
        [$"{nameof(IPlaybackApi)}.{nameof(IPlaybackApi.RemoveFromQueueAsync)}"] = Operation.EditQueue,
        [$"{nameof(IPlaybackApi)}.{nameof(IPlaybackApi.MoveQueueItemAsync)}"] = Operation.EditQueue,
        [$"{nameof(IPlaybackApi)}.{nameof(IPlaybackApi.ClearQueueAsync)}"] = Operation.EditQueue,

        [$"{nameof(IPlaylistApi)}.{nameof(IPlaylistApi.GetPlaylistsAsync)}"] = Operation.ReadPlaylists,
        [$"{nameof(IPlaylistApi)}.{nameof(IPlaylistApi.GetPlaylistAsync)}"] = Operation.ReadPlaylists,
        [$"{nameof(IPlaylistApi)}.{nameof(IPlaylistApi.CreatePlaylistAsync)}"] = Operation.EditPlaylists,
        [$"{nameof(IPlaylistApi)}.{nameof(IPlaylistApi.RenamePlaylistAsync)}"] = Operation.EditPlaylists,
        [$"{nameof(IPlaylistApi)}.{nameof(IPlaylistApi.SetPlaylistTracksAsync)}"] = Operation.EditPlaylists,
        [$"{nameof(IPlaylistApi)}.{nameof(IPlaylistApi.DeletePlaylistAsync)}"] = Operation.EditPlaylists,

        [$"{nameof(IAdminApi)}.{nameof(IAdminApi.BeginImportAsync)}"] = Operation.ImportTracks,
        [$"{nameof(IAdminApi)}.{nameof(IAdminApi.UploadChunkAsync)}"] = Operation.ImportTracks,
        [$"{nameof(IAdminApi)}.{nameof(IAdminApi.CompleteImportAsync)}"] = Operation.ImportTracks,
        [$"{nameof(IAdminApi)}.{nameof(IAdminApi.CancelImportAsync)}"] = Operation.ImportTracks,
        [$"{nameof(IAdminApi)}.{nameof(IAdminApi.CreateCategoryAsync)}"] = Operation.ManageCategories,
        [$"{nameof(IAdminApi)}.{nameof(IAdminApi.RenameCategoryAsync)}"] = Operation.ManageCategories,
        [$"{nameof(IAdminApi)}.{nameof(IAdminApi.ReorderCategoriesAsync)}"] = Operation.ManageCategories,
        [$"{nameof(IAdminApi)}.{nameof(IAdminApi.DeleteCategoryAsync)}"] = Operation.ManageCategories,
        [$"{nameof(IAdminApi)}.{nameof(IAdminApi.UpdateTrackAsync)}"] = Operation.ManageTracks,
        [$"{nameof(IAdminApi)}.{nameof(IAdminApi.DeleteTrackAsync)}"] = Operation.ManageTracks,
        [$"{nameof(IAdminApi)}.{nameof(IAdminApi.SetLoopOverrideAsync)}"] = Operation.ManageLoopPoints,
        [$"{nameof(IAdminApi)}.{nameof(IAdminApi.ReanalyzeTrackAsync)}"] = Operation.ManageLoopPoints,
        [$"{nameof(IAdminApi)}.{nameof(IAdminApi.GetUsersAsync)}"] = Operation.ManageUsers,
        [$"{nameof(IAdminApi)}.{nameof(IAdminApi.CreateUserAsync)}"] = Operation.ManageUsers,
        [$"{nameof(IAdminApi)}.{nameof(IAdminApi.SetUserSecretAsync)}"] = Operation.ManageUsers,
        [$"{nameof(IAdminApi)}.{nameof(IAdminApi.DeleteUserAsync)}"] = Operation.ManageUsers,
        [$"{nameof(IAdminApi)}.{nameof(IAdminApi.GetDevicesAsync)}"] = Operation.ManageDevices,
        [$"{nameof(IAdminApi)}.{nameof(IAdminApi.CreatePairingCodeAsync)}"] = Operation.ManageDevices,
        [$"{nameof(IAdminApi)}.{nameof(IAdminApi.RevokeDeviceAsync)}"] = Operation.ManageDevices,
    }.ToFrozenDictionary();

    public static Operation For(string interfaceName, string methodName) =>
        ByMethod.TryGetValue($"{interfaceName}.{methodName}", out var op)
            ? op
            : throw new InvalidOperationException($"{interfaceName}.{methodName} has no Operation mapping.");
}
