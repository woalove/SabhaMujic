using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SabhaMujic.Contracts;
using SabhaMujic.Data;
using SabhaMujic.Data.Repositories;
using SabhaMujic.Data.Storage;
using SabhaMujic.Server.Api;

namespace SabhaMujic.Server.Application;

/// <summary>Everything the application layer needs from the host (Windows service, dev host or tests).</summary>
public sealed class ServerDependencies
{
    /// <summary>Migrated database. Its <see cref="SqliteDatabase.Clock"/> is the clock for the whole application.</summary>
    public required SqliteDatabase Database { get; init; }

    public required ProtectedStore Store { get; init; }

    public required UploadStaging Uploads { get; init; }

    public required IPlaybackBackend Playback { get; init; }

    public required IAudioProbe AudioProbe { get; init; }

    public required IAnalysisQueue AnalysisQueue { get; init; }

    public required IRemoteUrlProvider RemoteUrls { get; init; }

    public ServerOptions Options { get; init; } = new();

    public ILoggerFactory LoggerFactory { get; init; } = NullLoggerFactory.Instance;

    public EventBroadcaster? Events { get; init; }
}

/// <summary>
/// Composition root of the application layer: repositories, services, the event broadcaster and the factory for
/// per-connection <see cref="ApiSession"/>s. Transports (pipe, HTTP/SignalR) only talk to <see cref="ApiSession"/>.
/// </summary>
public sealed class ServerApplication : IDisposable
{
    private readonly IPlaybackBackend _playback;

    public ServerApplication(ServerDependencies deps)
    {
        ArgumentNullException.ThrowIfNull(deps);
        Options = deps.Options;
        Clock = deps.Database.Clock;
        LoggerFactory = deps.LoggerFactory;
        _playback = deps.Playback;

        var db = deps.Database;
        Users = new SqliteUserRepository(db);
        Sessions = new SqliteSessionRepository(db);
        Devices = new SqliteDeviceRepository(db);
        PairingCodes = new SqlitePairingCodeRepository(db);
        Categories = new SqliteCategoryRepository(db);
        Tracks = new SqliteTrackRepository(db);
        Analysis = new SqliteTrackAnalysisRepository(db);
        Playlists = new SqlitePlaylistRepository(db);
        PlaybackState = new SqlitePlaybackStateRepository(db);
        Settings = new SqliteSettingsRepository(db);
        AuditLog = new SqliteAuditLog(db);

        Events = deps.Events ?? new EventBroadcaster(LoggerFactory.CreateLogger<EventBroadcaster>());
        var audit = new AuditWriter(AuditLog, Clock, LoggerFactory.CreateLogger<AuditWriter>());
        Auth = new AuthService(Users, Sessions, Devices, PairingCodes, Events, audit, Options, Clock);
        Library = new LibraryService(Categories, Tracks);
        PlaylistService = new PlaylistService(Playlists, Events);
        Playback = new PlaybackService(deps.Playback, Tracks, Playlists);
        Admin = new AdminService(Categories, Tracks, Analysis, Users, Sessions, Devices, PairingCodes, deps.Store, deps.Playback,
            deps.AnalysisQueue, deps.RemoteUrls, Auth, Events, audit, Options, Clock, LoggerFactory.CreateLogger<AdminService>());
        Import = new ImportService(deps.Uploads, deps.Store, Tracks, Categories, Analysis, deps.AudioProbe, deps.AnalysisQueue, Events, audit,
            LoggerFactory.CreateLogger<ImportService>());
        Maintenance = new MaintenanceService(Sessions, PairingCodes, Analysis, deps.Uploads, deps.AnalysisQueue, Clock,
            LoggerFactory.CreateLogger<MaintenanceService>());
        Uploads = deps.Uploads;

        _playback.StateChanged += Events.PublishPlaybackState;
        _playback.QueueChanged += Events.PublishQueue;
    }

    public ServerOptions Options { get; }

    public TimeProvider Clock { get; }

    public ILoggerFactory LoggerFactory { get; }

    public EventBroadcaster Events { get; }

    public AuthService Auth { get; }

    public LibraryService Library { get; }

    public PlaylistService PlaylistService { get; }

    public PlaybackService Playback { get; }

    public AdminService Admin { get; }

    public ImportService Import { get; }

    public MaintenanceService Maintenance { get; }

    public UploadStaging Uploads { get; }

    public IUserRepository Users { get; }

    public ISessionRepository Sessions { get; }

    public IDeviceRepository Devices { get; }

    public IPairingCodeRepository PairingCodes { get; }

    public ICategoryRepository Categories { get; }

    public ITrackRepository Tracks { get; }

    public ITrackAnalysisRepository Analysis { get; }

    public IPlaylistRepository Playlists { get; }

    /// <summary>For the M3b engine adapter (queue + settings persistence).</summary>
    public IPlaybackStateRepository PlaybackState { get; }

    public ISettingsRepository Settings { get; }

    public IAuditLog AuditLog { get; }

    /// <summary>Creates the authorization choke point for one client connection.</summary>
    public ApiSession CreateSession(ClientKind client, string transportPrincipal) =>
        new(this, client, transportPrincipal, LoggerFactory.CreateLogger<ApiSession>());

    public void Dispose()
    {
        _playback.StateChanged -= Events.PublishPlaybackState;
        _playback.QueueChanged -= Events.PublishQueue;
    }
}
