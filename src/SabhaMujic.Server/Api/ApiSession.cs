using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using SabhaMujic.Contracts;
using SabhaMujic.Core.Security;
using SabhaMujic.Server.Application;

namespace SabhaMujic.Server.Api;

/// <summary>Result of pairing a remote device: its record and the long-lived device session (token included once).</summary>
public sealed record PairedDevice(DeviceDto Device, SessionDto Session);

/// <summary>
/// The authorization choke point for one client connection, transport-agnostic (named pipe now, HTTP/SignalR in M5).
/// <para>
/// Every contract method is a one-liner through <see cref="Run{T}"/>: it resolves the operation from
/// <see cref="OperationMap"/> by interface + method name, re-validates the bound session (expiry/revocation), and calls
/// <see cref="Authorizer.Demand"/> <b>before</b> the body runs. Bodies only call application services. Unexpected
/// exceptions become <see cref="ErrorCode.Internal"/> without details.
/// </para>
/// Login/resume/pair bind a session to this connection; logout, revocation (<see cref="EndSession"/>) or expiry unbind it.
/// </summary>
public sealed class ApiSession : IAuthApi, ILibraryApi, IPlaybackApi, IPlaylistApi, IAdminApi
{
    private const string Auth = nameof(IAuthApi);
    private const string Library = nameof(ILibraryApi);
    private const string Playback = nameof(IPlaybackApi);
    private const string Playlists = nameof(IPlaylistApi);
    private const string Admin = nameof(IAdminApi);

    private readonly ServerApplication _app;
    private readonly ILogger _logger;
    private readonly CallerContext _anonymous;
    private readonly bool _isElevatedAdministrator;
    private readonly Lock _gate = new();
    private CallerContext _caller;
    private string? _tokenHash;
    private long _binding;

    internal ApiSession(ServerApplication app, ClientKind client, string transportPrincipal, bool isElevatedAdministrator, ILogger<ApiSession> logger)
    {
        _app = app;
        _isElevatedAdministrator = isElevatedAdministrator;
        _logger = logger;
        _anonymous = CallerContext.Anonymous(client, string.IsNullOrWhiteSpace(transportPrincipal) ? "unknown" : transportPrincipal);
        _caller = _anonymous;
    }

    /// <summary>Raised when the connection signs in or out (the transport updates its event subscription).</summary>
    public event Action<CallerContext>? CallerChanged;

    /// <summary>True when the transport verified an elevated local Administrator token (desktop pipe on Windows only).</summary>
    public bool IsElevatedAdministrator => _isElevatedAdministrator;

    /// <summary>The caller as of the last bind/unbind (not re-validated).</summary>
    public CallerContext Caller
    {
        get
        {
            lock (_gate)
            {
                return _caller;
            }
        }
    }

    /// <summary>Signs the connection out because its session was ended server-side (revoked device, deleted user, …).</summary>
    public void EndSession(string reason) => Unbind(expectedBinding: null);

    // ------------------------------------------------------------------ IAuthApi

    public Task<HelloResponse> HelloAsync(int clientContractVersion, CancellationToken ct = default) => Run(Auth, _ =>
    {
        if (clientContractVersion != ContractVersion.Current)
        {
            throw new SabhaMujicException(ErrorCode.IncompatibleClient,
                $"This app (contract {clientContractVersion}) does not match the SabhaMujic service (contract {ContractVersion.Current}). Update the app.");
        }

        return Task.FromResult(new HelloResponse(ContractVersion.Current, _app.Options.ServerVersion, _app.Options.MachineName));
    }, ct);

    public Task<SessionDto> LoginAsync(string userName, string secret, CancellationToken ct = default) => Run(Auth, async _ =>
        Bind(await _app.Auth.LoginAsync(_anonymous, userName, secret, ct).ConfigureAwait(false)), ct);

    public Task<SessionDto> ResumeSessionAsync(string token, CancellationToken ct = default) => Run(Auth, async _ =>
        Bind(await _app.Auth.ResumeAsync(_anonymous, token, ct).ConfigureAwait(false)), ct);

    public Task<bool> IsSetupRequiredAsync(CancellationToken ct = default) => Run(Auth, _ => _app.Auth.IsSetupRequiredAsync(ct), ct);

    /// <summary>
    /// First-run setup. Beyond the (anonymous) matrix entry, <see cref="AuthService.CompleteInitialSetupAsync"/> requires the
    /// desktop pipe and an elevated local Administrator as reported by the transport for this connection.
    /// </summary>
    public Task<SessionDto> CompleteInitialSetupAsync(string adminUserName, string secret, CancellationToken ct = default) => Run(Auth, async _ =>
        Bind(await _app.Auth.CompleteInitialSetupAsync(_anonymous, _isElevatedAdministrator, adminUserName, secret, ct).ConfigureAwait(false)), ct);

