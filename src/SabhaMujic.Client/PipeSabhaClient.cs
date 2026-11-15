using System.IO.Pipes;
using System.Security.Principal;
using SabhaMujic.Client.Rpc;
using SabhaMujic.Contracts;
using StreamJsonRpc;

namespace SabhaMujic.Client;

public sealed class PipeClientOptions
{
    public string PipeName { get; init; } = Endpoints.PipeName;

    /// <summary>"." for the local machine.</summary>
    public string ServerName { get; init; } = ".";

    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public TimeSpan InitialBackoff { get; init; } = TimeSpan.FromMilliseconds(250);

    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Largest accepted frame from the service (track pages can be large).</summary>
    public int MaxMessageBytes { get; init; } = 16 * 1024 * 1024;
}

/// <summary>
/// <see cref="ISabhaClient"/> over the service's named pipe (StreamJsonRpc, the protocol in <see cref="RpcProtocol"/>).
/// <para>
/// <see cref="ConnectAsync"/> starts a background loop that connects, performs the <c>Hello</c> version check, resumes the
/// stored session if there is one, and after a lost connection reconnects with exponential backoff until the client is
/// disposed. The returned task completes after the first successful connect + Hello; its token only bounds that wait.
/// An incompatible service fails it with <see cref="ErrorCode.IncompatibleClient"/> and stops the loop.
/// </para>
/// <see cref="Session"/> is set by successful login/resume through <see cref="Auth"/>, and cleared by logout, a
/// <c>SessionEnded</c> event or a failed resume (explicit, or the automatic one after a reconnect).
/// API calls made while disconnected fail with <see cref="SabhaMujicException"/>; every remote failure is surfaced as a
/// <see cref="SabhaMujicException"/> carrying the service's <see cref="ErrorCode"/>.
/// </summary>
public sealed class PipeSabhaClient : ISabhaClient
{
    private readonly PipeClientOptions _options;
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _disposed = new();
    private readonly TaskCompletionSource _firstConnected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private JsonRpc? _rpc;
    private ConnectionState _state = ConnectionState.Disconnected;
    private SessionDto? _session;
    private Task _loop = Task.CompletedTask;
    private bool _started;

    public PipeSabhaClient(PipeClientOptions? options = null)
    {
        _options = options ?? new PipeClientOptions();
        Auth = new AuthApi(this, RpcProxy.Create<IAuthApi>(CurrentRpc));
        Library = RpcProxy.Create<ILibraryApi>(CurrentRpc);
        Playback = RpcProxy.Create<IPlaybackApi>(CurrentRpc);
        Playlists = RpcProxy.Create<IPlaylistApi>(CurrentRpc);
        Admin = RpcProxy.Create<IAdminApi>(CurrentRpc);
    }

    public event EventHandler<ConnectionState>? ConnectionStateChanged;

    public event EventHandler<SessionDto?>? SessionChanged;

    public ConnectionState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    public SessionDto? Session
    {
        get
        {
            lock (_gate)
            {
                return _session;
            }
        }
    }

    /// <summary>Server information from the last successful handshake.</summary>
    public HelloResponse? ServerInfo { get; private set; }

    public IAuthApi Auth { get; }

    public ILibraryApi Library { get; }

    public IPlaybackApi Playback { get; }

    public IPlaylistApi Playlists { get; }

    public IAdminApi Admin { get; }

    public ClientEventHub Events { get; } = new();

