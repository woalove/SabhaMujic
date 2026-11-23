using SabhaMujic.Client;
using SabhaMujic.Contracts;
using SabhaMujic.Desktop.Tests.Fakes;
using SabhaMujic.Desktop.ViewModels;
using SabhaMujic.Desktop.ViewModels.Demo;

namespace SabhaMujic.Desktop.Tests;

/// <summary>Controllable TimeProvider: timers fire only via <see cref="FireTimers"/>.</summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly List<ManualTimer> _timers = [];

    public DateTimeOffset Now { get; set; } = new(2026, 11, 15, 10, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => Now;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(callback, state);
        _timers.Add(timer);
        return timer;
    }

    public void FireTimers()
    {
        foreach (var t in _timers.Where(t => !t.Disposed).ToArray())
        {
            t.Callback(t.State);
        }
    }

    private sealed class ManualTimer(TimerCallback callback, object? state) : ITimer
    {
        public TimerCallback Callback { get; } = callback;

        public object? State { get; } = state;

        public bool Disposed { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        public void Dispose() => Disposed = true;

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}

public class DemoSabhaClientTests
{
    private readonly ManualTimeProvider _time = new();
    private readonly DemoSabhaClient _demo;

    public DemoSabhaClientTests()
    {
        _demo = new DemoSabhaClient(_time, autoTick: false);
    }

    private async Task SignInAsync(string user = DemoSabhaClient.AdminUser, string secret = DemoSabhaClient.AdminSecret)
    {
        await _demo.ConnectAsync();
        await _demo.Auth.LoginAsync(user, secret);
    }

    [Fact]
    public async Task Connects_with_open_use_and_administration_needs_sign_in()
    {
        var states = new List<ConnectionState>();
        _demo.ConnectionStateChanged += (_, s) => states.Add(s);
        await _demo.ConnectAsync();
        Assert.Equal([ConnectionState.Connecting, ConnectionState.Connected], states);

        Assert.NotEmpty(await _demo.Library.GetCategoriesAsync()); // open use: no sign-in for the player
        var ex = await Assert.ThrowsAsync<SabhaMujicException>(() => _demo.Admin.GetUsersAsync());
        Assert.Equal(ErrorCode.Unauthenticated, ex.Code);

        var bad = await Assert.ThrowsAsync<SabhaMujicException>(() => _demo.Auth.LoginAsync("admin", "nope"));
        Assert.Equal(ErrorCode.Unauthenticated, bad.Code);

        SessionDto? changed = null;
        _demo.SessionChanged += (_, s) => changed = s;
        var session = await _demo.Auth.LoginAsync("ADMIN", DemoSabhaClient.AdminSecret);
        Assert.Equal(Role.Admin, session.Role);
        Assert.Same(session, _demo.Session);
        Assert.Same(session, changed);
    }

    [Fact]
    public async Task Volunteers_cannot_use_admin_apis_but_can_edit_playlists()
    {
        await SignInAsync(DemoSabhaClient.VolunteerUser, DemoSabhaClient.VolunteerSecret);

        var ex = await Assert.ThrowsAsync<SabhaMujicException>(() => _demo.Admin.GetUsersAsync());
        Assert.Equal(ErrorCode.Forbidden, ex.Code);
        var created = await _demo.Playlists.CreatePlaylistAsync("Mine");
        Assert.Equal("Mine", created.Name);
    }

    [Fact]
    public async Task Library_counts_match_tracks_and_search_filters()
    {
        await SignInAsync();

        var categories = await _demo.Library.GetCategoriesAsync();
        var all = await _demo.Library.GetTracksAsync(new TrackQuery());
        Assert.Equal(categories.Sum(c => c.TrackCount) + 1, all.Total); // + one uncategorized demo track
        foreach (var c in categories)
        {
            Assert.Equal(c.TrackCount, (await _demo.Library.GetTracksAsync(new TrackQuery(c.Id))).Total);
        }

        var found = await _demo.Library.GetTracksAsync(new TrackQuery(Search: "flute"));
        Assert.Equal("Demo Instrumental – Flute", Assert.Single(found.Items).Title);
    }

    [Fact]
    public async Task Playback_position_advances_and_the_queue_moves_on_at_track_end()
    {
        await SignInAsync();
        var tracks = (await _demo.Library.GetTracksAsync(new TrackQuery())).Items;
        var states = new List<PlaybackStateDto>();
        _demo.Events.PlaybackStateReceived += states.Add;
        await _demo.Playback.SetCrossfadeAsync(0);

        await _demo.Playback.PlayTrackAsync(tracks[0].Id);
        await _demo.Playback.EnqueueAsync([tracks[1].Id], playNext: false);
        _time.Now += TimeSpan.FromSeconds(30);

        var state = await _demo.Playback.GetStateAsync();
        Assert.Equal(PlaybackStatus.Playing, state.Status);
        Assert.Equal(tracks[0].Id, state.CurrentTrackId);
        Assert.Equal(tracks[1].Id, state.NextTrackId);
        Assert.Equal(30, state.PositionSeconds, precision: 6);

        _time.Now += TimeSpan.FromSeconds(tracks[0].DurationSeconds);
        _demo.Pump();
        state = await _demo.Playback.GetStateAsync();
        Assert.Equal(tracks[1].Id, state.CurrentTrackId);
        Assert.Equal(1, (await _demo.Playback.GetQueueAsync()).CurrentIndex);

        // Revisions only increase.
        Assert.True(states.Zip(states.Skip(1)).All(p => p.Second.Revision > p.First.Revision));
    }

    [Fact]
    public async Task Crossfade_window_reports_crossfading_and_queue_end_stops()
    {
        await SignInAsync();
        var tracks = (await _demo.Library.GetTracksAsync(new TrackQuery())).Items;
        await _demo.Playback.PlayTrackAsync(tracks[0].Id);
        await _demo.Playback.EnqueueAsync([tracks[1].Id], playNext: false);

        _time.Now += TimeSpan.FromSeconds(tracks[0].DurationSeconds - 3); // crossfade is 6 s by default
        _demo.Pump();
        Assert.Equal(PlaybackStatus.Crossfading, (await _demo.Playback.GetStateAsync()).Status);

        _time.Now += TimeSpan.FromSeconds(3 + tracks[1].DurationSeconds + 60);
        _demo.Pump();
        _demo.Pump();
        Assert.Equal(PlaybackStatus.Stopped, (await _demo.Playback.GetStateAsync()).Status);
    }

    [Fact]
    public async Task Smart_loop_wraps_inside_the_loop_region()
    {
        await SignInAsync();
        var track = (await _demo.Library.GetTracksAsync(new TrackQuery())).Items[0];
        await _demo.Playback.SetLoopModeAsync(LoopMode.SmartLoop);
        await _demo.Playback.PlayTrackAsync(track.Id);

        _time.Now += TimeSpan.FromSeconds(track.Loop!.EndSeconds + 1);
        _demo.Pump();

        var state = await _demo.Playback.GetStateAsync();
        Assert.Equal(track.Id, state.CurrentTrackId);
        Assert.Equal(track.Loop.StartSeconds + 1, state.PositionSeconds, precision: 3);
    }

    [Fact]
    public async Task Queue_operations_keep_the_current_item_and_bump_revisions()
    {
        await SignInAsync();
        var ids = (await _demo.Library.GetTracksAsync(new TrackQuery())).Items.Take(4).Select(t => t.Id).ToArray();
        var q = await _demo.Playback.EnqueueAsync(ids, playNext: false);
        await _demo.Playback.PlayQueueItemAsync(q.Items[1].QueueItemId);
        var current = q.Items[1].QueueItemId;

        var moved = await _demo.Playback.MoveQueueItemAsync(q.Items[0].QueueItemId, 3);
        Assert.Equal(current, moved.Items[moved.CurrentIndex].QueueItemId);
        Assert.True(moved.Revision > q.Revision);

        var removed = await _demo.Playback.RemoveFromQueueAsync(moved.Items[^1].QueueItemId);
        Assert.Equal(3, removed.Items.Count);

        var cleared = await _demo.Playback.ClearQueueAsync();
        Assert.Equal(current, Assert.Single(cleared.Items).QueueItemId);
        Assert.Equal(0, cleared.CurrentIndex);
    }

    [Fact]
    public async Task Import_validates_offsets_detects_duplicates_and_adds_tracks()
    {
        await SignInAsync();
        var changes = new List<LibraryChangedDto>();
        _demo.Events.LibraryChangeReceived += changes.Add;
        var data = new byte[3000];
        Random.Shared.NextBytes(data);

        var upload = await _demo.Admin.BeginImportAsync("song.mp3", data.Length);
        var wrongOffset = await Assert.ThrowsAsync<SabhaMujicException>(() => _demo.Admin.UploadChunkAsync(upload, 5, data[..10]));
        Assert.Equal(ErrorCode.InvalidArgument, wrongOffset.Code);
        await _demo.Admin.UploadChunkAsync(upload, 0, data[..2000]);
        await _demo.Admin.UploadChunkAsync(upload, 2000, data[2000..]);
        var result = await _demo.Admin.CompleteImportAsync(upload, new ImportMetadata("Song", null, null));
        Assert.False(result.WasDuplicate);
        Assert.Contains(changes, c => c.Kind == LibraryChangeKind.TrackAdded && c.EntityId == result.TrackId);
        Assert.Equal(AnalysisStatus.Analyzing, (await _demo.Library.GetTrackAsync(result.TrackId)).Analysis);

        _demo.Pump();
        Assert.Equal(AnalysisStatus.Ready, (await _demo.Library.GetTrackAsync(result.TrackId)).Analysis);

        var again = await _demo.Admin.BeginImportAsync("copy.mp3", data.Length);
        await _demo.Admin.UploadChunkAsync(again, 0, data);
        var dup = await _demo.Admin.CompleteImportAsync(again, new ImportMetadata("Copy", null, null));
        Assert.True(dup.WasDuplicate);
        Assert.Equal(result.TrackId, dup.TrackId);

        var bad = await Assert.ThrowsAsync<SabhaMujicException>(() => _demo.Admin.BeginImportAsync("notes.txt", 10));
        Assert.Equal(ErrorCode.UnsupportedFormat, bad.Code);
    }

    [Fact]
    public async Task Deleting_a_track_removes_it_from_playlists_and_queue()
    {
        await SignInAsync();
        var playlist = (await _demo.Playlists.GetPlaylistsAsync()).Single();
        var victim = playlist.TrackIds[0];
        await _demo.Playback.EnqueueAsync([victim], playNext: false);

        await _demo.Admin.DeleteTrackAsync(victim);

        Assert.DoesNotContain(victim, (await _demo.Playlists.GetPlaylistAsync(playlist.Id)).TrackIds);
        Assert.DoesNotContain((await _demo.Playback.GetQueueAsync()).Items, i => i.TrackId == victim);
        var ex = await Assert.ThrowsAsync<SabhaMujicException>(() => _demo.Library.GetTrackAsync(victim));
        Assert.Equal(ErrorCode.NotFound, ex.Code);
    }

    [Fact]
    public async Task Phone_setup_shows_addresses_and_needs_an_administrator()
    {
        await _demo.ConnectAsync();
        var denied = await Assert.ThrowsAsync<SabhaMujicException>(() => _demo.Admin.GetPhoneSetupInfoAsync());
        Assert.Equal(ErrorCode.Unauthenticated, denied.Code);
        await SignInAsync();
        var setup = await _demo.Admin.GetPhoneSetupInfoAsync();
        Assert.StartsWith("http://", setup.BootstrapUrl);
        Assert.StartsWith("https://", setup.RemoteUrl);
        Assert.True(setup.IsRemoteRunning);
    }

    [Fact]
    public async Task Simulated_connection_loss_reconnects()
    {
        await SignInAsync();
        var states = new List<ConnectionState>();
        _demo.ConnectionStateChanged += (_, s) => states.Add(s);

        _demo.SimulateConnectionLoss();
        await Assert.ThrowsAsync<IOException>(() => _demo.Playback.GetStateAsync());
        _time.FireTimers();

        Assert.Equal([ConnectionState.Reconnecting, ConnectionState.Connected], states);
        Assert.NotNull(_demo.Session);
    }

    [Fact]
    public async Task Drives_the_shell_end_to_end()
    {
        var ui = new TestUi();
        var shell = new ShellViewModel(_demo, ui.Services);
        shell.Start();
        await Task.Yield();
        for (var i = 0; i < 10 && !shell.IsConnected; i++)
        {
            await Task.Delay(10);
        }

        Assert.True(shell.IsConnected);

        shell.Login.UserName = DemoSabhaClient.AdminUser;
        shell.Login.Secret = DemoSabhaClient.AdminSecret;
        await shell.Login.LoginCommand.ExecuteAsync(null);
        Assert.True(shell.IsAdminVisible);
        Assert.Equal(5, shell.Library.Categories.Count);

        await shell.Library.PlayNowCommand.ExecuteAsync(shell.Library.Tracks[0]);
        Assert.Equal(shell.Library.Tracks[0].Title, shell.NowPlaying.Title);
        Assert.True(shell.NowPlaying.IsPlaying);
        Assert.Single(shell.Queue.Items);
        Assert.True(shell.Queue.Items[0].IsCurrent);
        Assert.Null(shell.Library.ErrorMessage);
    }
}