    /// <summary>
    /// Admin PIN recovery. Like setup, <see cref="AuthService.ResetAdministratorAsync"/> requires the desktop pipe and an
    /// elevated local Administrator as reported by the transport; the connection is then signed in as that administrator.
    /// </summary>
    public Task<SessionDto> ResetAdministratorAsync(string adminUserName, string newSecret, CancellationToken ct = default) => Run(Auth, async _ =>
        Bind(await _app.Auth.ResetAdministratorAsync(_anonymous, _isElevatedAdministrator, adminUserName, newSecret, ct).ConfigureAwait(false)), ct);

    public Task LogoutAsync(CancellationToken ct = default) => Run(Auth, async caller =>
    {
        await _app.Auth.LogoutAsync(caller, ct).ConfigureAwait(false);
        Unbind(expectedBinding: null);
    }, ct);

    /// <summary>Pairs a remote device with a one-time code (anonymous, rate-limited). Not on the pipe contract; used by M5.</summary>
    public Task<PairedDevice> PairAsync(string code, string deviceName, CancellationToken ct = default) => Run(Operation.Pair, async _ =>
    {
        var result = await _app.Auth.PairAsync(_anonymous, code, deviceName, ct).ConfigureAwait(false);
        Bind(new AuthResult(result.Session, result.TokenHash, result.Caller));
        return new PairedDevice(result.Device, result.Session);
    }, ct);

    // ------------------------------------------------------------------ ILibraryApi

    public Task<IReadOnlyList<CategoryDto>> GetCategoriesAsync(CancellationToken ct = default) =>
        Run(Library, c => _app.Library.GetCategoriesAsync(c, ct), ct);

    public Task<PagedResult<TrackDto>> GetTracksAsync(TrackQuery query, CancellationToken ct = default) =>
        Run(Library, c => _app.Library.GetTracksAsync(c, query, ct), ct);

    public Task<TrackDto> GetTrackAsync(Guid trackId, CancellationToken ct = default) =>
        Run(Library, c => _app.Library.GetTrackAsync(c, trackId, ct), ct);

    public Task<IReadOnlyList<TrackDto>> GetTracksByIdsAsync(IReadOnlyList<Guid> trackIds, CancellationToken ct = default) =>
        Run(Library, c => _app.Library.GetTracksByIdsAsync(c, trackIds, ct), ct);

    // ------------------------------------------------------------------ IPlaybackApi

    public Task<PlaybackStateDto> GetStateAsync(CancellationToken ct = default) => Run(Playback, c => _app.Playback.GetStateAsync(c, ct), ct);

    public Task<QueueDto> GetQueueAsync(CancellationToken ct = default) => Run(Playback, c => _app.Playback.GetQueueAsync(c, ct), ct);

    public Task PlayTrackAsync(Guid trackId, CancellationToken ct = default) => Run(Playback, c => _app.Playback.PlayTrackAsync(c, trackId, ct), ct);

    public Task PlayQueueItemAsync(Guid queueItemId, CancellationToken ct = default) =>
        Run(Playback, c => _app.Playback.PlayQueueItemAsync(c, queueItemId, ct), ct);

    public Task PauseAsync(CancellationToken ct = default) => Run(Playback, c => _app.Playback.PauseAsync(c, ct), ct);

    public Task ResumeAsync(CancellationToken ct = default) => Run(Playback, c => _app.Playback.ResumeAsync(c, ct), ct);

    public Task StopAsync(CancellationToken ct = default) => Run(Playback, c => _app.Playback.StopAsync(c, ct), ct);

    public Task NextAsync(CancellationToken ct = default) => Run(Playback, c => _app.Playback.NextAsync(c, ct), ct);

    public Task PreviousAsync(CancellationToken ct = default) => Run(Playback, c => _app.Playback.PreviousAsync(c, ct), ct);

    public Task SeekAsync(double positionSeconds, CancellationToken ct = default) =>
        Run(Playback, c => _app.Playback.SeekAsync(c, positionSeconds, ct), ct);

    public Task SetVolumeAsync(float volume, CancellationToken ct = default) => Run(Playback, c => _app.Playback.SetVolumeAsync(c, volume, ct), ct);

    public Task SetMutedAsync(bool muted, CancellationToken ct = default) => Run(Playback, c => _app.Playback.SetMutedAsync(c, muted, ct), ct);

    public Task SetLoopModeAsync(LoopMode mode, CancellationToken ct = default) => Run(Playback, c => _app.Playback.SetLoopModeAsync(c, mode, ct), ct);

