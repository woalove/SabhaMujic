using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using SabhaMujic.Client;
using SabhaMujic.Contracts;

namespace SabhaMujic.Reliability;

/// <summary>
/// What one client has been told by the server's push events: the latest state and queue, with per-epoch checks that
/// revisions only ever increase. The initial fetch after connecting seeds the view (like the real apps do) without being
/// checked, because a reply may legitimately be older than an event that overtook it.
/// </summary>
public sealed class ClientView(string name, Action<string, string> violation, Action<double> lag)
{
    private readonly Lock _gate = new();
    private PlaybackStateDto? _state;
    private QueueDto? _queue;
    private long _eventsReceived;

    public string Name { get; } = name;

    public long EventsReceived => Interlocked.Read(ref _eventsReceived);

    public PlaybackStateDto? State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    public QueueDto? Queue
    {
        get
        {
            lock (_gate)
            {
                return _queue;
            }
        }
    }

    public void OnState(PlaybackStateDto state)
    {
        Interlocked.Increment(ref _eventsReceived);
        lag((DateTimeOffset.UtcNow - state.Timestamp).TotalMilliseconds);
        lock (_gate)
        {
            if (_state is { } last && last.ServerEpoch == state.ServerEpoch && state.Revision <= last.Revision)
            {
                violation("StateRevisionOrder", $"{Name}: state revision {state.Revision} after {last.Revision}");
                return;
            }

            _state = state;
        }
    }

    public void OnQueue(QueueDto queue)
    {
        Interlocked.Increment(ref _eventsReceived);
        lock (_gate)
        {
            if (_queue is { } last && last.ServerEpoch == queue.ServerEpoch && queue.Revision <= last.Revision)
            {
                violation("QueueRevisionOrder", $"{Name}: queue revision {queue.Revision} after {last.Revision}");
                return;
            }

            _queue = queue;
        }
    }

    /// <summary>Seeds from a direct fetch unless a newer event already arrived.</summary>
    public void Seed(PlaybackStateDto state, QueueDto queue)
    {
        lock (_gate)
        {
            if (_state is null || _state.ServerEpoch != state.ServerEpoch || _state.Revision < state.Revision)
            {
                _state = state;
            }

            if (_queue is null || _queue.ServerEpoch != queue.ServerEpoch || _queue.Revision < queue.Revision)
            {
                _queue = queue;
            }
        }
    }

    /// <summary>True when the view shows the server's current state (position and revision of the state aside) and queue.</summary>
    public bool Matches(Guid epoch, PlaybackStateDto server, QueueDto serverQueue, out string difference)
    {
        var state = State;
        var queue = Queue;
        if (state is null || queue is null)
        {
            difference = "nothing received";
            return false;
        }

        if (state.ServerEpoch != epoch || queue.ServerEpoch != epoch)
        {
            difference = "old server epoch";
            return false;
        }

        if (queue.Revision != serverQueue.Revision)
        {
            difference = $"queue revision {queue.Revision} vs server {serverQueue.Revision}";
            return false;
        }

        if (state.Status != server.Status || state.CurrentQueueItemId != server.CurrentQueueItemId || state.LoopMode != server.LoopMode
            || state.Volume != server.Volume || state.Muted != server.Muted || state.CrossfadeSeconds != server.CrossfadeSeconds)
        {
            difference = $"state {state.Status}/{state.CurrentQueueItemId}/{state.LoopMode}/{state.Volume}/{state.Muted}/{state.CrossfadeSeconds} " +
                         $"vs server {server.Status}/{server.CurrentQueueItemId}/{server.LoopMode}/{server.Volume}/{server.Muted}/{server.CrossfadeSeconds}";
            return false;
        }

        difference = string.Empty;
        return true;
    }
}

/// <summary>A real desktop-protocol client (<see cref="PipeSabhaClient"/>) signed in as one user.</summary>
public sealed class PipeSoakClient : IAsyncDisposable
{
    private int _connections;

