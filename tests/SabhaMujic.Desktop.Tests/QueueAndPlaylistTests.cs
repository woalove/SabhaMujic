using SabhaMujic.Contracts;
using SabhaMujic.Desktop.Tests.Fakes;
using SabhaMujic.Desktop.ViewModels;
using SabhaMujic.Desktop.ViewModels.Infrastructure;
using static SabhaMujic.Desktop.Tests.TestHelpers;

namespace SabhaMujic.Desktop.Tests;

public class QueueViewModelTests
{
    private readonly FakeSabhaClient _client = new();
    private readonly TestUi _ui = new();
    private readonly QueueViewModel _vm;

    public QueueViewModelTests()
    {
        _client.Library.Tracks.Add(new TrackDto(TrackA, "Aarti", null, null, 125, AnalysisStatus.Ready, null, DateTimeOffset.UtcNow));
        _vm = new QueueViewModel(_client, _ui.Services, new TrackInfoCache(_client));
        _client.Events.QueueChanged(Queue(10, 1, Id(1), Id(2), Id(3), Id(4)));
    }

    [Fact]
    public void Shows_items_with_titles_and_current_highlight()
    {
        Assert.Equal(4, _vm.Items.Count);
        Assert.All(_vm.Items, i => Assert.Equal("Aarti", i.Title));
        Assert.Equal("2:05", _vm.Items[0].DurationText);
        Assert.Equal([false, true, false, false], _vm.Items.Select(i => i.IsCurrent).ToArray());
        Assert.Equal("4 tracks", _vm.Summary);
        Assert.Single(_client.CallsTo("GetTrackAsync")); // one lookup per distinct track
    }

    [Fact]
    public void Ignores_stale_queue_snapshots()
    {
        _client.Events.QueueChanged(Queue(9, 0, Id(1)));
        _client.Events.QueueChanged(Queue(10, 0, Id(1)));
        Assert.Equal(4, _vm.Items.Count);

        _client.Events.QueueChanged(Queue(11, 0, Id(4), Id(1)));
        Assert.Equal([Id(4), Id(1)], _vm.Items.Select(i => i.QueueItemId).ToArray());
        Assert.True(_vm.Items[0].IsCurrent);
    }

    [Theory]
    [InlineData(0, 3)] // drag first to last
    [InlineData(3, 0)] // drag last to first
    [InlineData(1, 2)]
    public async Task Drag_reorder_calls_move_with_item_and_final_index(int from, int to)
    {
        var item = _vm.Items[from];

        await _vm.MoveItemAsync(from, to);

        var call = Assert.Single(_client.CallsTo("MoveQueueItemAsync"));
        Assert.Equal(item.QueueItemId, call.Args[0]);
        Assert.Equal(to, call.Args[1]);
    }

    [Fact]
    public async Task Move_ignores_no_op_and_clamps_target()
    {
        await _vm.MoveItemAsync(2, 2);
        Assert.Empty(_client.CallsTo("MoveQueueItemAsync"));

        await _vm.MoveItemAsync(1, 99);
        Assert.Equal(3, _client.CallsTo("MoveQueueItemAsync").Single().Args[1]);
    }

    [Fact]
    public async Task Move_up_and_down_buttons()
    {
        await _vm.MoveUpCommand.ExecuteAsync(_vm.Items[2]);
        await _vm.MoveDownCommand.ExecuteAsync(_vm.Items[0]);

        var calls = _client.CallsTo("MoveQueueItemAsync").ToArray();
        Assert.Equal(1, calls[0].Args[1]);
        Assert.Equal(1, calls[1].Args[1]);
    }

    [Fact]
    public async Task Command_results_are_applied()
    {
        _client.Playback.QueueResult = () => Queue(12, 0, Id(2), Id(1), Id(3), Id(4));

        await _vm.MoveItemAsync(0, 1);

        Assert.Equal([Id(2), Id(1), Id(3), Id(4)], _vm.Items.Select(i => i.QueueItemId).ToArray());
        Assert.Equal(12, _vm.LastRevision);
    }

    [Fact]
    public async Task Remove_play_and_clear()
    {
        await _vm.RemoveCommand.ExecuteAsync(_vm.Items[3]);
        await _vm.PlayItemCommand.ExecuteAsync(_vm.Items[2]);
        Assert.Equal(Id(4), _client.CallsTo("RemoveFromQueueAsync").Single().Args[0]);
        Assert.Equal(Id(3), _client.CallsTo("PlayQueueItemAsync").Single().Args[0]);

        _ui.Dialogs.ConfirmResult = false;
        await _vm.ClearCommand.ExecuteAsync(null);
        Assert.Empty(_client.CallsTo("ClearQueueAsync"));

        _ui.Dialogs.ConfirmResult = true;
        await _vm.ClearCommand.ExecuteAsync(null);
        Assert.Single(_client.CallsTo("ClearQueueAsync"));
    }

    [Fact]
    public void Removed_tracks_show_as_unknown()
    {
        var gone = Guid.NewGuid();
        _client.Events.QueueChanged(new QueueDto([new QueueItemDto(Id(9), gone)], 0, 20));

        Assert.Equal("Unknown track", _vm.Items.Single().Title);
    }
}

