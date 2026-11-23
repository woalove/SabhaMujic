using SabhaMujic.Client;
using SabhaMujic.Contracts;
using SabhaMujic.Desktop.Tests.Fakes;
using SabhaMujic.Desktop.ViewModels;
using SabhaMujic.Desktop.ViewModels.Admin;
using SabhaMujic.Desktop.ViewModels.Infrastructure;
using static SabhaMujic.Desktop.Tests.TestHelpers;

namespace SabhaMujic.Desktop.Tests;

public sealed class ThemePreferenceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sabhamujic-theme-" + Guid.NewGuid().ToString("N"));

    private string SettingsPath => Path.Combine(_dir, "desktop-settings.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void Dark_is_the_default()
    {
        var theme = new ThemeViewModel(new UiSettingsStore(SettingsPath));

        Assert.Equal(AppTheme.Dark, theme.Theme);
        Assert.True(theme.IsDark);
        Assert.Equal(AppTheme.Dark, new UiSettings().Theme);
    }

    [Fact]
    public void Toggle_is_saved_immediately_and_restored_on_next_start()
    {
        var theme = new ThemeViewModel(new UiSettingsStore(SettingsPath));
        var changed = new List<string?>();
        theme.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        theme.ToggleCommand.Execute(null);

        Assert.Equal(AppTheme.Light, theme.Theme);
        Assert.Contains(nameof(ThemeViewModel.Theme), changed);
        Assert.Contains(nameof(ThemeViewModel.IsDark), changed);
        Assert.Equal(AppTheme.Light, new ThemeViewModel(new UiSettingsStore(SettingsPath)).Theme);
        Assert.Contains("\"Light\"", File.ReadAllText(SettingsPath));

        theme.ToggleCommand.Execute(null);
        Assert.Equal(AppTheme.Dark, new UiSettingsStore(SettingsPath).Load().Theme);
    }

    [Fact]
    public void Saving_the_theme_keeps_the_window_layout()
    {
        var store = new UiSettingsStore(SettingsPath);
        store.Save(new UiSettings { Width = 1000, Height = 700, IsQueuePanelOpen = false });

        new ThemeViewModel(store).ToggleCommand.Execute(null);

        var loaded = store.Load();
        Assert.Equal((1000d, 700d, false, AppTheme.Light), (loaded.Width, loaded.Height, loaded.IsQueuePanelOpen, loaded.Theme));
    }

    [Fact]
    public void Unknown_theme_values_fall_back_to_dark()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(SettingsPath, """{ "Theme": 7 }""");
        Assert.Equal(AppTheme.Dark, new UiSettingsStore(SettingsPath).Load().Theme);

        File.WriteAllText(SettingsPath, """{ "Theme": "Neon" }""");
        Assert.Equal(AppTheme.Dark, new UiSettingsStore(SettingsPath).Load().Theme);
    }

    [Fact]
    public void Shell_exposes_the_theme_and_defaults_to_dark_without_a_store()
    {
        var shell = new ShellViewModel(new FakeSabhaClient(), new TestUi().Services);
        Assert.True(shell.Theme.IsDark);

        shell.Theme.ToggleCommand.Execute(null);
        Assert.Equal(AppTheme.Light, shell.Theme.Theme);
    }
}

public class TagColorTests
{
    [Fact]
    public void Colours_cycle_accent_violet_amber_blue()
    {
        Assert.Equal(
            [TagColor.Accent, TagColor.Violet, TagColor.Amber, TagColor.Blue, TagColor.Accent, TagColor.Violet],
            Enumerable.Range(0, 6).Select(TagColors.ForPosition).ToArray());
    }
}

public class CategoryTabTests
{
    private readonly FakeSabhaClient _client = new();
    private readonly TestUi _ui = new();
    private readonly LibraryViewModel _vm;
    private readonly CategoryDto[] _categories;
    private readonly TrackDto _both;

