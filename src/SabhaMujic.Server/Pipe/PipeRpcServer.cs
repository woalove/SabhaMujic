using System.Collections.Concurrent;
using System.IO.Pipes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SabhaMujic.Client.Rpc;
using SabhaMujic.Contracts;
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

/// <summary>Describes who is on the other end of a pipe connection, for the audit log only (never for authorization).</summary>
public interface ICallerIdentity
{
    string Describe(NamedPipeServerStream pipe);
}

/// <summary>Uses the OS peer identity where available (Windows user name; Unix peer user), else a generic label.</summary>
public sealed class PipeCallerIdentity : ICallerIdentity
{
    public string Describe(NamedPipeServerStream pipe)
    {
        try
        {
            var name = pipe.GetImpersonationUserName();
            return string.IsNullOrWhiteSpace(name) ? "pipe-client" : name;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or PlatformNotSupportedException)
        {
            return "pipe-client";
        }
    }
}

public sealed class PipeServerOptions
{
    public string PipeName { get; init; } = Endpoints.PipeName;

    /// <summary>Connections beyond this are closed immediately.</summary>
    public int MaxConnections { get; init; } = 32;

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
    private readonly CancellationTokenSource _stop = new();
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
    }

    public string PipeName => _options.PipeName;

    public int ConnectionCount => _connections.Count;

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
            if (_connections.Count >= _options.MaxConnections)
            {
                _logger.LogWarning("Rejecting a pipe client: {Max} connections already open.", _options.MaxConnections);
                await accepted.DisposeAsync().ConfigureAwait(false);
                continue;
            }

            var connection = new PipeConnection(this, accepted);
            _connections.TryAdd(connection, 0);
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

    private sealed class PipeConnection(PipeRpcServer server, NamedPipeServerStream pipe)
    {
        private SabhaJsonRpc? _rpc;

        public async Task RunAsync()
        {
            var principal = server._identity.Describe(pipe);
            var session = server._app.CreateSession(ClientKind.Desktop, principal);
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
            subscription.SessionEnded += session.EndSession;
            try
            {
                if (server._stop.IsCancellationRequested)
                {
                    return;
                }

                rpc.StartListening();
                await rpc.Completion.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                server._logger.LogInformation(ex, "Pipe client {Principal} disconnected abnormally.", principal);
            }
            finally
            {
                session.CallerChanged -= subscription.UpdateCaller;
                subscription.SessionEnded -= session.EndSession;
                await subscription.DisposeAsync().ConfigureAwait(false);
                rpc.Dispose();
                await pipe.DisposeAsync().ConfigureAwait(false);
            }
        }

        public async Task CloseAsync()
        {
            _rpc?.Dispose();
            await pipe.DisposeAsync().ConfigureAwait(false);
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
