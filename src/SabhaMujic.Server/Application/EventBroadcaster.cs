using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SabhaMujic.Contracts;
using SabhaMujic.Core.Security;

namespace SabhaMujic.Server.Application;

/// <summary>A server push notification (one per <see cref="IClientEvents"/> method).</summary>
public abstract record ServerEvent;

public sealed record PlaybackStateEvent(PlaybackStateDto State) : ServerEvent;

public sealed record QueueEvent(QueueDto Queue) : ServerEvent;

public sealed record LibraryEvent(LibraryChangedDto Change) : ServerEvent;

public sealed record ImportProgressEvent(ImportProgressDto Progress) : ServerEvent;

public sealed record SessionEndedEvent(string Reason) : ServerEvent;

/// <summary>Delivers events to one client over its transport (pipe notification, SignalR call, …).</summary>
public interface IEventSink
{
    ValueTask DeliverAsync(ServerEvent serverEvent, CancellationToken ct);
}

/// <summary>Adapts an <see cref="IClientEvents"/> implementation (in-process or a transport proxy) to <see cref="IEventSink"/>.</summary>
public sealed class ClientEventsSink(IClientEvents target) : IEventSink
{
    public ValueTask DeliverAsync(ServerEvent serverEvent, CancellationToken ct)
    {
        switch (serverEvent)
        {
            case PlaybackStateEvent e:
                target.PlaybackStateChanged(e.State);
                break;
            case QueueEvent e:
                target.QueueChanged(e.Queue);
                break;
            case LibraryEvent e:
                target.LibraryChanged(e.Change);
                break;
            case ImportProgressEvent e:
                target.ImportProgress(e.Progress);
                break;
            case SessionEndedEvent e:
                target.SessionEnded(e.Reason);
                break;
        }

        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Fans server events out to subscribers. Publishing never blocks: every subscriber has its own bounded queue and pump.
/// Playback-state and queue snapshots are coalesced (only the newest pending one is kept); when a queue is full the
/// oldest droppable event is discarded. <see cref="SessionEndedEvent"/> is never dropped. A subscriber whose delivery
/// throws or takes longer than the delivery timeout is removed and its fault callback runs (the transport then
/// disconnects it). Only authenticated subscribers receive playback/library events, matching the authorization matrix.
/// </summary>
public sealed class EventBroadcaster
{
    public const int DefaultCapacity = 256;

    private readonly Lock _gate = new();
    private readonly List<EventSubscription> _subscribers = [];
    private readonly ILogger _logger;

    public EventBroadcaster(ILogger<EventBroadcaster>? logger = null, int capacity = DefaultCapacity, TimeSpan? deliveryTimeout = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 2);
        _logger = logger ?? NullLogger<EventBroadcaster>.Instance;
        Capacity = capacity;
        DeliveryTimeout = deliveryTimeout ?? TimeSpan.FromSeconds(10);
    }

    public int Capacity { get; }

    public TimeSpan DeliveryTimeout { get; }

    public int SubscriberCount
    {
        get
        {
            lock (_gate)
            {
                return _subscribers.Count;
            }
        }
    }

    /// <summary>
    /// Adds a subscriber. <paramref name="onFault"/> runs (once, on a pool thread) if delivery fails or stalls.
    /// Dispose the subscription when the client goes away.
    /// </summary>
    public EventSubscription Subscribe(IEventSink sink, CallerContext caller, Action<Exception>? onFault = null)
    {
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(caller);
        var subscription = new EventSubscription(this, sink, caller, onFault);
        lock (_gate)
        {
            _subscribers.Add(subscription);
        }

        subscription.Start();
        return subscription;
    }

    public void PublishPlaybackState(PlaybackStateDto state) => Publish(new PlaybackStateEvent(state), s => s.IsAuthenticated);

    public void PublishQueue(QueueDto queue) => Publish(new QueueEvent(queue), s => s.IsAuthenticated);

    public void PublishLibraryChanged(LibraryChangedDto change) => Publish(new LibraryEvent(change), s => s.IsAuthenticated);

    /// <summary>Import progress goes only to the session that owns the upload.</summary>
    public void PublishImportProgress(string sessionId, ImportProgressDto progress) =>
        Publish(new ImportProgressEvent(progress), s => s.IsAuthenticated && s.SessionId == sessionId);

    /// <summary>
    /// Sends <see cref="IClientEvents.SessionEnded"/> to every subscriber whose caller matches, then raises their
    /// <see cref="EventSubscription.SessionEnded"/> so the transport signs the connection out. Returns how many matched.
    /// </summary>
    public int EndSessions(Func<CallerContext, bool> match, string reason)
    {
        ArgumentNullException.ThrowIfNull(match);
        var matched = Snapshot().Where(s => s.Caller.IsAuthenticated && match(s.Caller)).ToList();
        foreach (var subscription in matched)
        {
            subscription.Enqueue(new SessionEndedEvent(reason));
            subscription.RaiseSessionEnded(reason);
        }

        return matched.Count;
    }

    private void Publish(ServerEvent serverEvent, Func<CallerContext, bool> filter)
    {
        foreach (var subscription in Snapshot())
        {
            if (filter(subscription.Caller))
            {
                subscription.Enqueue(serverEvent);
            }
        }
    }

    private EventSubscription[] Snapshot()
    {
        lock (_gate)
        {
            return _subscribers.ToArray();
        }
    }

    internal void Remove(EventSubscription subscription)
    {
        lock (_gate)
        {
            _subscribers.Remove(subscription);
        }
    }

    internal ILogger Logger => _logger;
}

/// <summary>One subscriber's queue and delivery pump.</summary>
public sealed class EventSubscription : IAsyncDisposable
{
    private readonly EventBroadcaster _owner;
    private readonly IEventSink _sink;
    private readonly Action<Exception>? _onFault;
    private readonly Lock _gate = new();
    private readonly LinkedList<ServerEvent> _pending = new();
    private readonly Channel<bool> _wakeup = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly CancellationTokenSource _stop = new();
    private CallerContext _caller;
    private Task _pump = Task.CompletedTask;
    private int _closed;

