using SabhaMujic.Contracts;
using SabhaMujic.Desktop.Tests.Fakes;
using SabhaMujic.Desktop.ViewModels;
using SabhaMujic.Desktop.ViewModels.Infrastructure;

namespace SabhaMujic.Desktop.Tests;

public class LibraryViewModelTests
{
    private readonly FakeSabhaClient _client = new();
    private readonly TestUi _ui = new();
    private readonly PlaylistsViewModel _playlists;
    private readonly LibraryViewModel _vm;
    private readonly CategoryDto _dhun = new(Guid.NewGuid(), "Dhun", 1, 2);
    private readonly CategoryDto _kirtan = new(Guid.NewGuid(), "Kirtan", 0, 1);

    public LibraryViewModelTests()
    {
        _client.Library.Categories.AddRange([_dhun, _kirtan]);
        _client.Library.AddTrack("Morning Dhun", _dhun.Id);
        _client.Library.AddTrack("Evening Dhun", _dhun.Id);
        _client.Library.AddTrack("Kirtan One", _kirtan.Id);
        _client.Library.AddTrack("Loose track");
        var tracks = new TrackInfoCache(_client);
        _playlists = new PlaylistsViewModel(_client, _ui.Services, tracks);
        _vm = new LibraryViewModel(_client, _ui.Services, tracks, _playlists) { PageSize = 2 };
    }

    private List<TrackQuery> Queries => _client.CallsTo("GetTracksAsync").Select(c => (TrackQuery)c.Args[0]!).ToList();

    [Fact]
    public async Task Sidebar_has_all_plus_categories_in_sort_order_with_counts()
    {
        await _vm.LoadAsync();

        Assert.Equal(["All", "Kirtan", "Dhun"], _vm.Categories.Select(c => c.Name).ToArray());
        Assert.Equal([4, 1, 2], _vm.Categories.Select(c => c.TrackCount).ToArray());
        Assert.True(_vm.Categories[0].IsAll);
        Assert.Same(_vm.Categories[0], _vm.SelectedCategory);
        Assert.Equal(2, _vm.Tracks.Count);
        Assert.Equal("1–2 of 4", _vm.PageText);
        Assert.Equal("Dhun", _vm.Tracks.Single(t => t.Title == "Morning Dhun").CategoryName);
    }

    [Fact]
    public async Task Selecting_a_category_filters_the_list()
    {
        await _vm.LoadAsync();

        _vm.SelectedCategory = _vm.Categories.Single(c => c.Name == "Dhun");

        var query = Queries.Last();
        Assert.Equal(_dhun.Id, query.CategoryId);
        Assert.Equal(0, query.Skip);
        Assert.All(_vm.Tracks, t => Assert.Equal("Dhun", t.CategoryName));
        Assert.Equal(2, _vm.TotalCount);
    }

    [Fact]
    public async Task Search_is_debounced_and_sends_one_query_with_the_final_text()
    {
        await _vm.LoadAsync();
        var before = Queries.Count;

        _vm.SearchText = "d";
        _ui.Scheduler.Advance(TimeSpan.FromMilliseconds(100));
        _vm.SearchText = "dh";
        _ui.Scheduler.Advance(TimeSpan.FromMilliseconds(100));
        _vm.SearchText = "dhun ";
        _ui.Scheduler.Advance(LibraryViewModel.SearchDebounce - TimeSpan.FromMilliseconds(1));
        Assert.Equal(before, Queries.Count);

        _ui.Scheduler.Advance(TimeSpan.FromMilliseconds(1));

        Assert.Equal(before + 1, Queries.Count);
        Assert.Equal("dhun", Queries.Last().Search);
        Assert.Equal(["Evening Dhun", "Morning Dhun"], _vm.Tracks.Select(t => t.Title).Order().ToArray());
    }

    [Fact]
    public async Task Search_now_skips_the_debounce()
    {
        await _vm.LoadAsync();
        _vm.SearchText = "kirtan";

        await _vm.SearchNowCommand.ExecuteAsync(null);
        _ui.Scheduler.Advance(TimeSpan.FromSeconds(1));

        Assert.Single(Queries, q => q.Search == "kirtan");
    }

    [Fact]
    public async Task Stale_search_responses_are_dropped()
    {
        await _vm.LoadAsync();
        var slow = new TaskCompletionSource();
        _client.Library.Gate = q => q.Search == "morning" ? slow.Task : Task.CompletedTask;

        _vm.SearchText = "morning";
        _ui.Scheduler.Advance(LibraryViewModel.SearchDebounce);   // request 1 (slow) in flight
        _vm.SearchText = "kirtan";
        _ui.Scheduler.Advance(LibraryViewModel.SearchDebounce);   // request 2 answers immediately
        slow.SetResult();                                         // request 1 answers late
        await Task.Yield();

        Assert.Equal(["Kirtan One"], _vm.Tracks.Select(t => t.Title).ToArray());
    }

