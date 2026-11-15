using SabhaMujic.Contracts;

namespace SabhaMujic.Client;

public enum ConnectionState
{
    Disconnected = 0,
    Connecting = 1,
    Connected = 2,
    /// <summary>Connection lost; the client is retrying with backoff.</summary>
    Reconnecting = 3,
}

/// <summary>
/// Desktop-side view of the SabhaMujic service. View models depend only on this interface,
/// so they can be unit-tested with a fake. The production implementation (named pipe + StreamJsonRpc)
/// lives in this project and is built in M3.
/// </summary>
public interface ISabhaClient : IAsyncDisposable
{
    ConnectionState State { get; }

    /// <summary>
    /// The current session, or null when signed out. Successful <c>Auth.LoginAsync</c>/<c>ResumeSessionAsync</c> calls
    /// made through this client set it; <c>LogoutAsync</c>, a <c>SessionEnded</c> event or a failed resume clear it.
    /// Every change raises <see cref="SessionChanged"/>.
    /// </summary>
    SessionDto? Session { get; }

    IAuthApi Auth { get; }

    ILibraryApi Library { get; }

    IPlaybackApi Playback { get; }

    IPlaylistApi Playlists { get; }

    IAdminApi Admin { get; }

    /// <summary>Server push events. Raised on a background thread; UI code must marshal to the dispatcher.</summary>
    ClientEventHub Events { get; }

    event EventHandler<ConnectionState>? ConnectionStateChanged;

    event EventHandler<SessionDto?>? SessionChanged;

    /// <summary>
    /// Completes once the first connection (including the Hello handshake) succeeds; throws on an incompatible service
    /// or cancellation. After that the client keeps reconnecting in the background with backoff until disposed, and
    /// automatically resumes the stored session after each reconnect.
    /// </summary>
    Task ConnectAsync(CancellationToken ct = default);
}

/// <summary>Adapts <see cref="IClientEvents"/> callbacks into .NET events.</summary>
public sealed class ClientEventHub : IClientEvents
{
    public event Action<PlaybackStateDto>? PlaybackStateReceived;

    public event Action<QueueDto>? QueueReceived;

    public event Action<LibraryChangedDto>? LibraryChangeReceived;

    public event Action<ImportProgressDto>? ImportProgressReceived;

    public event Action<string>? SessionEndedReceived;

    public void PlaybackStateChanged(PlaybackStateDto state) => PlaybackStateReceived?.Invoke(state);

    public void QueueChanged(QueueDto queue) => QueueReceived?.Invoke(queue);

    public void LibraryChanged(LibraryChangedDto change) => LibraryChangeReceived?.Invoke(change);

    public void ImportProgress(ImportProgressDto progress) => ImportProgressReceived?.Invoke(progress);

    public void SessionEnded(string reason) => SessionEndedReceived?.Invoke(reason);
}
