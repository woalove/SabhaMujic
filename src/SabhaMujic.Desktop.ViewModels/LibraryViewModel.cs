using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SabhaMujic.Client;
using SabhaMujic.Contracts;
using SabhaMujic.Desktop.ViewModels.Infrastructure;

namespace SabhaMujic.Desktop.ViewModels;

/// <summary>Sidebar entry: "All" (Id null) or a category.</summary>
public sealed partial class CategoryItemViewModel : ObservableObject
{
    public CategoryItemViewModel(Guid? id, string name, int trackCount)
    {
        Id = id;
        Name = name;
        TrackCount = trackCount;
    }

    public Guid? Id { get; }

    public bool IsAll => Id is null;

    [ObservableProperty]
    public partial string Name { get; set; }

    [ObservableProperty]
    public partial int TrackCount { get; set; }
}

/// <summary>One track in the library list.</summary>
public sealed class TrackItemViewModel
{
    public TrackItemViewModel(LibraryViewModel owner, TrackDto track, string? categoryName)
    {
        Owner = owner;
        Track = track;
        CategoryName = categoryName;
    }

    public LibraryViewModel Owner { get; }

    public TrackDto Track { get; }

    public Guid Id => Track.Id;

    public string Title => Track.Title;

    public string? Artist => Track.Artist;

    public string? CategoryName { get; }

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
/// Library browser: category sidebar (All + categories with counts), debounced search, paged track list,
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
    private readonly Dictionary<Guid, string> _categoryNames = [];
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
            var categoryName = track.CategoryId is { } c && _categoryNames.TryGetValue(c, out var n) ? n : null;
            Tracks.Add(new TrackItemViewModel(this, track, categoryName));
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

        _categoryNames.Clear();
        foreach (var c in categories)
        {
            _categoryNames[c.Id] = c.Name;
        }

        var selectedId = SelectedCategory?.Id;
        _suppressReload = true;
        try
        {
            Categories.Clear();
            Categories.Add(new CategoryItemViewModel(null, "All", all.Total));
            foreach (var c in categories.OrderBy(c => c.SortOrder).ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                Categories.Add(new CategoryItemViewModel(c.Id, c.Name, c.TrackCount));
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