    public Task SetCrossfadeAsync(double seconds, CancellationToken ct = default) =>
        Run(Playback, c => _app.Playback.SetCrossfadeAsync(c, seconds, ct), ct);

    public Task<QueueDto> EnqueueAsync(IReadOnlyList<Guid> trackIds, bool playNext, CancellationToken ct = default) =>
        Run(Playback, c => _app.Playback.EnqueueAsync(c, trackIds, playNext, ct), ct);

    public Task<QueueDto> EnqueuePlaylistAsync(Guid playlistId, bool replaceQueue, CancellationToken ct = default) =>
        Run(Playback, c => _app.Playback.EnqueuePlaylistAsync(c, playlistId, replaceQueue, ct), ct);

    public Task<QueueDto> RemoveFromQueueAsync(Guid queueItemId, CancellationToken ct = default) =>
        Run(Playback, c => _app.Playback.RemoveFromQueueAsync(c, queueItemId, ct), ct);

    public Task<QueueDto> MoveQueueItemAsync(Guid queueItemId, int newIndex, CancellationToken ct = default) =>
        Run(Playback, c => _app.Playback.MoveQueueItemAsync(c, queueItemId, newIndex, ct), ct);

    public Task<QueueDto> ClearQueueAsync(CancellationToken ct = default) => Run(Playback, c => _app.Playback.ClearQueueAsync(c, ct), ct);

    // ------------------------------------------------------------------ IPlaylistApi

    public Task<IReadOnlyList<PlaylistDto>> GetPlaylistsAsync(CancellationToken ct = default) =>
        Run(Playlists, c => _app.PlaylistService.GetPlaylistsAsync(c, ct), ct);

    public Task<PlaylistDto> GetPlaylistAsync(Guid playlistId, CancellationToken ct = default) =>
        Run(Playlists, c => _app.PlaylistService.GetPlaylistAsync(c, playlistId, ct), ct);

    public Task<PlaylistDto> CreatePlaylistAsync(string name, IReadOnlyList<Guid>? trackIds = null, CancellationToken ct = default) =>
        Run(Playlists, c => _app.PlaylistService.CreatePlaylistAsync(c, name, trackIds, ct), ct);

    public Task<PlaylistDto> RenamePlaylistAsync(Guid playlistId, string name, CancellationToken ct = default) =>
        Run(Playlists, c => _app.PlaylistService.RenamePlaylistAsync(c, playlistId, name, ct), ct);

    public Task<PlaylistDto> SetPlaylistTracksAsync(Guid playlistId, IReadOnlyList<Guid> trackIds, CancellationToken ct = default) =>
        Run(Playlists, c => _app.PlaylistService.SetPlaylistTracksAsync(c, playlistId, trackIds, ct), ct);

    public Task DeletePlaylistAsync(Guid playlistId, CancellationToken ct = default) =>
        Run(Playlists, c => _app.PlaylistService.DeletePlaylistAsync(c, playlistId, ct), ct);

    // ------------------------------------------------------------------ IAdminApi

    public Task<Guid> BeginImportAsync(string fileName, long totalBytes, CancellationToken ct = default) =>
        Run(Admin, c => _app.Import.BeginImportAsync(c, fileName, totalBytes, ct), ct);

    public Task UploadChunkAsync(Guid uploadId, long offset, byte[] data, CancellationToken ct = default) =>
        Run(Admin, c => _app.Import.UploadChunkAsync(c, uploadId, offset, data, ct), ct);

    public Task<ImportResultDto> CompleteImportAsync(Guid uploadId, ImportMetadata metadata, CancellationToken ct = default) =>
        Run(Admin, c => _app.Import.CompleteImportAsync(c, uploadId, metadata, ct), ct);

    public Task CancelImportAsync(Guid uploadId, CancellationToken ct = default) =>
        Run(Admin, c => _app.Import.CancelImportAsync(c, uploadId, ct), ct);

    public Task<CategoryDto> CreateCategoryAsync(string name, CancellationToken ct = default) =>
        Run(Admin, c => _app.Admin.CreateCategoryAsync(c, name, ct), ct);

    public Task<CategoryDto> RenameCategoryAsync(Guid categoryId, string name, CancellationToken ct = default) =>
        Run(Admin, c => _app.Admin.RenameCategoryAsync(c, categoryId, name, ct), ct);

    public Task ReorderCategoriesAsync(IReadOnlyList<Guid> orderedCategoryIds, CancellationToken ct = default) =>
        Run(Admin, c => _app.Admin.ReorderCategoriesAsync(c, orderedCategoryIds, ct), ct);

    public Task DeleteCategoryAsync(Guid categoryId, CancellationToken ct = default) =>
        Run(Admin, c => _app.Admin.DeleteCategoryAsync(c, categoryId, ct), ct);

