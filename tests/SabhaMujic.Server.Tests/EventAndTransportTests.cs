using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using SabhaMujic.Client;
using SabhaMujic.Contracts;
using SabhaMujic.Core.Security;
using SabhaMujic.Server.Application;

namespace SabhaMujic.Server.Tests;

public sealed class EventBroadcasterTests
{
    private static readonly CallerContext SignedIn = new(ClientKind.Desktop, Role.Volunteer, Guid.NewGuid(), null, "s1", "t");

    private static PlaybackStateDto State(long revision) =>
        new(PlaybackStatus.Playing, null, null, null, 0, 0, 1, false, LoopMode.Off, 6, null, revision, DateTimeOffset.UnixEpoch);

    private sealed class RecordingSink : IEventSink
    {
        public ConcurrentQueue<ServerEvent> Received { get; } = new();

        public ValueTask DeliverAsync(ServerEvent serverEvent, CancellationToken ct)
        {
            Received.Enqueue(serverEvent);
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>Never completes and ignores cancellation, like a client that stopped reading.</summary>
    private sealed class StalledSink : IEventSink
    {
        public int Calls;

        public ValueTask DeliverAsync(ServerEvent serverEvent, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return new ValueTask(new TaskCompletionSource().Task);
        }
    }

    [Fact]
    public async Task A_stalled_subscriber_does_not_block_others_and_is_dropped()
    {
        var broadcaster = new EventBroadcaster(capacity: 16, deliveryTimeout: TimeSpan.FromMilliseconds(300));
        var stalled = new StalledSink();
        var faulted = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var bad = broadcaster.Subscribe(stalled, SignedIn, ex => faulted.TrySetResult(ex));
        var good = new RecordingSink();
        await using var ok = broadcaster.Subscribe(good, SignedIn);

        var publishTime = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 1_000; i++)
        {
            broadcaster.PublishLibraryChanged(new LibraryChangedDto(LibraryChangeKind.TrackAdded, Guid.NewGuid()));
            broadcaster.PublishPlaybackState(State(i));
        }

        Assert.True(publishTime.ElapsedMilliseconds < 2_000, "publishing must never wait for subscribers");
        Assert.True(bad.PendingCount <= broadcaster.Capacity, "the stalled queue stays bounded");

        await AssertEx.Eventually(() => good.Received.OfType<PlaybackStateEvent>().Any(e => e.State.Revision == 999), "healthy subscriber gets the latest state");
        Assert.IsType<TimeoutException>(await faulted.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(bad.IsClosed);
        Assert.Equal(1, broadcaster.SubscriberCount);
        Assert.Equal(1, stalled.Calls);
    }

    [Fact]
    public async Task Pending_state_snapshots_are_coalesced_and_session_ended_is_never_dropped()
    {
        var broadcaster = new EventBroadcaster(capacity: 4, deliveryTimeout: TimeSpan.FromSeconds(30));
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var received = new ConcurrentQueue<ServerEvent>();
        var sink = new GatedSink(gate.Task, received);
        await using var sub = broadcaster.Subscribe(sink, SignedIn);

        broadcaster.PublishPlaybackState(State(0)); // taken by the pump, blocks on the gate
        await AssertEx.Eventually(() => sink.Started, "pump started");
        broadcaster.EndSessions(c => c.SessionId == "s1", "bye");
        for (var i = 1; i <= 100; i++)
        {
            broadcaster.PublishPlaybackState(State(i));
            broadcaster.PublishLibraryChanged(new LibraryChangedDto(LibraryChangeKind.TrackUpdated, null));
        }

        Assert.True(sub.PendingCount <= 4);
        gate.SetResult();
        await AssertEx.Eventually(() => received.OfType<PlaybackStateEvent>().Any(e => e.State.Revision == 100), "latest state delivered");
        Assert.Single(received.OfType<SessionEndedEvent>());
        Assert.True(received.OfType<PlaybackStateEvent>().Count() <= 2);
    }

    [Fact]
    public async Task Anonymous_subscribers_get_no_library_or_playback_events_and_progress_is_per_session()
    {
        var broadcaster = new EventBroadcaster();
        var anonymous = new RecordingSink();
        var other = new RecordingSink();
        var owner = new RecordingSink();
        await using var a = broadcaster.Subscribe(anonymous, CallerContext.Anonymous(ClientKind.Desktop, "x"));
        await using var o = broadcaster.Subscribe(other, SignedIn with { SessionId = "s2" });
        await using var w = broadcaster.Subscribe(owner, SignedIn);

        broadcaster.PublishPlaybackState(State(1));
        broadcaster.PublishImportProgress("s1", new ImportProgressDto(Guid.NewGuid(), "a.mp3", 1, 2, "Uploading"));
        await AssertEx.Eventually(() => owner.Received.Count == 2 && other.Received.Count == 1, "delivery");
        Assert.Empty(anonymous.Received);
        Assert.IsType<PlaybackStateEvent>(Assert.Single(other.Received));
    }

    private sealed class GatedSink(Task gate, ConcurrentQueue<ServerEvent> received) : IEventSink
    {
        public volatile bool Started;

        public async ValueTask DeliverAsync(ServerEvent serverEvent, CancellationToken ct)
        {
            Started = true;
            await gate;
            received.Enqueue(serverEvent);
        }
    }
}

public sealed class TransportTests : IAsyncLifetime
{
    private TestServer _server = null!;

