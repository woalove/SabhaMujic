using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SabhaMujic.Client;
using SabhaMujic.Contracts;
using SabhaMujic.Desktop.ViewModels.Admin;
using SabhaMujic.Desktop.ViewModels.Infrastructure;

namespace SabhaMujic.Desktop.ViewModels;

/// <summary>Category tab: "All" (Id null) or a category, with its dot colour and track count.</summary>
public sealed partial class CategoryItemViewModel : ObservableObject
{
    public CategoryItemViewModel(Guid? id, string name, int trackCount, TagColor? color = null)
    {
        Id = id;
        Name = name;
        TrackCount = trackCount;
        Color = color;
    }

    public Guid? Id { get; }

    public bool IsAll => Id is null;

    /// <summary>Dot colour; null for "All" (no dot).</summary>
    public TagColor? Color { get; }

    public bool HasDot => Color is not null;

    [ObservableProperty]
    public partial string Name { get; set; }

    [ObservableProperty]
    public partial int TrackCount { get; set; }
}

/// <summary>One track in the library list.</summary>
public sealed partial class TrackItemViewModel : ObservableObject
{
    public TrackItemViewModel(LibraryViewModel owner, TrackDto track, IReadOnlyList<CategoryPill> pills)
    {
        Owner = owner;
        Track = track;
        Pills = pills ?? [];
        CategoryName = Pills.Count == 0 ? null : string.Join(", ", Pills.Select(p => p.Name));
    }

    public LibraryViewModel Owner { get; }

    public TrackDto Track { get; }

    public Guid Id => Track.Id;

    public string Title => Track.Title;

    public string? Artist => Track.Artist;

    /// <summary>The track's categories joined with commas (null when it has none).</summary>
    public string? CategoryName { get; }

    /// <summary>One pill per category, in category order.</summary>
    public IReadOnlyList<CategoryPill> Pills { get; }

    /// <summary>The track playing now (filled timeline node, accent title).</summary>
    [ObservableProperty]
    public partial bool IsCurrent { get; set; }

    /// <summary>Muted line above the title: duration and artist.</summary>
    public string TopLine => Artist is null ? DurationText : $"{DurationText} · {Artist}";

    public string DurationText => Formatting.Time(Track.DurationSeconds);

    public bool HasSmartLoop => Track.Loop is not null;

    public string AnalysisText => Track.Analysis switch
    {
        AnalysisStatus.Pending => "Waiting for analysis",
        AnalysisStatus.Analyzing => "Analyzing…",
        AnalysisStatus.Ready => Track.Loop is { IsManualOverride: true } ? "Loop: manual" : "Loop: auto",
        AnalysisStatus.Failed => "Analysis failed",
        _ => "",
    };
}

/// <summary>
/// Library browser: category tab row (All + categories with dot colours and counts), debounced search, paged track list,
/// per-track play/queue/playlist commands, and live refresh on <see cref="IClientEvents.LibraryChanged"/>.
/// </summary>
public sealed partial class LibraryViewModel : ViewModelBase
{
    public static readonly TimeSpan SearchDebounce = TimeSpan.FromMilliseconds(300);

    /// <summary>Bursts of library changes (e.g. a bulk import) are coalesced into one refresh.</summary>
    public static readonly TimeSpan RefreshCoalesce = TimeSpan.FromMilliseconds(250);

    public const int DefaultPageSize = 100;

    private readonly ISabhaClient _client;
    private readonly UiServices _ui;
    private readonly TrackInfoCache _tracks;
    private readonly PlaylistsViewModel _playlists;
    private readonly Debouncer _searchDebouncer;
    private readonly Debouncer _refreshCoalescer;
    private readonly Dictionary<Guid, CategoryPill> _categoryPills = [];
    private readonly Dictionary<Guid, int> _categoryOrder = [];
    private Guid? _currentTrackId;
    private int _requestSequence;
    private bool _suppressReload;
    private bool _loaded;