public class PlaylistsViewModelTests
{
    private readonly FakeSabhaClient _client = new();
    private readonly TestUi _ui = new();
    private readonly PlaylistsViewModel _vm;
    private readonly PlaylistDto _sunday;
    private readonly Guid[] _tracks;

    public PlaylistsViewModelTests()
    {
        _tracks = [.. Enumerable.Range(1, 3).Select(i => _client.Library.AddTrack($"T{i}").Id)];
        _sunday = _client.Playlists.Add("Sunday", _tracks);
        _vm = new PlaylistsViewModel(_client, _ui.Services, new TrackInfoCache(_client));
    }

    private async Task LoadAndSelectAsync()
    {
        await _vm.LoadAsync();
        _vm.SelectedPlaylist = _vm.Playlists.Single();
    }

    [Fact]
    public async Task Selecting_a_playlist_shows_its_tracks_in_order()
    {
        await LoadAndSelectAsync();

        Assert.Equal(["T1", "T2", "T3"], _vm.Tracks.Select(t => t.Title).ToArray());
        Assert.Equal("3 tracks", _vm.Playlists.Single().CountText);
    }

    [Fact]
    public async Task Create_prompts_for_a_name_and_selects_the_new_playlist()
    {
        await _vm.LoadAsync();
        _ui.Dialogs.PromptResults.Enqueue("  Evening  ");

        await _vm.CreateCommand.ExecuteAsync(null);

        Assert.Equal("Evening", _client.CallsTo("CreatePlaylistAsync").Single().Args[0]);
        Assert.Equal("Evening", _vm.SelectedPlaylist?.Name);
    }

    [Fact]
    public async Task Create_cancelled_does_nothing()
    {
        await _vm.CreateCommand.ExecuteAsync(null);
        Assert.Empty(_client.CallsTo("CreatePlaylistAsync"));
    }

    [Fact]
    public async Task Rename_updates_the_row()
    {
        await LoadAndSelectAsync();
        _ui.Dialogs.PromptResults.Enqueue("Sunday Sabha");

        await _vm.RenameCommand.ExecuteAsync(null);

        Assert.Equal([_sunday.Id, "Sunday Sabha"], _client.CallsTo("RenamePlaylistAsync").Single().Args);
        Assert.Equal("Sunday Sabha", _vm.Playlists.Single().Name);
    }

    [Fact]
    public async Task Delete_requires_confirmation()
    {
        await LoadAndSelectAsync();
        _ui.Dialogs.ConfirmResult = false;
        await _vm.DeleteCommand.ExecuteAsync(null);
        Assert.Empty(_client.CallsTo("DeletePlaylistAsync"));

        _ui.Dialogs.ConfirmResult = true;
        await _vm.DeleteCommand.ExecuteAsync(null);
        Assert.Equal(_sunday.Id, _client.CallsTo("DeletePlaylistAsync").Single().Args[0]);
        Assert.Empty(_vm.Playlists);
        Assert.Null(_vm.SelectedPlaylist);
    }

    [Fact]
    public async Task Reordering_tracks_saves_the_full_new_order()
    {
        await LoadAndSelectAsync();

        await _vm.MoveTrackAsync(0, 2);

        var call = _client.CallsTo("SetPlaylistTracksAsync").Single();
        Assert.Equal([_tracks[1], _tracks[2], _tracks[0]], (Guid[])call.Args[1]!);
        Assert.Equal(["T2", "T3", "T1"], _vm.Tracks.Select(t => t.Title).ToArray());
        Assert.Equal("T1", _vm.SelectedTrack?.Title);
    }

    [Fact]
    public async Task Removing_a_track_saves_the_rest()
    {
        await LoadAndSelectAsync();

        await _vm.RemoveTrackCommand.ExecuteAsync(_vm.Tracks[1]);

        Assert.Equal([_tracks[0], _tracks[2]], (Guid[])_client.CallsTo("SetPlaylistTracksAsync").Single().Args[1]!);
    }

    [Fact]
    public async Task Enqueue_append_and_replace()
    {
        await LoadAndSelectAsync();

        await _vm.EnqueueAppendCommand.ExecuteAsync(null);
        await _vm.EnqueueReplaceCommand.ExecuteAsync(null);

        var calls = _client.CallsTo("EnqueuePlaylistAsync").ToArray();
        Assert.Equal([_sunday.Id, false], calls[0].Args);
        Assert.Equal([_sunday.Id, true], calls[1].Args);
        Assert.Contains("confirm:Replace queue", _ui.Dialogs.Asked);
    }

    [Fact]
    public async Task Playlist_changes_from_elsewhere_refresh_the_list()
    {
        await _vm.LoadAsync();
        _client.Playlists.Add("From the iPhone");

        _client.Events.LibraryChanged(new LibraryChangedDto(LibraryChangeKind.PlaylistsChanged, null));

        Assert.Equal(2, _vm.Playlists.Count);
    }

    [Fact]
    public async Task Errors_are_shown_not_thrown()
    {
        await LoadAndSelectAsync();
        _client.Playlists.Items.Clear(); // deleted elsewhere
        _ui.Dialogs.PromptResults.Enqueue("New name");

        await _vm.RenameCommand.ExecuteAsync(null);

        Assert.Equal(ErrorMessages.For(ErrorCode.NotFound), _vm.ErrorMessage);
    }
}
