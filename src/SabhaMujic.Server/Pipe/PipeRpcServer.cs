using System.Collections.Concurrent;
using System.IO.Pipes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SabhaMujic.Client.Rpc;
using SabhaMujic.Contracts;
using SabhaMujic.Core.Security;
using SabhaMujic.Server.Api;
using SabhaMujic.Server.Application;
using StreamJsonRpc;

namespace SabhaMujic.Server.Pipe;

/// <summary>
/// Creates listening pipe instances. The Windows service supplies an implementation that applies the pipe ACL
/// (Authenticated Users: connect; SYSTEM + service: full control).
/// </summary>
public interface IPipeStreamFactory
{
    NamedPipeServerStream CreateServerStream(string pipeName);
}

/// <summary>Default pipe instances (no custom ACL). On Linux .NET maps them to Unix domain sockets.</summary>
public sealed class DefaultPipeStreamFactory : IPipeStreamFactory
{
    public NamedPipeServerStream CreateServerStream(string pipeName) =>
        new(pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
}

/// <summary>
/// What the OS says about the process on the other end of a pipe connection.
/// <see cref="Principal"/> is for the audit log only. <see cref="IsElevatedAdministrator"/> gates first-run setup
/// (decision #24) and must only be true when the client's token is an elevated local Administrator.
/// </summary>
public sealed record PipeCallerInfo(string Principal, bool IsElevatedAdministrator);

/// <summary>Identifies the client of a pipe connection. The Windows service supplies a token-based implementation.</summary>
public interface ICallerIdentity
{
    PipeCallerInfo Describe(NamedPipeServerStream pipe);
}

/// <summary>
/// Default (cross-platform) identity: the OS peer user name where available, else a generic label; never elevated.
/// </summary>
public sealed class PipeCallerIdentity : ICallerIdentity
{
    public PipeCallerInfo Describe(NamedPipeServerStream pipe)
    {
        try
        {
            var name = pipe.GetImpersonationUserName();
            return new PipeCallerInfo(string.IsNullOrWhiteSpace(name) ? "pipe-client" : name, false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or PlatformNotSupportedException)
        {
            return new PipeCallerInfo("pipe-client", false);
        }
    }
}

/// <summary>
/// Pipe limits. Any local user can open the pipe (by design), so slots are protected against idle squatters:
/// <list type="bullet">
/// <item>Main defence, deadlines for anonymous connections: <see cref="HelloTimeout"/> to complete the handshake, then
/// <see cref="AuthenticationTimeout"/> to sign in (counted again from each sign-out). On expiry the connection is closed;
/// the desktop app reconnects quietly while signed out.</item>
/// <item>Room for real users: anonymous connections may hold at most <see cref="MaxConnections"/> minus
/// <see cref="ReservedConnections"/> slots, and when the pipe is full a newcomer replaces the oldest anonymous
/// connection. Only when every slot is signed in is a newcomer refused.</item>
/// <item>Optionally (the Windows service) at most <see cref="MaxConnectionsPerPrincipal"/> connections per OS account,
/// again making room by closing that account's oldest anonymous connection.</item>
/// </list>
/// Rejected and closed connections are logged with their OS principal.
/// </summary>
public sealed class PipeServerOptions
{
    public string PipeName { get; init; } = Endpoints.PipeName;

    /// <summary>Hard cap on open connections (see the class remarks for how anonymous ones make room).</summary>
    public int MaxConnections { get; init; } = 32;

    /// <summary>Slots anonymous connections can never occupy together (default: a quarter of <see cref="MaxConnections"/>).</summary>
    public int? ReservedConnections { get; init; }

    /// <summary>Per OS account (impersonation principal); null = no per-account cap (Linux/dev, where all tests share one account).</summary>
    public int? MaxConnectionsPerPrincipal { get; init; }