    public CategoryTabTests()
    {
        _categories = new[] { "Dhun", "Kirtan", "Aarti", "Bhajan", "Flute" }
            .Select((name, i) => new CategoryDto(Guid.NewGuid(), name, i, 0))
            .ToArray();
        _client.Library.Categories.AddRange(_categories.Reverse());
        _client.Library.AddTrack("Dhun only", _categories[0].Id);
        _both = new TrackDto(Guid.NewGuid(), "Dhun and Aarti", null, [_categories[2].Id, _categories[0].Id], 90,
            AnalysisStatus.Ready, null, DateTimeOffset.UtcNow);
        _client.Library.Tracks.Add(_both);
        _client.Library.AddTrack("Untagged");
        var cache = new TrackInfoCache(_client);
        _vm = new LibraryViewModel(_client, _ui.Services, cache, new PlaylistsViewModel(_client, _ui.Services, cache));
    }

    [Fact]
    public async Task Tabs_are_all_plus_each_category_with_cycling_dot_colours_and_counts()
    {
        _client.Library.Categories.Clear();
        _client.Library.Categories.AddRange(_categories.Select((c, i) => c with { TrackCount = i == 0 ? 2 : i == 2 ? 1 : 0 }));

        await _vm.LoadAsync();

        Assert.Equal(["All", "Dhun", "Kirtan", "Aarti", "Bhajan", "Flute"], _vm.Categories.Select(c => c.Name).ToArray());
        Assert.Equal([3, 2, 0, 1, 0, 0], _vm.Categories.Select(c => c.TrackCount).ToArray());
        Assert.False(_vm.Categories[0].HasDot);
        Assert.Equal(
            [null, TagColor.Accent, TagColor.Violet, TagColor.Amber, TagColor.Blue, TagColor.Accent],
            _vm.Categories.Select(c => c.Color).ToArray());
    }

    [Fact]
    public async Task Selecting_a_tag_filters_by_it_and_a_track_with_several_tags_appears_under_each()
    {
        await _vm.LoadAsync();

        _vm.SelectedCategory = _vm.Categories.Single(c => c.Name == "Dhun");
        Assert.Equal(_categories[0].Id, _vm.LastQuery!.CategoryId);
        Assert.Equal(["Dhun only", "Dhun and Aarti"], _vm.Tracks.Select(t => t.Title).ToArray());

        _vm.SelectedCategory = _vm.Categories.Single(c => c.Name == "Aarti");
        Assert.Equal(["Dhun and Aarti"], _vm.Tracks.Select(t => t.Title).ToArray());

        _vm.SelectedCategory = _vm.Categories[0];
        Assert.Null(_vm.LastQuery!.CategoryId);
        Assert.Equal(3, _vm.TotalCount);
    }

    [Fact]
    public async Task Each_track_shows_one_pill_per_category_in_category_order()
    {
        await _vm.LoadAsync();

        var row = _vm.Tracks.Single(t => t.Id == _both.Id);
        Assert.Equal(
            [new CategoryPill(_categories[0].Id, "Dhun", TagColor.Accent), new CategoryPill(_categories[2].Id, "Aarti", TagColor.Amber)],
            row.Pills);
        Assert.Equal("Dhun, Aarti", row.CategoryName);
        Assert.Empty(_vm.Tracks.Single(t => t.Title == "Untagged").Pills);
        Assert.Equal("1:30", row.TopLine);
    }

    [Fact]
    public async Task The_playing_track_row_is_marked_current()
    {
        await _vm.LoadAsync();

        _vm.SetCurrentTrack(_both.Id);

        Assert.Equal([_both.Id], _vm.Tracks.Where(t => t.IsCurrent).Select(t => t.Id).ToArray());
        await _vm.NextPageCommand.ExecuteAsync(null); // reloads keep the mark
        _vm.SelectedCategory = _vm.Categories.Single(c => c.Name == "Aarti");
        Assert.True(_vm.Tracks.Single().IsCurrent);
    }
}

public class CategoryAssignmentTests
{
    private readonly FakeSabhaClient _client = new();
    private readonly TestUi _ui = new();
    private readonly CategoryDto _dhun = new(Guid.NewGuid(), "Dhun", 0, 1);
    private readonly CategoryDto _aarti = new(Guid.NewGuid(), "Aarti", 1, 1);
    private readonly TrackDto[] _tracks;