    public Task ConnectAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed.IsCancellationRequested, this);
        lock (_gate)
        {
            if (!_started)
            {
                _started = true;
                var token = _disposed.Token;
                _loop = Task.Run(() => RunAsync(token), CancellationToken.None);
            }
        }

        return _firstConnected.Task.WaitAsync(ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed.IsCancellationRequested)
        {
            return;
        }

        await _disposed.CancelAsync().ConfigureAwait(false);
        Volatile.Read(ref _rpc)?.Dispose();
        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _firstConnected.TrySetCanceled();
        _disposed.Dispose();
    }

    private JsonRpc? CurrentRpc() => Volatile.Read(ref _rpc);

    private async Task RunAsync(CancellationToken ct)
    {
        var backoff = _options.InitialBackoff;
        var everConnected = false;
        while (!ct.IsCancellationRequested)
        {
            SetState(everConnected ? ConnectionState.Reconnecting : ConnectionState.Connecting);
            JsonRpc? rpc = null;
            try
            {
                rpc = await OpenAsync(ct).ConfigureAwait(false);
                everConnected = true;
                backoff = _options.InitialBackoff;
                Volatile.Write(ref _rpc, rpc);
                SetState(ConnectionState.Connected);
                _firstConnected.TrySetResult();
                await rpc.Completion.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (SabhaMujicException ex) when (ex.Code == ErrorCode.IncompatibleClient)
            {
                _firstConnected.TrySetException(ex);
                break;
            }
            catch (Exception)
            {
                // Connection failed or dropped: retry below.
            }
            finally
            {
                Interlocked.CompareExchange(ref _rpc, null, rpc);
                rpc?.Dispose();
            }

            if (ct.IsCancellationRequested)
            {
                break;
            }

            SetState(everConnected ? ConnectionState.Reconnecting : ConnectionState.Connecting);
            try
            {
                await Task.Delay(backoff, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, _options.MaxBackoff.Ticks));
        }

        SetState(ConnectionState.Disconnected);
    }

    /// <summary>Connects, handshakes and resumes the session before the connection is published to API callers.</summary>
    private async Task<JsonRpc> OpenAsync(CancellationToken ct)
    {
        var pipe = new NamedPipeClientStream(_options.ServerName, _options.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous,
            TokenImpersonationLevel.Identification);
        SabhaJsonRpc? rpc = null;
        try
        {
            await pipe.ConnectAsync(_options.ConnectTimeout, ct).ConfigureAwait(false);
            rpc = new SabhaJsonRpc(RpcProtocol.CreateMessageHandler(pipe, _options.MaxMessageBytes));
            rpc.AddLocalRpcTarget<IClientEvents>(new EventTarget(this), null);
            rpc.StartListening();

            ServerInfo = await CallAsync<HelloResponse>(rpc, nameof(IAuthApi.HelloAsync), [ContractVersion.Current], [typeof(int)], ct)
                .ConfigureAwait(false);

            if (Session is { } session)
            {
                try
                {
                    var resumed = await CallAsync<SessionDto>(rpc, nameof(IAuthApi.ResumeSessionAsync), [session.Token], [typeof(string)], ct)
                        .ConfigureAwait(false);
                    SetSession(resumed);
                }
                catch (SabhaMujicException ex) when (ex.Code == ErrorCode.Unauthenticated)
                {
                    SetSession(null);
                }
            }

            return rpc;
        }
        catch
        {
            if (rpc is not null)
            {
                rpc.Dispose();
            }
            else
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }
    }

    private static async Task<T> CallAsync<T>(JsonRpc rpc, string method, object?[] args, Type[] types, CancellationToken ct)
    {
        try
        {
            return await rpc.InvokeWithCancellationAsync<T>(RpcProtocol.MethodName(typeof(IAuthApi), method), args, types, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw RpcProtocol.ToSabhaException(ex);
        }
    }

    private void SetState(ConnectionState state)
    {
        lock (_gate)
        {
            if (_state == state)
            {
                return;
            }

            _state = state;
        }

        ConnectionStateChanged?.Invoke(this, state);
    }

    private void SetSession(SessionDto? session)
    {
        lock (_gate)
        {
            if (Equals(_session, session))
            {
                return;
            }

            _session = session;
        }

        SessionChanged?.Invoke(this, session);
    }

    /// <summary>Keeps <see cref="Session"/> in sync with login/resume/logout.</summary>
    private sealed class AuthApi(PipeSabhaClient owner, IAuthApi inner) : IAuthApi
    {
        public Task<HelloResponse> HelloAsync(int clientContractVersion, CancellationToken ct = default) => inner.HelloAsync(clientContractVersion, ct);

        public async Task<SessionDto> LoginAsync(string userName, string secret, CancellationToken ct = default)
        {
            var session = await inner.LoginAsync(userName, secret, ct).ConfigureAwait(false);
            owner.SetSession(session);
            return session;
        }

        public async Task<SessionDto> ResumeSessionAsync(string token, CancellationToken ct = default)
        {
            SessionDto session;
            try
            {
                session = await inner.ResumeSessionAsync(token, ct).ConfigureAwait(false);
            }
            catch (SabhaMujicException ex) when (ex.Code == ErrorCode.Unauthenticated)
            {
                owner.SetSession(null);
                throw;
            }

            owner.SetSession(session);
            return session;
        }

        public Task<bool> IsSetupRequiredAsync(CancellationToken ct = default) => inner.IsSetupRequiredAsync(ct);

        public async Task<SessionDto> CompleteInitialSetupAsync(string adminUserName, string secret, CancellationToken ct = default)
        {
            var session = await inner.CompleteInitialSetupAsync(adminUserName, secret, ct).ConfigureAwait(false);
            owner.SetSession(session);
            return session;
        }

        public async Task LogoutAsync(CancellationToken ct = default)
        {
            try
            {
                await inner.LogoutAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                owner.SetSession(null);
            }
        }
    }

    /// <summary>Receives server notifications; a server-ended session also clears the stored token.</summary>
    private sealed class EventTarget(PipeSabhaClient owner) : IClientEvents
    {
        public void PlaybackStateChanged(PlaybackStateDto state) => owner.Events.PlaybackStateChanged(state);

        public void QueueChanged(QueueDto queue) => owner.Events.QueueChanged(queue);

        public void LibraryChanged(LibraryChangedDto change) => owner.Events.LibraryChanged(change);

        public void ImportProgress(ImportProgressDto progress) => owner.Events.ImportProgress(progress);

        public void SessionEnded(string reason)
        {
            owner.SetSession(null);
            owner.Events.SessionEnded(reason);
        }
    }
}