    /// <summary>A connection that has not completed Hello by then is closed. Null disables it.</summary>
    public TimeSpan? HelloTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>A connection still signed out this long after Hello (or after signing out) is closed. Null disables it.</summary>
    public TimeSpan? AuthenticationTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Largest accepted JSON-RPC frame body. Upload chunks (1 MiB, base64) must fit.</summary>
    public int MaxMessageBytes { get; init; } = 4 * 1024 * 1024;
}

/// <summary>
/// Named-pipe JSON-RPC server for the desktop app. Each connection gets its own <see cref="Api.ApiSession"/> (the
/// authorization choke point) exposed as the five contract interfaces with method names "&lt;Interface&gt;.&lt;Method&gt;",
/// plus an event subscription that pushes <see cref="IClientEvents"/> notifications. A failing or misbehaving
/// connection (oversized/garbled frames, stalled event delivery) is closed without affecting the others.
/// Disconnecting never touches playback.
/// </summary>
public sealed class PipeRpcServer : IAsyncDisposable
{
    private readonly ServerApplication _app;
    private readonly PipeServerOptions _options;
    private readonly IPipeStreamFactory _pipes;
    private readonly ICallerIdentity _identity;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<PipeConnection, byte> _connections = new();
    private readonly Lock _admission = new();
    private readonly CancellationTokenSource _stop = new();
    private long _sequence;
    private Task _acceptLoop = Task.CompletedTask;

    public PipeRpcServer(ServerApplication app, PipeServerOptions? options = null, IPipeStreamFactory? pipes = null,
        ICallerIdentity? identity = null, ILogger<PipeRpcServer>? logger = null)
    {
        _app = app;
        _options = options ?? new PipeServerOptions();
        _pipes = pipes ?? new DefaultPipeStreamFactory();
        _identity = identity ?? new PipeCallerIdentity();
        _logger = logger ?? NullLogger<PipeRpcServer>.Instance;
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.MaxConnections, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(_options.ReservedConnections ?? 0);
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.MaxConnectionsPerPrincipal ?? 1, 1);
        MaxAnonymousConnections = Math.Max(1, _options.MaxConnections - (_options.ReservedConnections ?? _options.MaxConnections / 4));
    }

    public string PipeName => _options.PipeName;

    public int ConnectionCount => _connections.Count;

    /// <summary>Most signed-out connections held at once; beyond it the oldest is closed for the newcomer.</summary>
    public int MaxAnonymousConnections { get; }

    /// <summary>Open connections that are not signed in (diagnostics/tests).</summary>
    public int AnonymousConnectionCount => _connections.Keys.Count(c => !c.IsAuthenticated && !c.IsClosing);

    /// <summary>Starts listening. The first pipe instance exists when this returns, so clients can connect immediately.</summary>
    public void Start()
    {
        var first = _pipes.CreateServerStream(_options.PipeName);
        _acceptLoop = Task.Run(() => AcceptLoopAsync(first, _stop.Token));
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        try
        {
            await _acceptLoop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        await Task.WhenAll(_connections.Keys.Select(c => c.CloseAsync())).ConfigureAwait(false);
        _stop.Dispose();
    }

    private async Task AcceptLoopAsync(NamedPipeServerStream? pipe, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                pipe ??= _pipes.CreateServerStream(_options.PipeName);
                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Never let the listener die: log, back off and create a fresh instance.
                _logger.LogWarning(ex, "Pipe accept failed; retrying.");
                if (pipe is not null)
                {
                    await pipe.DisposeAsync().ConfigureAwait(false);
                    pipe = null;
                }

                try
                {
                    await Task.Delay(250, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                continue;
            }

            var accepted = pipe;
            pipe = null;
            var info = Identify(accepted);
            var connection = new PipeConnection(this, accepted, info, Interlocked.Increment(ref _sequence));
            if (!Admit(connection, out var evicted))
            {
                await accepted.DisposeAsync().ConfigureAwait(false);
                continue;
            }

            foreach (var victim in evicted)
            {
                _ = victim.CloseAsync();
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await connection.RunAsync().ConfigureAwait(false);
                }
                finally
                {
                    _connections.TryRemove(connection, out _);
                }
            }, CancellationToken.None);
        }