    public CategoryAssignmentTests()
    {
        _client.Library.Categories.AddRange([_dhun, _aarti]);
        _tracks =
        [
            Track("Morning Dhun", _dhun.Id),
            Track("Evening Aarti", _aarti.Id),
            Track("Flute Dhun", _dhun.Id, _aarti.Id),
            Track("Sitar"),
        ];
        _client.Library.Tracks.AddRange(_tracks);
    }

    private static TrackDto Track(string title, params Guid[] tags) =>
        new(Guid.NewGuid(), title, "Artist", tags, 120, AnalysisStatus.Ready, null, DateTimeOffset.UtcNow);

    [Fact]
    public async Task Add_tracks_sends_the_ticked_tracks_including_ones_hidden_by_the_search()
    {
        var vm = new CategoriesAdminViewModel(_client, _ui.Services);
        await vm.LoadAsync();
        CategoryTracksSheet? seen = null;
        _ui.Dialogs.OnSheet = async sheet =>
        {
            seen = Assert.IsType<CategoryTracksSheet>(sheet);
            Assert.Equal(["Morning Dhun", "Flute Dhun"], seen.VisibleTracks.Where(t => t.IsSelected).Select(t => t.Title).ToArray());
            Assert.Equal("2 tracks selected", seen.SelectionText);

            seen.SearchText = "sitar";
            Assert.Equal(["Sitar"], seen.VisibleTracks.Select(t => t.Title).ToArray());
            seen.VisibleTracks.Single().IsSelected = true;

            seen.SearchText = "morning";
            seen.VisibleTracks.Single().IsSelected = false;
            seen.SearchText = "";
            Assert.Equal(2, seen.SelectedCount);

            await seen.SaveCommand.ExecuteAsync(null);
        };

        await vm.AddTracksCommand.ExecuteAsync(vm.Categories.Single(c => c.Name == "Dhun"));

        var call = Assert.Single(_client.CallsTo("SetCategoryTracksAsync"));
        Assert.Equal(_dhun.Id, call.Args[0]);
        Assert.Equal([_tracks[2].Id, _tracks[3].Id], ((IReadOnlyList<Guid>)call.Args[1]!).ToArray());
        Assert.Equal("Tracks in \"Dhun\"", seen!.Title);
        Assert.Equal("\"Dhun\" now has 2 tracks.", vm.StatusMessage);
        Assert.Equal(2, _client.CallsTo("GetCategoriesAsync").Count()); // reloaded for the new counts
    }

    [Fact]
    public async Task Cancelling_add_tracks_changes_nothing()
    {
        var vm = new CategoriesAdminViewModel(_client, _ui.Services);
        await vm.LoadAsync();

        await vm.AddTracksCommand.ExecuteAsync(vm.Categories[0]);

        Assert.Single(_ui.Dialogs.Sheets);
        Assert.Empty(_client.CallsTo("SetCategoryTracksAsync"));
    }

    [Fact]
    public async Task Add_tracks_loads_the_whole_library_page_by_page()
    {
        for (var i = 0; i < LibraryLimits.MaxPageSize + 3; i++)
        {
            _client.Library.Tracks.Add(Track($"Bulk {i:000}"));
        }

        var sheet = new CategoryTracksSheet(_client, _dhun.Id, _dhun.Name);
        Assert.True(await sheet.LoadAsync());

        Assert.Equal(_tracks.Length + LibraryLimits.MaxPageSize + 3, sheet.VisibleTracks.Count);
        var queries = _client.CallsTo("GetTracksAsync").Select(c => (TrackQuery)c.Args[0]!).ToArray();
        Assert.Equal([0, LibraryLimits.MaxPageSize], queries.Select(q => q.Skip).ToArray());
        Assert.All(queries, q => Assert.Equal(LibraryLimits.MaxPageSize, q.Take));
    }