    internal EventSubscription(EventBroadcaster owner, IEventSink sink, CallerContext caller, Action<Exception>? onFault)
    {
        _owner = owner;
        _sink = sink;
        _caller = caller;
        _onFault = onFault;
    }

    /// <summary>Raised (synchronously, from the revoking thread) when the subscriber's session was ended server-side.</summary>
    public event Action<string>? SessionEnded;

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

    public int PendingCount
    {
        get
        {
            lock (_gate)
            {
                return _pending.Count;
            }
        }
    }

    public bool IsClosed => Volatile.Read(ref _closed) != 0;

    /// <summary>Called by the transport when the connection signs in or out.</summary>
    public void UpdateCaller(CallerContext caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        lock (_gate)
        {
            _caller = caller;
        }
    }

    public async ValueTask DisposeAsync()
    {
        Close();
        try
        {
            await _pump.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _stop.Dispose();
    }

    internal void Start() => _pump = Task.Run(PumpAsync);

    internal void Enqueue(ServerEvent serverEvent)
    {
        lock (_gate)
        {
            if (IsClosed)
            {
                return;
            }

            if (serverEvent is PlaybackStateEvent or QueueEvent)
            {
                // Snapshots: only the newest matters.
                for (var node = _pending.First; node is not null; node = node.Next)
                {
                    if (node.Value.GetType() == serverEvent.GetType())
                    {
                        _pending.Remove(node);
                        break;
                    }
                }
            }

            if (_pending.Count >= _owner.Capacity)
            {
                var victim = _pending.First;
                while (victim is not null && victim.Value is SessionEndedEvent)
                {
                    victim = victim.Next;
                }

                if (victim is null)
                {
                    return;
                }

                _pending.Remove(victim);
            }

            _pending.AddLast(serverEvent);
        }

        _wakeup.Writer.TryWrite(true);
    }

    internal void RaiseSessionEnded(string reason)
    {
        try
        {
            SessionEnded?.Invoke(reason);
        }
        catch (Exception ex)
        {
            _owner.Logger.LogWarning(ex, "SessionEnded handler failed.");
        }
    }

    private bool TryDequeue(out ServerEvent serverEvent)
    {
        lock (_gate)
        {
            if (_pending.First is { } first)
            {
                _pending.RemoveFirst();
                serverEvent = first.Value;
                return true;
            }
        }

        serverEvent = null!;
        return false;
    }

    private async Task PumpAsync()
    {
        var ct = _stop.Token;
        try
        {
            while (await _wakeup.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                _wakeup.Reader.TryRead(out _);
                while (!ct.IsCancellationRequested && TryDequeue(out var serverEvent))
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(_owner.DeliveryTimeout);
                    // WaitAsync also bounds sinks that ignore the token.
                    await _sink.DeliverAsync(serverEvent, timeout.Token).AsTask()
                        .WaitAsync(_owner.DeliveryTimeout, ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _owner.Logger.LogInformation(ex, "Dropping an event subscriber whose delivery failed or stalled.");
            Close();
            if (_onFault is { } onFault)
            {
                _ = Task.Run(() =>
                {
                    try
                    {
                        onFault(ex);
                    }
                    catch (Exception faultEx)
                    {
                        _owner.Logger.LogWarning(faultEx, "Event subscriber fault handler failed.");
                    }
                });
            }
        }
    }

    private void Close()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return;
        }

        _owner.Remove(this);
        lock (_gate)
        {
            _pending.Clear();
        }

        _wakeup.Writer.TryComplete();
        try
        {
            _stop.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }
}
