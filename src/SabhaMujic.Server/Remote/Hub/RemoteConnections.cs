using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using SabhaMujic.Contracts;
using SabhaMujic.Server.Api;
using SabhaMujic.Server.Application;
using SabhaMujic.Server.Remote.Http;

namespace SabhaMujic.Server.Remote.Hub;

/// <summary>
/// Per-connection state of the hub: one <see cref="ApiSession"/> and one <see cref="EventSubscription"/> per connection,
/// wired exactly like <c>PipeRpcServer</c>: <see cref="ApiSession.CallerChanged"/> → <see cref="EventSubscription.UpdateCaller"/>,
/// and <see cref="EventSubscription.SessionEnded"/> → <see cref="ApiSession.EndSession"/> + close the connection (after
/// the <c>SessionEnded</c> event was delivered, or after a short grace period). Disconnecting never touches playback.
/// </summary>
public sealed class RemoteConnections(ServerApplication app, IHubContext<RemoteHub> hub, ILogger<RemoteConnections> logger)
{
    internal static readonly TimeSpan SessionEndGrace = TimeSpan.FromSeconds(2);

    private readonly ConcurrentDictionary<string, Connection> _connections = new(StringComparer.Ordinal);

    public int Count => _connections.Count;

    /// <summary>Aborts every live hub connection (shutdown, tests). Playback is unaffected.</summary>
    public void AbortAll()
    {
        foreach (var connection in _connections.Values)
        {
            connection.Context.Abort();
        }
    }

    internal ApiSession SessionFor(string connectionId) =>
        _connections.TryGetValue(connectionId, out var connection)
            ? connection.Session
            : throw new SabhaMujicException(ErrorCode.Unauthenticated, "Sign in is required.");

    internal async Task AttachAsync(HubCallerContext context)
    {
        var http = context.GetHttpContext();
        var token = http is null ? null : RemoteAuth.GetToken(http);
        if (http is null || token is null)
        {
            throw new SabhaMujicException(ErrorCode.Unauthenticated, "Sign in is required.");
        }

        var session = app.CreateSession(ClientKind.Remote, RemoteAuth.Principal(http));
        await session.ResumeSessionAsync(token, context.ConnectionAborted).ConfigureAwait(false);

        var connection = new Connection(context, session);
        var subscription = app.Events.Subscribe(new HubEventSink(hub, connection), session.Caller, onFault: _ => context.Abort());
        connection.Subscription = subscription;
        session.CallerChanged += subscription.UpdateCaller;
        subscription.SessionEnded += connection.OnSessionEnded;
        _connections[context.ConnectionId] = connection;
        logger.LogInformation("Remote {Caller} connected.", session.Caller.Principal);
    }

    internal async Task DetachAsync(string connectionId)
    {
        if (!_connections.TryRemove(connectionId, out var connection) || connection.Subscription is not { } subscription)
        {
            return;
        }

        connection.Session.CallerChanged -= subscription.UpdateCaller;
        subscription.SessionEnded -= connection.OnSessionEnded;
        await subscription.DisposeAsync().ConfigureAwait(false);
        logger.LogInformation("Remote {Caller} disconnected.", connection.Session.Caller.Principal);
    }

    private sealed class Connection(HubCallerContext context, ApiSession session)
    {
        public HubCallerContext Context { get; } = context;

        public ApiSession Session { get; } = session;

        public EventSubscription? Subscription { get; set; }

        public void OnSessionEnded(string reason)
        {
            Session.EndSession(reason);
            // Normally the sink closes the connection right after delivering SessionEnded; this is the fallback.
            _ = Task.Delay(SessionEndGrace).ContinueWith(_ => Context.Abort(), TaskScheduler.Default);
        }
    }

    /// <summary>Delivers events as hub client calls named after the <see cref="IClientEvents"/> methods.</summary>
    private sealed class HubEventSink(IHubContext<RemoteHub> hub, Connection connection) : IEventSink
    {
        public async ValueTask DeliverAsync(ServerEvent serverEvent, CancellationToken ct)
        {
            var client = hub.Clients.Client(connection.Context.ConnectionId);
            switch (serverEvent)
            {
                case PlaybackStateEvent e:
                    await client.SendAsync(nameof(IClientEvents.PlaybackStateChanged), e.State, ct).ConfigureAwait(false);
                    break;
                case QueueEvent e:
                    await client.SendAsync(nameof(IClientEvents.QueueChanged), e.Queue, ct).ConfigureAwait(false);
                    break;
                case LibraryEvent e:
                    await client.SendAsync(nameof(IClientEvents.LibraryChanged), e.Change, ct).ConfigureAwait(false);
                    break;
                case ImportProgressEvent e:
                    await client.SendAsync(nameof(IClientEvents.ImportProgress), e.Progress, ct).ConfigureAwait(false);
                    break;
                case SessionEndedEvent e:
                    await client.SendAsync(nameof(IClientEvents.SessionEnded), e.Reason, ct).ConfigureAwait(false);
                    connection.Context.Abort();
                    break;
                default:
                    throw new NotSupportedException(serverEvent.GetType().Name);
            }
        }
    }
}
