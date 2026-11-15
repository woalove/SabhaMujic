using System.Collections.Concurrent;
using System.Security.Cryptography;
using SabhaMujic.Client;
using SabhaMujic.Contracts;
using SabhaMujic.Data.Repositories;

namespace SabhaMujic.Server.Tests;

/// <summary>Contract v1 additions from the M4a review: batch lookup, page limit, ServerEpoch, admin-only events, client semantics.</summary>
public sealed class ContractAdditionsTests : IAsyncLifetime
{
    private TestServer _server = null!;

    public async Task InitializeAsync() => _server = await TestServer.StartAsync();

    public async Task DisposeAsync() => await _server.DisposeAsync();

    private async Task<TrackDto> AddTrackAsync(string title)
    {
        var hash = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        return (await _server.App.Tracks.AddAsync(new NewTrack(hash, hash + ".mp3", title, null, null, 60, 1))).Track;
    }

    [Fact]
    public async Task Batch_lookup_keeps_request_order_and_omits_unknown_ids()
    {
        var a = await AddTrackAsync("A");
        var b = await AddTrackAsync("B");
        var client = await _server.ConnectVolunteerAsync();

        var found = await client.Library.GetTracksByIdsAsync([b.Id, Guid.NewGuid(), a.Id, b.Id]);
        Assert.Equal([b.Id, a.Id], found.Select(t => t.Id));
        Assert.Equal(a, found[1]);
        Assert.Empty(await client.Library.GetTracksByIdsAsync([]));

        var tooMany = Enumerable.Range(0, LibraryLimits.MaxBatchLookup + 1).Select(_ => Guid.NewGuid()).ToArray();
        await AssertEx.ThrowsCode(ErrorCode.InvalidArgument, () => client.Library.GetTracksByIdsAsync(tooMany));
        Assert.Empty(await client.Library.GetTracksByIdsAsync(tooMany[..LibraryLimits.MaxBatchLookup]));
    }

    [Fact]
    public async Task Track_pages_are_limited()
    {
        var client = await _server.ConnectVolunteerAsync();
        await client.Library.GetTracksAsync(new TrackQuery(Take: LibraryLimits.MaxPageSize));
        await AssertEx.ThrowsCode(ErrorCode.InvalidArgument, () => client.Library.GetTracksAsync(new TrackQuery(Take: LibraryLimits.MaxPageSize + 1)));
        await AssertEx.ThrowsCode(ErrorCode.InvalidArgument, () => client.Library.GetTracksAsync(new TrackQuery(Skip: -1)));
    }

    [Fact]
    public async Task Server_epoch_is_stamped_on_every_returned_and_pushed_state_and_queue()
    {
        var epoch = _server.App.ServerEpoch;
        Assert.NotEqual(Guid.Empty, epoch);
        var track = await AddTrackAsync("A");
        var client = await _server.ConnectVolunteerAsync();
        var states = new ConcurrentQueue<PlaybackStateDto>();
        var queues = new ConcurrentQueue<QueueDto>();
        client.Events.PlaybackStateReceived += states.Enqueue;
        client.Events.QueueReceived += queues.Enqueue;

        Assert.Equal(epoch, (await client.Playback.GetStateAsync()).ServerEpoch);
        Assert.Equal(epoch, (await client.Playback.GetQueueAsync()).ServerEpoch);
        var queue = await client.Playback.EnqueueAsync([track.Id, track.Id], playNext: false);
        Assert.Equal(epoch, queue.ServerEpoch);
        Assert.Equal(epoch, (await client.Playback.MoveQueueItemAsync(queue.Items[0].QueueItemId, 1)).ServerEpoch);
        Assert.Equal(epoch, (await client.Playback.RemoveFromQueueAsync(queue.Items[0].QueueItemId)).ServerEpoch);
        Assert.Equal(epoch, (await client.Playback.ClearQueueAsync()).ServerEpoch);
        await client.Playback.PlayTrackAsync(track.Id);

        await AssertEx.Eventually(() => !states.IsEmpty && !queues.IsEmpty, "pushed state and queue");
        Assert.All(states, s => Assert.Equal(epoch, s.ServerEpoch));
        Assert.All(queues, q => Assert.Equal(epoch, q.ServerEpoch));

        // A restarted service (new application instance) has a new epoch.
        await using var other = await TestServer.StartAsync();
        Assert.NotEqual(epoch, other.App.ServerEpoch);
    }