    [Fact]
    public async Task Track_categories_sheet_ticks_current_tags_creates_new_ones_and_saves_the_set()
    {
        var shell = ConnectedShellWith(_client, _ui, Role.Admin);
        await shell.Library.LoadAsync();
        var row = shell.Library.Tracks.Single(t => t.Title == "Morning Dhun");
        _ui.Dialogs.PromptResults.Enqueue("Bhajan");
        _ui.Dialogs.OnSheet = async sheet =>
        {
            var s = Assert.IsType<TrackCategoriesSheet>(sheet);
            Assert.Equal(["Dhun"], s.Choices.Where(c => c.IsSelected).Select(c => c.Name).ToArray());
            Assert.Equal([TagColor.Accent, TagColor.Violet], s.Choices.Select(c => c.Color).ToArray());

            await s.NewCategoryCommand.ExecuteAsync(null);
            s.Choices.Single(c => c.Name == "Dhun").IsSelected = false;
            s.Choices.Single(c => c.Name == "Aarti").IsSelected = true;
            await s.SaveCommand.ExecuteAsync(null);
        };

        await shell.Library.EditCategoriesCommand.ExecuteAsync(row);

        Assert.Equal("Bhajan", _client.CallsTo("CreateCategoryAsync").Single().Args[0]);
        var bhajan = _client.Library.Categories.Single(c => c.Name == "Bhajan");
        var update = Assert.IsType<TrackUpdateDto>(_client.CallsTo("UpdateTrackAsync").Single().Args[0]);
        Assert.Equal((row.Id, "Morning Dhun", "Artist"), (update.TrackId, update.Title, update.Artist));
        Assert.Equal([_aarti.Id, bhajan.Id], update.CategoryIds);
        Assert.Equal("Categories saved for \"Morning Dhun\".", shell.Library.StatusMessage);
    }

    [Fact]
    public async Task Category_editing_from_the_library_is_admin_only()
    {
        var shell = ConnectedShellWith(_client, _ui, signedInAs: null);
        await shell.Library.LoadAsync();
        Assert.False(shell.Library.CanManageCategories);

        await shell.Library.EditCategoriesCommand.ExecuteAsync(shell.Library.Tracks[0]);
        await shell.Library.AddTracksToCategoryCommand.ExecuteAsync(shell.Library.Categories[1]);

        Assert.Empty(_ui.Dialogs.Sheets);
    }

    [Fact]
    public async Task Add_tracks_from_a_library_tab_uses_that_category()
    {
        var shell = ConnectedShellWith(_client, _ui, Role.Admin);
        await shell.Library.LoadAsync();
        _ui.Dialogs.OnSheet = sheet => ((CategoryTracksSheet)sheet).SaveCommand.ExecuteAsync(null);

        await shell.Library.AddTracksToCategoryCommand.ExecuteAsync(shell.Library.Categories.Single(c => c.Name == "Aarti"));
        await shell.Library.AddTracksToCategoryCommand.ExecuteAsync(shell.Library.Categories[0]); // "All": nothing to edit

        var call = Assert.Single(_client.CallsTo("SetCategoryTracksAsync"));
        Assert.Equal(_aarti.Id, call.Args[0]);
        Assert.Equal([_tracks[1].Id, _tracks[2].Id], ((IReadOnlyList<Guid>)call.Args[1]!).ToArray());
    }

    [Fact]
    public async Task Tracks_admin_new_category_is_added_ticked()
    {
        var vm = new TracksAdminViewModel(_client, _ui.Services);
        vm.SetCategories(_client.Library.Categories);
        await vm.LoadAsync();
        vm.SelectedTrack = vm.Tracks.Single(t => t.Title == "Sitar");
        _ui.Dialogs.PromptResults.Enqueue("Bhajan");

        await vm.NewCategoryCommand.ExecuteAsync(null);
        await vm.SaveCommand.ExecuteAsync(null);

        var bhajan = _client.Library.Categories.Single(c => c.Name == "Bhajan");
        Assert.Equal([bhajan.Id], Assert.IsType<TrackUpdateDto>(_client.CallsTo("UpdateTrackAsync").Single().Args[0]).CategoryIds);
        Assert.Equal(TagColor.Amber, vm.EditCategories.Last().Color);
    }