    public Task<TrackDto> UpdateTrackAsync(TrackUpdateDto update, CancellationToken ct = default) =>
        Run(Admin, c => _app.Admin.UpdateTrackAsync(c, update, ct), ct);

    public Task DeleteTrackAsync(Guid trackId, CancellationToken ct = default) =>
        Run(Admin, c => _app.Admin.DeleteTrackAsync(c, trackId, ct), ct);

    public Task<TrackDto> SetLoopOverrideAsync(Guid trackId, LoopRegionDto? region, CancellationToken ct = default) =>
        Run(Admin, c => _app.Admin.SetLoopOverrideAsync(c, trackId, region, ct), ct);

    public Task ReanalyzeTrackAsync(Guid trackId, CancellationToken ct = default) =>
        Run(Admin, c => _app.Admin.ReanalyzeTrackAsync(c, trackId, ct), ct);

    public Task<IReadOnlyList<UserDto>> GetUsersAsync(CancellationToken ct = default) => Run(Admin, c => _app.Admin.GetUsersAsync(c, ct), ct);

    public Task<UserDto> CreateUserAsync(string userName, Role role, string secret, CancellationToken ct = default) =>
        Run(Admin, c => _app.Admin.CreateUserAsync(c, userName, role, secret, ct), ct);

    public Task SetUserSecretAsync(Guid userId, string secret, CancellationToken ct = default) =>
        Run(Admin, c => _app.Admin.SetUserSecretAsync(c, userId, secret, ct), ct);

    public Task DeleteUserAsync(Guid userId, CancellationToken ct = default) =>
        Run(Admin, c => _app.Admin.DeleteUserAsync(c, userId, ct), ct);

    public Task<IReadOnlyList<DeviceDto>> GetDevicesAsync(CancellationToken ct = default) => Run(Admin, c => _app.Admin.GetDevicesAsync(c, ct), ct);

    public Task<PairingCodeDto> CreatePairingCodeAsync(Role role, CancellationToken ct = default) =>
        Run(Admin, c => _app.Admin.CreatePairingCodeAsync(c, role, ct), ct);

    public Task RevokeDeviceAsync(Guid deviceId, CancellationToken ct = default) =>
        Run(Admin, c => _app.Admin.RevokeDeviceAsync(c, deviceId, ct), ct);

    // ------------------------------------------------------------------ the choke point

    private Task<T> Run<T>(string api, Func<CallerContext, Task<T>> body, CancellationToken ct, [CallerMemberName] string method = "") =>
        Run(OperationMap.For(api, method), body, ct);

    private Task Run(string api, Func<CallerContext, Task> body, CancellationToken ct, [CallerMemberName] string method = "") =>
        Run<bool>(OperationMap.For(api, method), async c =>
        {
            await body(c).ConfigureAwait(false);
            return true;
        }, ct);

    private async Task<T> Run<T>(Operation operation, Func<CallerContext, Task<T>> body, CancellationToken ct)
    {
        try
        {
            var caller = await CurrentCallerAsync(ct).ConfigureAwait(false);
            Authorizer.Demand(caller, operation);
            return await body(caller).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not SabhaMujicException and not OperationCanceledException)
        {
            _logger.LogError(ex, "Unexpected failure in {Operation}.", operation);
            throw new SabhaMujicException(ErrorCode.Internal, "An internal error occurred in the SabhaMujic service.");
        }
    }

    /// <summary>The caller for this call: anonymous unless a still-valid session is bound.</summary>
    private async Task<CallerContext> CurrentCallerAsync(CancellationToken ct)
    {
        string? tokenHash;
        CallerContext caller;
        long binding;
        lock (_gate)
        {
            tokenHash = _tokenHash;
            caller = _caller;
            binding = _binding;
        }

        if (tokenHash is null)
        {
            return _anonymous;
        }

        if (await _app.Auth.ValidateAsync(tokenHash, ct).ConfigureAwait(false) is null)
        {
            Unbind(binding);
            return _anonymous;
        }

        return caller;
    }

    private SessionDto Bind(AuthResult result)
    {
        lock (_gate)
        {
            _tokenHash = result.TokenHash;
            _caller = result.Caller;
            _binding++;
        }

        CallerChanged?.Invoke(result.Caller);
        return result.Session;
    }

    private void Unbind(long? expectedBinding)
    {
        lock (_gate)
        {
            if (_tokenHash is null || (expectedBinding is { } expected && expected != _binding))
            {
                return;
            }

            _tokenHash = null;
            _caller = _anonymous;
            _binding++;
        }

        CallerChanged?.Invoke(_anonymous);
    }
}