    public async Task InitializeAsync() => _server = await TestServer.StartAsync(maxConnections: 8, maxMessageBytes: 2 * 1024 * 1024);

    public async Task DisposeAsync() => await _server.DisposeAsync();

    [Fact]
    public async Task Events_fan_out_to_every_signed_in_client()
    {
        var a = await _server.ConnectVolunteerAsync();
        var b = await _server.ConnectAdminAsync();
        var anonymous = await _server.ConnectAsync();
        var gotA = new ConcurrentQueue<PlaybackStateDto>();
        var gotB = new ConcurrentQueue<PlaybackStateDto>();
        var gotAnon = new ConcurrentQueue<PlaybackStateDto>();
        var queueB = new ConcurrentQueue<QueueDto>();
        a.Events.PlaybackStateReceived += gotA.Enqueue;
        b.Events.PlaybackStateReceived += gotB.Enqueue;
        b.Events.QueueReceived += queueB.Enqueue;
        anonymous.Events.PlaybackStateReceived += gotAnon.Enqueue;

        await a.Playback.SetVolumeAsync(0.25f);
        await AssertEx.Eventually(() => gotA.Any(s => s.Volume == 0.25f) && gotB.Any(s => s.Volume == 0.25f), "both clients see the change");

        await b.Playback.ClearQueueAsync();
        await AssertEx.Eventually(() => !queueB.IsEmpty, "queue event");
        await Task.Delay(100);
        Assert.Empty(gotAnon);
    }