    private static ShellViewModel ConnectedShellWith(FakeSabhaClient client, TestUi ui, Role? signedInAs)
    {
        var shell = new ShellViewModel(client, ui.Services);
        shell.Start();
        client.SetConnectionState(ConnectionState.Connected);
        if (signedInAs is { } role)
        {
            client.SetSession(FakeSabhaClient.MakeSession(role));
        }

        return shell;
    }
}

public class ImportCategoryMultiSelectTests
{
    private readonly FakeSabhaClient _client = new();
    private readonly TestUi _ui = new();
    private readonly CategoryDto _dhun = new(Guid.NewGuid(), "Dhun", 0, 0);
    private readonly CategoryDto _kirtan = new(Guid.NewGuid(), "Kirtan", 1, 0);
    private readonly CategoryDto _aarti = new(Guid.NewGuid(), "Aarti", 2, 0);

    private ImportViewModel CreateWithCategories()
    {
        var vm = new ImportViewModel(_client, _ui.Services) { ChunkSize = 4 };
        vm.SetCategories([_aarti, _dhun, _kirtan]);
        return vm;
    }

    private ImportMetadata MetadataFor(int index) =>
        (ImportMetadata)_client.CallsTo("CompleteImportAsync").ElementAt(index).Args[1]!;

    [Fact]
    public async Task Several_default_categories_are_sent_in_category_order()
    {
        var vm = CreateWithCategories();
        Assert.Equal(["Dhun", "Kirtan", "Aarti"], vm.DefaultCategories.Choices.Select(c => c.Name).ToArray());
        vm.DefaultCategories.Choices[2].IsSelected = true;
        vm.DefaultCategories.Choices[0].IsSelected = true;
        Assert.Equal("Dhun, Aarti", vm.DefaultCategories.Summary);

        vm.AddFiles([_ui.FileSystem.Add("a.mp3", 5)]);
        Assert.Equal("Dhun, Aarti", vm.Files[0].Categories.Summary);
        await vm.StartCommand.ExecuteAsync(null);

        Assert.Equal([_dhun.Id, _aarti.Id], MetadataFor(0).CategoryIds!);
    }

    [Fact]
    public async Task Each_file_can_have_its_own_categories_or_none()
    {
        var vm = CreateWithCategories();
        vm.DefaultCategories.Choices[1].IsSelected = true;
        vm.AddFiles([_ui.FileSystem.Add("a.mp3", 5), _ui.FileSystem.Add("b.mp3", 5), _ui.FileSystem.Add("c.mp3", 5)]);
        vm.Files[1].Categories.Choices[0].IsSelected = true;
        vm.Files[2].Categories.Choices[1].IsSelected = false;
        Assert.Equal(CategorySelection.NoneText, vm.Files[2].Categories.Summary);

        await vm.StartCommand.ExecuteAsync(null);

        Assert.Equal([_kirtan.Id], MetadataFor(0).CategoryIds!);
        Assert.Equal([_dhun.Id, _kirtan.Id], MetadataFor(1).CategoryIds!);
        Assert.Null(MetadataFor(2).CategoryIds);
    }

    [Fact]
    public void Ticks_survive_a_category_refresh_and_new_categories_start_unticked()
    {
        var vm = CreateWithCategories();
        vm.DefaultCategories.Choices[0].IsSelected = true;
        vm.AddFiles([_ui.FileSystem.Add("a.mp3", 5)]);
        vm.Files[0].Categories.Choices[2].IsSelected = true;
        var bhajan = new CategoryDto(Guid.NewGuid(), "Bhajan", 3, 0);

        vm.SetCategories([_dhun, _kirtan, _aarti, bhajan]);

        Assert.Equal([_dhun.Id], vm.DefaultCategories.SelectedIds);
        Assert.Equal([_dhun.Id, _aarti.Id], vm.Files[0].Categories.SelectedIds);
        Assert.False(vm.Files[0].Categories.Choices.Single(c => c.Name == "Bhajan").IsSelected);
        Assert.Equal(TagColor.Blue, vm.Files[0].Categories.Choices.Single(c => c.Name == "Bhajan").Color);
    }