    private PipeSoakClient(PipeSabhaClient client, ClientView view, bool isAdmin)
    {
        Client = client;
        View = view;
        IsAdmin = isAdmin;
        _connections = 1;
        client.ConnectionStateChanged += (_, state) =>
        {
            if (state == ConnectionState.Connected)
            {
                Interlocked.Increment(ref _connections);
            }
        };
    }

    /// <summary>How many times the client has (re)connected.</summary>
    public int Connections => Volatile.Read(ref _connections);

    public PipeSabhaClient Client { get; }

    public ClientView View { get; }

    public bool IsAdmin { get; }

    public bool IsConnected => Client.State == ConnectionState.Connected && Client.Session is not null;

    public static async Task<PipeSoakClient> ConnectAsync(string pipeName, string name, string? user, string? secret, bool isAdmin,
        Action<string, string> violation, Action<double> lag, CancellationToken ct)
    {
        var client = new PipeSabhaClient(new PipeClientOptions
        {
            PipeName = pipeName,
            ConnectTimeout = TimeSpan.FromSeconds(2),
            InitialBackoff = TimeSpan.FromMilliseconds(50),
            MaxBackoff = TimeSpan.FromMilliseconds(500),
        });
        var view = new ClientView(name, violation, lag);
        client.Events.PlaybackStateReceived += view.OnState;
        client.Events.QueueReceived += view.OnQueue;
        try
        {
            await client.ConnectAsync(ct).ConfigureAwait(false);
            if (user is not null)
            {
                await client.Auth.LoginAsync(user, secret!, ct).ConfigureAwait(false);
                view.Seed(await client.Playback.GetStateAsync(ct).ConfigureAwait(false), await client.Playback.GetQueueAsync(ct).ConfigureAwait(false));
            }
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return new PipeSoakClient(client, view, isAdmin);
    }

    /// <summary>Re-seeds after the client reconnected on its own (e.g. after a service restart).</summary>
    public async Task ReseedAsync(CancellationToken ct) =>
        View.Seed(await Client.Playback.GetStateAsync(ct).ConfigureAwait(false), await Client.Playback.GetQueueAsync(ct).ConfigureAwait(false));

    public ValueTask DisposeAsync() => Client.DisposeAsync();
}

/// <summary>A paired phone: a SignalR hub connection authenticated with a device token.</summary>
public sealed class RemoteSoakClient : IAsyncDisposable
{
    private RemoteSoakClient(HubConnection hub, ClientView view, string token)
    {
        Hub = hub;
        View = view;
        Token = token;
    }

    public HubConnection Hub { get; }

    public ClientView View { get; }

    public string Token { get; }

    public bool IsConnected => Hub.State == HubConnectionState.Connected;

    public static async Task<RemoteSoakClient> ConnectAsync(SoakRemoteComponent remote, string token, string name,
        Action<string, string> violation, Action<double> lag, CancellationToken ct)
    {
        var hub = remote.Hub(token);
        var view = new ClientView(name, violation, lag);
        hub.On<PlaybackStateDto>(nameof(IClientEvents.PlaybackStateChanged), view.OnState);
        hub.On<QueueDto>(nameof(IClientEvents.QueueChanged), view.OnQueue);
        try
        {
            await hub.StartAsync(ct).ConfigureAwait(false);
            view.Seed(await hub.InvokeAsync<PlaybackStateDto>("GetState", ct).ConfigureAwait(false),
                await hub.InvokeAsync<QueueDto>("GetQueue", ct).ConfigureAwait(false));
        }
        catch
        {
            await hub.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return new RemoteSoakClient(hub, view, token);
    }

    public ValueTask DisposeAsync() => Hub.DisposeAsync();
}

public static class HubErrors
{
    /// <summary>The <see cref="ErrorCode"/> carried by a hub error (<c>… HubException: {"code":…}</c>), if any.</summary>
    public static ErrorCode? CodeOf(HubException ex)
    {
        const string marker = "HubException: ";
        var at = ex.Message.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0)
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(ex.Message[(at + marker.Length)..]);
            return doc.RootElement.TryGetProperty("code", out var code) && Enum.TryParse<ErrorCode>(code.GetString(), out var parsed) ? parsed : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