    public LibraryViewModel(ISabhaClient client, UiServices ui, TrackInfoCache tracks, PlaylistsViewModel playlists)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
        _tracks = tracks ?? throw new ArgumentNullException(nameof(tracks));
        _playlists = playlists ?? throw new ArgumentNullException(nameof(playlists));
        _searchDebouncer = new Debouncer(ui.Scheduler, SearchDebounce);
        _refreshCoalescer = new Debouncer(ui.Scheduler, RefreshCoalesce);
        _client.Events.LibraryChangeReceived += change => _ui.Dispatcher.Post(() => OnLibraryChanged(change));
    }

    public int PageSize { get; init; } = DefaultPageSize;

    public ObservableCollection<CategoryItemViewModel> Categories { get; } = [];

    public ObservableCollection<TrackItemViewModel> Tracks { get; } = [];

    /// <summary>Targets for "Add to playlist" (shared with the Playlists screen).</summary>
    public ObservableCollection<PlaylistItemViewModel> PlaylistTargets => _playlists.Playlists;

    [ObservableProperty]
    public partial CategoryItemViewModel? SelectedCategory { get; set; }

    /// <summary>True while an administrator is signed in: shows "Categories…" on tracks and "Add tracks…" on category tabs.</summary>
    [ObservableProperty]
    public partial bool CanManageCategories { get; set; }

    [ObservableProperty]
    public partial string SearchText { get; set; } = "";

    [ObservableProperty]
    public partial TrackItemViewModel? SelectedTrack { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageCount), nameof(PageText), nameof(IsEmpty))]
    [NotifyCanExecuteChangedFor(nameof(NextPageCommand), nameof(PreviousPageCommand))]
    public partial int TotalCount { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageText))]
    [NotifyCanExecuteChangedFor(nameof(NextPageCommand), nameof(PreviousPageCommand))]
    public partial int PageIndex { get; private set; }

    public int PageCount => Math.Max(1, (TotalCount + PageSize - 1) / PageSize);

    public bool IsEmpty => TotalCount == 0;

    public string PageText => TotalCount == 0
        ? "No tracks"
        : $"{(PageIndex * PageSize) + 1}–{Math.Min(TotalCount, (PageIndex + 1) * PageSize)} of {TotalCount}";

    /// <summary>The query the current page was loaded with (exposed for diagnostics and tests).</summary>
    public TrackQuery? LastQuery { get; private set; }

    public async Task<bool> LoadAsync()
    {
        var ok = await TryAsync(LoadCategoriesAsync, showBusy: true, clearError: false).ConfigureAwait(true);
        ok &= await LoadPageAsync(0).ConfigureAwait(true);
        _loaded = true;
        return ok;
    }

    public void Reset()
    {
        _loaded = false;
        _searchDebouncer.Cancel();
        _refreshCoalescer.Cancel();
        _requestSequence++;
        _suppressReload = true;
        try
        {
            Categories.Clear();
            Tracks.Clear();
            SelectedCategory = null;
            SelectedTrack = null;
            SearchText = "";
            TotalCount = 0;
            PageIndex = 0;
            ErrorMessage = StatusMessage = null;
        }
        finally
        {
            _suppressReload = false;
        }
    }

    [RelayCommand]
    private Task PlayNowAsync(TrackItemViewModel? track) => track is null ? Task.CompletedTask : TryAsync(async () =>
    {
        await _client.Playback.PlayTrackAsync(track.Id).ConfigureAwait(true);
        StatusMessage = $"Playing \"{track.Title}\".";
    });

    [RelayCommand]
    private Task PlayNextAsync(TrackItemViewModel? track) => track is null ? Task.CompletedTask : TryAsync(async () =>
    {
        await _client.Playback.EnqueueAsync([track.Id], playNext: true).ConfigureAwait(true);
        StatusMessage = $"\"{track.Title}\" will play next.";
    });

    [RelayCommand]
    private Task AddToQueueAsync(TrackItemViewModel? track) => track is null ? Task.CompletedTask : TryAsync(async () =>
    {
        await _client.Playback.EnqueueAsync([track.Id], playNext: false).ConfigureAwait(true);
        StatusMessage = $"Added \"{track.Title}\" to the queue.";
    });

    /// <summary>Adds <see cref="SelectedTrack"/> to <paramref name="playlist"/> (context menu "Add to playlist").</summary>
    [RelayCommand]
    private async Task AddToPlaylistAsync(PlaylistItemViewModel? playlist)
    {
        if (playlist is null || SelectedTrack is not { } track)
        {
            return;
        }

        ErrorMessage = null;
        if (await _playlists.AddTracksAsync(playlist.Id, [track.Id]).ConfigureAwait(true))
        {
            StatusMessage = $"Added \"{track.Title}\" to \"{playlist.Name}\".";
        }
        else
        {
            ErrorMessage = _playlists.ErrorMessage;
        }
    }

    /// <summary>Marks the row of the track playing now (called when now-playing changes).</summary>
    public void SetCurrentTrack(Guid? trackId)
    {
        _currentTrackId = trackId;
        foreach (var t in Tracks)
        {
            t.IsCurrent = t.Id == trackId;
        }
    }

    /// <summary>Admin: "Categories…" on a track — a checklist of every category, plus "New category".</summary>
    [RelayCommand]
    private async Task EditCategoriesAsync(TrackItemViewModel? track)
    {
        if (track is null || !CanManageCategories)
        {
            return;
        }

        var sheet = new TrackCategoriesSheet(_client, _ui, track.Track);
        if (!await sheet.LoadAsync().ConfigureAwait(true))
        {
            ErrorMessage = sheet.ErrorMessage;
            return;
        }

        if (await _ui.Dialogs.ShowSheetAsync(sheet).ConfigureAwait(true))
        {
            StatusMessage = $"Categories saved for \"{sheet.Track.Title}\".";
            await RefreshAfterCategoryChangeAsync().ConfigureAwait(true);
        }
    }

    /// <summary>Admin: "Add tracks…" on a category tab — a searchable checklist of the library.</summary>
    [RelayCommand]
    private async Task AddTracksToCategoryAsync(CategoryItemViewModel? category)
    {
        if (category?.Id is not { } categoryId || !CanManageCategories)
        {
            return;
        }

        var sheet = new CategoryTracksSheet(_client, categoryId, category.Name);
        if (!await sheet.LoadAsync().ConfigureAwait(true))
        {
            ErrorMessage = sheet.ErrorMessage;
            return;
        }

        if (await _ui.Dialogs.ShowSheetAsync(sheet).ConfigureAwait(true))
        {
            StatusMessage = $"Tracks saved for \"{category.Name}\".";
            await RefreshAfterCategoryChangeAsync().ConfigureAwait(true);
        }
    }

    private async Task RefreshAfterCategoryChangeAsync()
    {
        await TryAsync(LoadCategoriesAsync, clearError: false).ConfigureAwait(true);
        await LoadPageAsync(PageIndex).ConfigureAwait(true);
    }

    private bool CanGoNext() => PageIndex + 1 < PageCount;

    private bool CanGoPrevious() => PageIndex > 0;

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private Task NextPageAsync() => LoadPageAsync(PageIndex + 1);

    [RelayCommand(CanExecute = nameof(CanGoPrevious))]
    private Task PreviousPageAsync() => LoadPageAsync(PageIndex - 1);

    /// <summary>Runs the search immediately (Enter key), skipping the debounce.</summary>
    [RelayCommand]
    private Task SearchNowAsync()
    {
        _searchDebouncer.Cancel();
        return LoadPageAsync(0);
    }

    [RelayCommand]
    private void ClearSearch() => SearchText = "";

    partial void OnSearchTextChanged(string value)
    {
        if (_suppressReload)
        {
            return;
        }

        _searchDebouncer.Trigger(() => _ = LoadPageAsync(0));
    }

    partial void OnSelectedCategoryChanged(CategoryItemViewModel? value)
    {
        if (_suppressReload || value is null)
        {
            return;
        }

        _ = LoadPageAsync(0);
    }

    private async Task<bool> LoadPageAsync(int pageIndex)
    {
        var sequence = ++_requestSequence;
        var search = string.IsNullOrWhiteSpace(SearchText) ? null : SearchText.Trim();
        var query = new TrackQuery(SelectedCategory?.Id, search, Math.Max(0, pageIndex) * PageSize, PageSize);
        PagedResult<TrackDto>? result = null;
        var ok = await TryAsync(async () =>
        {
            result = await _client.Library.GetTracksAsync(query).ConfigureAwait(true);
        }, clearError: false).ConfigureAwait(true);

        // A newer request (typing, category click) superseded this one: drop the stale response.
        if (!ok || result is null || sequence != _requestSequence)
        {
            return ok;
        }

        // The page vanished (e.g. tracks deleted while on the last page): go back to the last valid page.
        if (result.Items.Count == 0 && result.Total > 0 && pageIndex > 0)
        {
            return await LoadPageAsync((result.Total - 1) / PageSize).ConfigureAwait(true);
        }

        LastQuery = query;
        var selectedId = SelectedTrack?.Id;
        Tracks.Clear();
        foreach (var track in result.Items)
        {
            _tracks.Put(track);
            var pills = track.CategoryIds
                .Where(_categoryPills.ContainsKey)
                .OrderBy(c => _categoryOrder[c])
                .Select(c => _categoryPills[c])
                .ToArray();
            Tracks.Add(new TrackItemViewModel(this, track, pills) { IsCurrent = track.Id == _currentTrackId });
        }

        PageIndex = Math.Max(0, pageIndex);
        TotalCount = result.Total;
        SelectedTrack = selectedId is null ? null : Tracks.FirstOrDefault(t => t.Id == selectedId);
        return true;
    }

    private async Task LoadCategoriesAsync()
    {
        var categories = await _client.Library.GetCategoriesAsync().ConfigureAwait(true);
        var all = await _client.Library.GetTracksAsync(new TrackQuery(Take: 1)).ConfigureAwait(true);

        var ordered = categories.OrderBy(c => c.SortOrder).ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        _categoryPills.Clear();
        _categoryOrder.Clear();
        for (var i = 0; i < ordered.Length; i++)
        {
            _categoryPills[ordered[i].Id] = new CategoryPill(ordered[i].Id, ordered[i].Name, TagColors.ForPosition(i));
            _categoryOrder[ordered[i].Id] = i;
        }

        var selectedId = SelectedCategory?.Id;
        _suppressReload = true;
        try
        {
            Categories.Clear();
            Categories.Add(new CategoryItemViewModel(null, "All", all.Total));
            for (var i = 0; i < ordered.Length; i++)
            {
                Categories.Add(new CategoryItemViewModel(ordered[i].Id, ordered[i].Name, ordered[i].TrackCount, TagColors.ForPosition(i)));
            }

            // Keep the selection; fall back to "All" if the category was deleted.
            SelectedCategory = Categories.FirstOrDefault(c => c.Id == selectedId) ?? Categories[0];
        }
        finally
        {
            _suppressReload = false;
        }
    }

    private void OnLibraryChanged(LibraryChangedDto change)
    {
        if (change.EntityId is { } id &&
            change.Kind is LibraryChangeKind.TrackUpdated or LibraryChangeKind.TrackRemoved or LibraryChangeKind.AnalysisUpdated)
        {
            _tracks.Invalidate(id);
        }

        if (!_loaded || change.Kind == LibraryChangeKind.PlaylistsChanged)
        {
            return;
        }

        _refreshCoalescer.Trigger(() => Run(async () =>
        {
            var previousCategory = SelectedCategory?.Id;
            await LoadCategoriesAsync().ConfigureAwait(true);
            if (SelectedCategory?.Id != previousCategory)
            {
                await LoadPageAsync(0).ConfigureAwait(true);
            }
            else
            {
                await LoadPageAsync(PageIndex).ConfigureAwait(true);
            }
        }));
    }
}