    [Fact]
    public void Apply_to_all_copies_the_default_ticks_to_waiting_files_only()
    {
        var vm = CreateWithCategories();
        vm.AddFiles([_ui.FileSystem.Add("a.mp3", 5), _ui.FileSystem.Add("b.txt", 5)]);
        vm.DefaultCategories.Choices[1].IsSelected = true;
        vm.DefaultCategories.Choices[2].IsSelected = true;

        vm.ApplyDefaultCategoriesToAllCommand.Execute(null);

        Assert.Equal([_kirtan.Id, _aarti.Id], vm.Files[0].Categories.SelectedIds);
        Assert.Empty(vm.Files[1].Categories.SelectedIds); // rejected file: not editable
    }
}

public class AdminRailTests
{
    [Fact]
    public void Admin_rail_item_appears_only_while_an_administrator_is_signed_in()
    {
        var (shell, client, _) = ConnectedShell();
        Assert.False(shell.IsAdminVisible);
        Assert.False(shell.Library.CanManageCategories);
        Assert.Equal("Admin sign-in", shell.AccountName);
        Assert.Equal("", shell.AccountInitial);

        client.SetSession(new SessionDto("t", "priya", Role.Admin, DateTimeOffset.UtcNow.AddMinutes(15)));
        Assert.True(shell.IsAdminVisible);
        Assert.True(shell.Library.CanManageCategories);
        Assert.Equal(("priya", "P"), (shell.AccountName, shell.AccountInitial));
        shell.NavigateCommand.Execute(NavPage.Admin);
        Assert.Equal(NavPage.Admin, shell.CurrentPage);

        client.Events.SessionEnded("idle for 15 minutes");
        Assert.False(shell.IsAdminVisible);
        Assert.False(shell.Library.CanManageCategories);
        Assert.Equal(NavPage.Library, shell.CurrentPage);
        Assert.Equal("Admin sign-in", shell.AccountName);
    }

    [Fact]
    public void A_volunteer_session_never_shows_the_admin_rail_item()
    {
        var (shell, _, _) = ConnectedShell(Role.Volunteer);

        Assert.False(shell.IsAdminVisible);
        shell.NavigateCommand.Execute(NavPage.Admin);
        Assert.Equal(NavPage.Library, shell.CurrentPage);
    }

    [Theory]
    [InlineData(ConnectionState.Connected, "Live")]
    [InlineData(ConnectionState.Connecting, "Connecting…")]
    [InlineData(ConnectionState.Reconnecting, "Reconnecting…")]
    [InlineData(ConnectionState.Disconnected, "Disconnected")]
    public void Status_pill_text_follows_the_connection(ConnectionState state, string text)
    {
        var (shell, client, _) = ConnectedShell();

        client.SetConnectionState(state);

        Assert.Equal(text, shell.ConnectionStatusText);
    }

    [Fact]
    public void The_library_marks_the_track_now_playing()
    {
        var client = new FakeSabhaClient();
        client.Library.Tracks.Add(new TrackDto(TrackA, "Aarti", null, [], 200, AnalysisStatus.Ready, null, DateTimeOffset.UtcNow));
        client.Library.AddTrack("Other");
        var shell = new ShellViewModel(client, new TestUi().Services);
        shell.Start();
        client.SetConnectionState(ConnectionState.Connected);
        Assert.Equal(2, shell.Library.Tracks.Count);
        Assert.DoesNotContain(shell.Library.Tracks, t => t.IsCurrent);

        client.Events.PlaybackStateChanged(State(1, trackId: TrackA));

        Assert.Equal([TrackA], shell.Library.Tracks.Where(t => t.IsCurrent).Select(t => t.Id).ToArray());
    }
}