    [Fact]
    public async Task Client_reconnects_and_resumes_after_a_service_restart()
    {
        var client = await _server.ConnectVolunteerAsync();
        var token = client.Session!.Token;
        var states = new ConcurrentQueue<ConnectionState>();
        client.ConnectionStateChanged += (_, s) => states.Enqueue(s);

        await _server.StopPipeAsync();
        await AssertEx.Eventually(() => client.State == ConnectionState.Reconnecting, "client notices the drop");
        await AssertEx.ThrowsCode(ErrorCode.Unavailable, () => client.Playback.GetStateAsync());

        _server.StartPipe();
        await AssertEx.Eventually(() => client.State == ConnectionState.Connected, "client reconnects");
        Assert.Equal(token, client.Session?.Token);
        await client.Playback.GetStateAsync(); // the session was resumed on the new connection
        Assert.Contains(ConnectionState.Reconnecting, states);

        // Events flow on the new connection too.
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Events.PlaybackStateReceived += _ => received.TrySetResult();
        _server.Backend.RaiseState();
        await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task A_revoked_session_is_not_resumed_after_reconnect()
    {
        var client = await _server.ConnectVolunteerAsync();
        await _server.StopPipeAsync();
        await _server.App.Sessions.RevokeAllForUserAsync(_server.VolunteerId);
        _server.StartPipe();
        await AssertEx.Eventually(() => client.State == ConnectionState.Connected && client.Session is null, "reconnected signed out");
        await AssertEx.ThrowsCode(ErrorCode.Unauthenticated, () => client.Playback.GetStateAsync());
    }

    [Fact]
    public async Task Disconnecting_every_client_never_touches_playback()
    {
        var clients = new List<PipeSabhaClient>
        {
            await _server.ConnectVolunteerAsync(),
            await _server.ConnectAdminAsync(),
            await _server.ConnectAsync(),
        };
        _server.Backend.SetPlaying();
        var commandsBefore = _server.Backend.Commands.Count;

        // A raw client that vanishes mid-request.
        var raw = new NamedPipeClientStream(".", _server.PipeOptions.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await raw.ConnectAsync(5_000);
        await raw.WriteAsync(Encoding.ASCII.GetBytes("Content-Length: 100\r\n\r\n{\"jsonrpc\":"));
        await raw.DisposeAsync();

        foreach (var client in clients)
        {
            await client.DisposeAsync();
        }

        await AssertEx.Eventually(() => _server.Pipe!.ConnectionCount == 0, "server drops the connections");
        Assert.Equal(commandsBefore, _server.Backend.Commands.Count);
        Assert.Equal(PlaybackStatus.Playing, _server.Backend.Status);
    }

    [Theory]
    [InlineData("Content-Length: 999999999\r\n\r\n")]
    [InlineData("Content-Length: banana\r\n\r\n")]
    [InlineData("X-No-Length: 1\r\n\r\n")]
    [InlineData("Content-Length: 5\r\n\r\nhello")]
    public async Task Misbehaving_clients_are_disconnected_without_affecting_others(string payload)
    {
        var good = await _server.ConnectVolunteerAsync();
        var raw = new NamedPipeClientStream(".", _server.PipeOptions.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await raw.ConnectAsync(5_000);
        await raw.WriteAsync(Encoding.ASCII.GetBytes(payload));
        await raw.FlushAsync();
        Assert.Equal(0, await ReadUntilClosedAsync(raw));
        await raw.DisposeAsync();
        await good.Playback.GetStateAsync();
    }

    [Fact]
    public async Task Huge_header_is_rejected()
    {
        var raw = new NamedPipeClientStream(".", _server.PipeOptions.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await raw.ConnectAsync(5_000);
        try
        {
            await raw.WriteAsync(Encoding.ASCII.GetBytes("X-Pad: " + new string('a', 4_000)));
        }
        catch (IOException)
        {
            // The server may already have closed the pipe.
        }

        Assert.Equal(0, await ReadUntilClosedAsync(raw));
        await raw.DisposeAsync();
    }

    [Fact]
    public async Task Connections_beyond_the_limit_are_closed_when_every_slot_is_signed_in()
    {
        // Signed-out connections would make room instead (see PipeLimitsTests).
        for (var i = 0; i < 8; i++)
        {
            await _server.ConnectVolunteerAsync();
        }

        await AssertEx.Eventually(() => _server.Pipe!.ConnectionCount == 8, "8 connections open");
        var raw = new NamedPipeClientStream(".", _server.PipeOptions.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await raw.ConnectAsync(5_000);
        Assert.Equal(0, await ReadUntilClosedAsync(raw));
        await raw.DisposeAsync();
        Assert.Equal(8, _server.Pipe!.ConnectionCount);
    }

    [Fact]
    public async Task Unexpected_server_errors_leak_no_details()
    {
        var client = await _server.ConnectVolunteerAsync();
        _server.Backend.ThrowOnPause = new InvalidOperationException("secret internal detail at C:\\ProgramData\\SabhaMujic");
        var ex = await AssertEx.ThrowsCode(ErrorCode.Internal, () => client.Playback.PauseAsync());
        Assert.DoesNotContain("secret", ex.Message);
        Assert.DoesNotContain("ProgramData", ex.Message);
        Assert.DoesNotContain("InvalidOperationException", ex.Message);

        // Application errors keep their code and message, and the connection stays usable.
        var notFound = await AssertEx.ThrowsCode(ErrorCode.NotFound, () => client.Library.GetTrackAsync(Guid.NewGuid()));
        Assert.Equal("The track does not exist.", notFound.Message);
        await AssertEx.ThrowsCode(ErrorCode.InvalidArgument, () => client.Playback.SetVolumeAsync(2f));
        await client.Playback.GetStateAsync();
    }

    /// <summary>Reads until the server closes the pipe; returns 0 then. Fails the test if it stays open for 10 s.</summary>
    private static async Task<int> ReadUntilClosedAsync(Stream stream)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var buffer = new byte[4096];
        try
        {
            while (await stream.ReadAsync(buffer, timeout.Token) > 0)
            {
            }
        }
        catch (IOException)
        {
        }

        return 0;
    }

    [Fact]
    public async Task Playback_commands_reach_the_backend_after_validation()
    {
        var client = await _server.ConnectVolunteerAsync();
        await AssertEx.ThrowsCode(ErrorCode.NotFound, () => client.Playback.PlayTrackAsync(Guid.NewGuid()));
        await AssertEx.ThrowsCode(ErrorCode.NotFound, () => client.Playback.EnqueueAsync([Guid.NewGuid()], false));
        await AssertEx.ThrowsCode(ErrorCode.InvalidArgument, () => client.Playback.SetCrossfadeAsync(13));
        await AssertEx.ThrowsCode(ErrorCode.InvalidArgument, () => client.Playback.SeekAsync(-1));
        await AssertEx.ThrowsCode(ErrorCode.InvalidArgument, () => client.Playback.MoveQueueItemAsync(Guid.NewGuid(), -1));
        await AssertEx.ThrowsCode(ErrorCode.InvalidArgument, () => client.Playback.SetLoopModeAsync((LoopMode)42));
        Assert.Empty(_server.Backend.Commands);

        var hash = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var track = (await _server.App.Tracks.AddAsync(new Data.Repositories.NewTrack(hash, hash + ".mp3", "T", null, null, 60, 1))).Track;
        var playlist = await client.Playlists.CreatePlaylistAsync("P", [track.Id, track.Id]);
        var queue = await client.Playback.EnqueuePlaylistAsync(playlist.Id, replaceQueue: true);
        Assert.Equal([track.Id, track.Id], queue.Items.Select(i => i.TrackId));
        await client.Playback.PlayQueueItemAsync(queue.Items[1].QueueItemId);
        Assert.Equal(["ReplaceQueue", "PlayQueueItem"], _server.Backend.Commands);
    }
}

public sealed class MaintenanceTests : IAsyncLifetime
{
    private TestServer _server = null!;

    public async Task InitializeAsync() => _server = await TestServer.StartAsync();

    public async Task DisposeAsync() => await _server.DisposeAsync();

    [Fact]
    public async Task Startup_resets_interrupted_analysis_and_queues_pending_tracks()
    {
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var track = (await _server.App.Tracks.AddAsync(new Data.Repositories.NewTrack(hash, hash + ".mp3", "T", null, null, 60, 1))).Track;
        await _server.App.Analysis.UpsertAsync(new Data.Repositories.TrackAnalysis(track.Id, AnalysisStatus.Analyzing, 1));

        await _server.App.Maintenance.StartupAsync();

        Assert.Equal(AnalysisStatus.Pending, (await _server.App.Analysis.GetAsync(track.Id))!.Status);
        Assert.Equal([track.Id], _server.AnalysisQueue.Queued);
    }

    [Fact]
    public async Task Purges_expired_sessions_codes_and_abandoned_uploads()
    {
        var admin = await _server.ConnectAdminAsync();
        await admin.Admin.CreatePairingCodeAsync(Role.Volunteer);
        await admin.Admin.BeginImportAsync("a.mp3", 100);
        _server.Clock.Advance(TimeSpan.FromHours(13));

        var report = await _server.App.Maintenance.RunOnceAsync();
        Assert.Equal(new MaintenanceReport(1, 1, 1), report);
        Assert.Equal(0, _server.Uploads.ActiveCount);
    }

    [Fact]
    public async Task Periodic_runs_stop_on_cancellation()
    {
        using var cts = new CancellationTokenSource();
        var run = _server.App.Maintenance.RunPeriodicallyAsync(TimeSpan.FromMinutes(1), cts.Token);
        await cts.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