        if (pipe is not null)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
        }
    }

    private PipeCallerInfo Identify(NamedPipeServerStream pipe)
    {
        try
        {
            return _identity.Describe(pipe);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not identify a pipe client; treating it as a standard user.");
            return new PipeCallerInfo("pipe-client", false);
        }
    }

    /// <summary>
    /// Registers <paramref name="connection"/> if the limits allow, choosing anonymous connections to close for it
    /// (oldest first). False (nothing changed) when it must be refused.
    /// </summary>
    private bool Admit(PipeConnection connection, out List<PipeConnection> evictedConnections)
    {
        var evicted = new List<PipeConnection>();
        evictedConnections = evicted;
        lock (_admission)
        {
            var open = _connections.Keys.Where(c => !c.IsClosing).OrderBy(c => c.Sequence).ToList();
            var anonymous = open.Where(c => !c.IsAuthenticated).ToList();

            if (_options.MaxConnectionsPerPrincipal is { } perPrincipal)
            {
                var same = open.Where(c => string.Equals(c.Principal, connection.Principal, StringComparison.OrdinalIgnoreCase)).ToList();
                if (same.Count >= perPrincipal)
                {
                    var victim = same.FirstOrDefault(c => !c.IsAuthenticated);
                    if (victim is null)
                    {
                        _logger.LogWarning("Refused a pipe connection from {Principal}: it already has {Max} signed-in connections.",
                            connection.Principal, perPrincipal);
                        return false;
                    }

                    evicted.Add(victim);
                }
            }

            int remainingAnonymous = anonymous.Count - evicted.Count;
            int remainingTotal = open.Count - evicted.Count;
            if (remainingAnonymous >= MaxAnonymousConnections || remainingTotal >= _options.MaxConnections)
            {
                var victim = anonymous.FirstOrDefault(c => !evicted.Contains(c));
                if (victim is null)
                {
                    _logger.LogWarning("Refused a pipe connection from {Principal}: all {Max} connections are signed in.",
                        connection.Principal, _options.MaxConnections);
                    evicted.Clear();
                    return false;
                }

                evicted.Add(victim);
            }

            foreach (var victim in evicted)
            {
                victim.MarkClosing();
                _logger.LogWarning("Closing an idle signed-out pipe connection from {Principal} to make room for a new one from {Newcomer}.",
                    victim.Principal, connection.Principal);
            }

            _connections.TryAdd(connection, 0);
            return true;
        }
    }

    private sealed class PipeConnection(PipeRpcServer server, NamedPipeServerStream pipe, PipeCallerInfo info, long sequence)
    {
        private SabhaJsonRpc? _rpc;
        private ApiSession? _session;
        private int _closing;
        private long _anonymousSince = Environment.TickCount64;

        public long Sequence { get; } = sequence;

        public string Principal => info.Principal;

        /// <summary>Signed in right now (a closing connection counts as anonymous only until it is gone).</summary>
        public bool IsAuthenticated => Volatile.Read(ref _session)?.Caller.IsAuthenticated == true;

        public bool IsClosing => Volatile.Read(ref _closing) != 0;

        public void MarkClosing() => Volatile.Write(ref _closing, 1);

        public async Task RunAsync()
        {
            var principal = info.Principal;
            var session = server._app.CreateSession(ClientKind.Desktop, principal, info.IsElevatedAdministrator);
            Volatile.Write(ref _session, session);
            var rpc = new SabhaJsonRpc(RpcProtocol.CreateMessageHandler(pipe, server._options.MaxMessageBytes))
            {
                CancelLocallyInvokedMethodsWhenConnectionIsClosed = true,
                UnexpectedException = ex => server._logger.LogError(ex, "Unexpected exception in an RPC method."),
            };
            _rpc = rpc;
            foreach (var api in RpcProtocol.ApiInterfaces)
            {
                rpc.AddLocalRpcTarget(api, session, new JsonRpcTargetOptions
                {
                    MethodNameTransform = CommonMethodNameTransforms.Prepend(RpcProtocol.MethodPrefix(api)),
                    NotifyClientOfEvents = false,
                    DisposeOnDisconnect = false,
                });
            }

            var subscription = server._app.Events.Subscribe(new RpcEventSink(rpc), session.Caller, onFault: _ => rpc.Dispose());
            session.CallerChanged += subscription.UpdateCaller;
            session.CallerChanged += OnCallerChanged;
            subscription.SessionEnded += session.EndSession;
            using var watchdogStop = new CancellationTokenSource();
            var watchdog = Task.CompletedTask;
            try
            {
                if (server._stop.IsCancellationRequested || IsClosing)
                {
                    return;
                }

                rpc.StartListening();
                watchdog = WatchDeadlinesAsync(session, watchdogStop.Token);
                await rpc.Completion.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                server._logger.LogInformation(ex, "Pipe client {Principal} disconnected abnormally.", principal);
            }
            finally
            {
                await watchdogStop.CancelAsync().ConfigureAwait(false);
                await watchdog.ConfigureAwait(false);
                session.CallerChanged -= subscription.UpdateCaller;
                session.CallerChanged -= OnCallerChanged;
                subscription.SessionEnded -= session.EndSession;
                await subscription.DisposeAsync().ConfigureAwait(false);
                rpc.Dispose();
                await pipe.DisposeAsync().ConfigureAwait(false);
            }
        }

        public async Task CloseAsync()
        {
            MarkClosing();
            _rpc?.Dispose();
            await pipe.DisposeAsync().ConfigureAwait(false);
        }

        private void OnCallerChanged(CallerContext caller)
        {
            if (!caller.IsAuthenticated)
            {
                Volatile.Write(ref _anonymousSince, Environment.TickCount64); // signed out: the sign-in deadline starts again
            }
        }

        /// <summary>Closes the connection when it misses the Hello deadline, or stays signed out past the sign-in deadline.</summary>
        private async Task WatchDeadlinesAsync(ApiSession session, CancellationToken ct)
        {
            var options = server._options;
            if (options.HelloTimeout is null && options.AuthenticationTimeout is null)
            {
                return;
            }

            var shortest = Math.Min(options.HelloTimeout?.TotalMilliseconds ?? double.MaxValue, options.AuthenticationTimeout?.TotalMilliseconds ?? double.MaxValue);
            var poll = TimeSpan.FromMilliseconds(Math.Clamp(shortest / 4, 10, 1_000));
            var connectedAt = Environment.TickCount64;
            var helloSeen = false;
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(poll, ct).ConfigureAwait(false);
                    var now = Environment.TickCount64;
                    if (!session.HelloCompleted)
                    {
                        if (options.HelloTimeout is { } helloTimeout && now - connectedAt >= helloTimeout.TotalMilliseconds)
                        {
                            server._logger.LogWarning("Closing a pipe connection from {Principal}: no handshake within {Seconds} s.",
                                Principal, helloTimeout.TotalSeconds);
                            await CloseAsync().ConfigureAwait(false);
                            return;
                        }

                        continue;
                    }

                    if (!helloSeen)
                    {
                        // The sign-in deadline counts from the handshake (or a later sign-out), not from the connect.
                        helloSeen = true;
                        Volatile.Write(ref _anonymousSince, Math.Max(Volatile.Read(ref _anonymousSince), now));
                    }

                    if (options.AuthenticationTimeout is not { } authTimeout || IsAuthenticated || session.HasCallInProgress)
                    {
                        continue;
                    }

                    if (now - Volatile.Read(ref _anonymousSince) >= authTimeout.TotalMilliseconds)
                    {
                        server._logger.LogInformation("Closing a pipe connection from {Principal}: not signed in within {Seconds} s.",
                            Principal, authTimeout.TotalSeconds);
                        await CloseAsync().ConfigureAwait(false);
                        return;
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    /// <summary>Sends events as JSON-RPC notifications named after the <see cref="IClientEvents"/> methods.</summary>
    private sealed class RpcEventSink(JsonRpc rpc) : IEventSink
    {
        public ValueTask DeliverAsync(ServerEvent serverEvent, CancellationToken ct)
        {
            var (method, argument, type) = serverEvent switch
            {
                PlaybackStateEvent e => (nameof(IClientEvents.PlaybackStateChanged), (object)e.State, typeof(PlaybackStateDto)),
                QueueEvent e => (nameof(IClientEvents.QueueChanged), e.Queue, typeof(QueueDto)),
                LibraryEvent e => (nameof(IClientEvents.LibraryChanged), e.Change, typeof(LibraryChangedDto)),
                ImportProgressEvent e => (nameof(IClientEvents.ImportProgress), e.Progress, typeof(ImportProgressDto)),
                SessionEndedEvent e => (nameof(IClientEvents.SessionEnded), e.Reason, typeof(string)),
                _ => throw new NotSupportedException(serverEvent.GetType().Name),
            };
            return new ValueTask(rpc.NotifyAsync(method, [argument], [type]).WaitAsync(ct));
        }
    }
}