    [Fact]
    public async Task User_and_device_changes_reach_admin_sessions_only()
    {
        var admin = await _server.ConnectAdminAsync();
        var volunteer = await _server.ConnectVolunteerAsync();
        var adminSaw = new ConcurrentQueue<LibraryChangedDto>();
        var volunteerSaw = new ConcurrentQueue<LibraryChangedDto>();
        admin.Events.LibraryChangeReceived += adminSaw.Enqueue;
        volunteer.Events.LibraryChangeReceived += volunteerSaw.Enqueue;

        var user = await admin.Admin.CreateUserAsync("asha", Role.Volunteer, "asha-pass");
        var code = await admin.Admin.CreatePairingCodeAsync(Role.Volunteer);
        var paired = await _server.App.CreateSession(ClientKind.Remote, "phone").PairAsync(code.Code, "Phone");
        await admin.Admin.RevokeDeviceAsync(paired.Device.Id);
        await admin.Admin.DeleteUserAsync(user.Id);
        var category = await admin.Admin.CreateCategoryAsync("Marker"); // a change everyone sees, to order the check

        await AssertEx.Eventually(() => volunteerSaw.Any(c => c.EntityId == category.Id) && adminSaw.Any(c => c.EntityId == category.Id),
            "category change delivered to both");
        Assert.DoesNotContain(volunteerSaw, c => c.Kind is LibraryChangeKind.UsersChanged or LibraryChangeKind.DevicesChanged);
        Assert.Equal(
            [
                (LibraryChangeKind.UsersChanged, (Guid?)user.Id),
                (LibraryChangeKind.DevicesChanged, paired.Device.Id),
                (LibraryChangeKind.DevicesChanged, paired.Device.Id),
                (LibraryChangeKind.UsersChanged, user.Id),
            ],
            adminSaw.Where(c => c.Kind is LibraryChangeKind.UsersChanged or LibraryChangeKind.DevicesChanged).Select(c => (c.Kind, c.EntityId)));
    }

    [Fact]
    public async Task Connect_token_only_bounds_the_first_wait_and_the_client_keeps_trying()
    {
        await _server.StopPipeAsync();
        var client = _server.NewClient();
        using (var shortWait = new CancellationTokenSource(TimeSpan.FromMilliseconds(300)))
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ConnectAsync(shortWait.Token));
        }

        Assert.NotEqual(ConnectionState.Connected, client.State);
        _server.StartPipe();
        await AssertEx.Eventually(() => client.State == ConnectionState.Connected, "background loop connects after the wait was cancelled");
        await client.ConnectAsync(); // completes immediately now
        await client.Auth.LoginAsync(TestServer.VolunteerName, TestServer.VolunteerSecret);
        await client.Playback.GetStateAsync();
    }

    [Fact]
    public async Task Session_changes_are_raised_for_login_failed_resume_and_logout()
    {
        var client = await _server.ConnectAsync();
        var changes = new ConcurrentQueue<SessionDto?>();
        client.SessionChanged += (_, s) => changes.Enqueue(s);

        var session = await client.Auth.LoginAsync(TestServer.VolunteerName, TestServer.VolunteerSecret);
        await AssertEx.ThrowsCode(ErrorCode.Unauthenticated, () => client.Auth.ResumeSessionAsync("expired-or-bogus"));
        Assert.Null(client.Session);
        await client.Auth.ResumeSessionAsync(session.Token);
        Assert.Equal(session.Token, client.Session!.Token);
        await client.Auth.LogoutAsync();
        Assert.Null(client.Session);

        Assert.Equal([session.Token, null, session.Token, null], changes.Select(s => s?.Token));
    }
}