    [Fact]
    public async Task Paging_requests_the_right_window()
    {
        await _vm.LoadAsync();
        Assert.True(_vm.NextPageCommand.CanExecute(null));
        Assert.False(_vm.PreviousPageCommand.CanExecute(null));

        await _vm.NextPageCommand.ExecuteAsync(null);

        Assert.Equal(2, Queries.Last().Skip);
        Assert.Equal(2, Queries.Last().Take);
        Assert.Equal("3–4 of 4", _vm.PageText);
        Assert.False(_vm.NextPageCommand.CanExecute(null));
    }

    [Fact]
    public async Task Per_track_commands_call_play_and_enqueue()
    {
        await _vm.LoadAsync();
        var track = _vm.Tracks[0];

        await _vm.PlayNowCommand.ExecuteAsync(track);
        await _vm.PlayNextCommand.ExecuteAsync(track);
        await _vm.AddToQueueCommand.ExecuteAsync(track);

        Assert.Equal(track.Id, _client.CallsTo("PlayTrackAsync").Single().Args[0]);
        var enqueues = _client.CallsTo("EnqueueAsync").ToArray();
        Assert.Equal(2, enqueues.Length);
        Assert.Equal([track.Id], (Guid[])enqueues[0].Args[0]!);
        Assert.Equal(true, enqueues[0].Args[1]);
        Assert.Equal([track.Id], (Guid[])enqueues[1].Args[0]!);
        Assert.Equal(false, enqueues[1].Args[1]);
        Assert.Contains("added", _vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Add_to_playlist_appends_selected_track()
    {
        var existing = Guid.NewGuid();
        var playlist = _client.Playlists.Add("Sunday", existing);
        await _playlists.LoadAsync();
        await _vm.LoadAsync();
        _vm.SelectedTrack = _vm.Tracks[1];

        await _vm.AddToPlaylistCommand.ExecuteAsync(_vm.PlaylistTargets.Single());

        var set = _client.CallsTo("SetPlaylistTracksAsync").Single();
        Assert.Equal(playlist.Id, set.Args[0]);
        Assert.Equal([existing, _vm.Tracks[1].Id], (Guid[])set.Args[1]!);
        Assert.Equal(2, _vm.PlaylistTargets.Single().TrackCount);
    }

    [Fact]
    public async Task Library_changes_trigger_one_coalesced_refresh()
    {
        await _vm.LoadAsync();
        var before = Queries.Count;
        _client.Library.AddTrack("New Aarti");

        for (var i = 0; i < 5; i++)
        {
            _client.Events.LibraryChanged(new LibraryChangedDto(LibraryChangeKind.TrackAdded, Guid.NewGuid()));
        }

        Assert.Equal(before, Queries.Count);
        _ui.Scheduler.Advance(LibraryViewModel.RefreshCoalesce);

        Assert.Single(_client.CallsTo("GetCategoriesAsync").Skip(1));
        Assert.Equal(5, _vm.Categories[0].TrackCount);
        Assert.Equal(5, _vm.TotalCount);
    }

    [Fact]
    public async Task Deleted_category_falls_back_to_all()
    {
        await _vm.LoadAsync();
        _vm.SelectedCategory = _vm.Categories.Single(c => c.Name == "Kirtan");
        _client.Library.Categories.Remove(_kirtan);

        _client.Events.LibraryChanged(new LibraryChangedDto(LibraryChangeKind.CategoriesChanged, _kirtan.Id));
        _ui.Scheduler.Advance(LibraryViewModel.RefreshCoalesce);

        Assert.True(_vm.SelectedCategory!.IsAll);
        Assert.Null(Queries.Last().CategoryId);
    }

    [Fact]
    public async Task Track_updates_invalidate_cached_titles()
    {
        var cache = new TrackInfoCache(_client);
        var vm = new LibraryViewModel(_client, _ui.Services, cache, _playlists);
        await vm.LoadAsync();
        var id = _client.Library.Tracks[0].Id;
        Assert.True(cache.TryGet(id, out _));

        _client.Events.LibraryChanged(new LibraryChangedDto(LibraryChangeKind.TrackUpdated, id));

        Assert.False(cache.TryGet(id, out _));
    }
}
