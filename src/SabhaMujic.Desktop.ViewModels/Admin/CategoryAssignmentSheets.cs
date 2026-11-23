using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SabhaMujic.Client;
using SabhaMujic.Contracts;
using SabhaMujic.Desktop.ViewModels.Infrastructure;

namespace SabhaMujic.Desktop.ViewModels.Admin;

/// <summary>
/// Track → "Categories…": a checklist of every category (with its coloured dot) plus "New category". Saving replaces the
/// track's categories with <see cref="IAdminApi.UpdateTrackAsync"/> (title and artist unchanged). Admin only.
/// </summary>
public sealed partial class TrackCategoriesSheet : SheetViewModel
{
    private readonly ISabhaClient _client;
    private readonly UiServices _ui;
    private TrackDto _track;

    public TrackCategoriesSheet(ISabhaClient client, UiServices ui, TrackDto track)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
        _track = track ?? throw new ArgumentNullException(nameof(track));
    }

    public override string Title => $"Categories for \"{_track.Title}\"";

    public ObservableCollection<CategoryChoice> Choices { get; } = [];

    /// <summary>The ticked categories, in category order.</summary>
    public IReadOnlyList<Guid> SelectedIds => Choices.Where(c => c.IsSelected).Select(c => c.Id).ToArray();

    /// <summary>The track as saved (after a successful save).</summary>
    public TrackDto Track => _track;

    /// <summary>Loads the latest track and the category list. Call before showing the sheet.</summary>
    public Task<bool> LoadAsync() => TryAsync(async () =>
    {
        _track = await _client.Library.GetTrackAsync(_track.Id).ConfigureAwait(true);
        var categories = await _client.Library.GetCategoriesAsync().ConfigureAwait(true);
        var ticked = _track.CategoryIds.ToHashSet();
        Choices.Clear();
        var position = 0;
        foreach (var c in categories.OrderBy(c => c.SortOrder))
        {
            Choices.Add(new CategoryChoice(c.Id, c.Name, ticked.Contains(c.Id), TagColors.ForPosition(position++)));
        }

        OnPropertyChanged(nameof(Title));
    }, showBusy: true);

    [RelayCommand]
    private async Task NewCategoryAsync()
    {
        var name = await _ui.Dialogs.PromptTextAsync("New category", "Category name:").ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        await TryAsync(async () =>
        {
            var created = await _client.Admin.CreateCategoryAsync(name.Trim()).ConfigureAwait(true);
            Choices.Add(new CategoryChoice(created.Id, created.Name, isSelected: true, TagColors.ForPosition(Choices.Count)));
        }).ConfigureAwait(true);
    }

    [RelayCommand]
    private Task SaveAsync() => TryAsync(async () =>
    {
        _track = await _client.Admin
            .UpdateTrackAsync(new TrackUpdateDto(_track.Id, _track.Title, _track.Artist, SelectedIds))
            .ConfigureAwait(true);
        Close(saved: true);
    }, showBusy: true);
}

/// <summary>One library track in the "Add tracks…" checklist.</summary>
public sealed partial class TrackChoice(TrackDto track, bool isSelected) : ObservableObject
{
    public Guid Id => Track.Id;

    public TrackDto Track { get; } = track;

    public string Title => Track.Title;

    public string? Artist => Track.Artist;

    public string DurationText => Formatting.Time(Track.DurationSeconds);

    [ObservableProperty]
    public partial bool IsSelected { get; set; } = isSelected;
}

/// <summary>
/// Category → "Add tracks…": a searchable checklist of the whole library. Saving calls
/// <see cref="IAdminApi.SetCategoryTracksAsync"/>, which replaces this category's track list and keeps each track's other
/// categories. The search only filters what is shown; ticks on hidden tracks are kept. Admin only.
/// </summary>
public sealed partial class CategoryTracksSheet : SheetViewModel
{
    private readonly ISabhaClient _client;
    private readonly List<TrackChoice> _all = [];

    public CategoryTracksSheet(ISabhaClient client, Guid categoryId, string categoryName)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        CategoryId = categoryId;
        CategoryName = categoryName ?? "";
    }

    public Guid CategoryId { get; }

    public string CategoryName { get; }

    public override string Title => $"Tracks in \"{CategoryName}\"";

    /// <summary>Tracks matching <see cref="SearchText"/>, in library order.</summary>
    public ObservableCollection<TrackChoice> VisibleTracks { get; } = [];

    [ObservableProperty]
    public partial string SearchText { get; set; } = "";

    public int SelectedCount => _all.Count(t => t.IsSelected);

    public string SelectionText => SelectedCount == 1 ? "1 track selected" : $"{SelectedCount} tracks selected";

    /// <summary>The ticked tracks, in library order (what Save sends).</summary>
    public IReadOnlyList<Guid> SelectedIds => _all.Where(t => t.IsSelected).Select(t => t.Id).ToArray();

    /// <summary>Loads the whole library (page by page). Call before showing the sheet.</summary>
    public Task<bool> LoadAsync() => TryAsync(async () =>
    {
        foreach (var choice in _all)
        {
            choice.PropertyChanged -= OnChoiceChanged;
        }

        _all.Clear();
        var skip = 0;
        while (true)
        {
            var page = await _client.Library
                .GetTracksAsync(new TrackQuery(null, null, skip, LibraryLimits.MaxPageSize))
                .ConfigureAwait(true);
            foreach (var track in page.Items)
            {
                var choice = new TrackChoice(track, track.CategoryIds.Contains(CategoryId));
                choice.PropertyChanged += OnChoiceChanged;
                _all.Add(choice);
            }

            skip += page.Items.Count;
            if (page.Items.Count == 0 || skip >= page.Total)
            {
                break;
            }
        }

        ApplyFilter();
        OnSelectionChanged();
    }, showBusy: true);

    [RelayCommand]
    private Task SaveAsync() => TryAsync(async () =>
    {
        await _client.Admin.SetCategoryTracksAsync(CategoryId, SelectedIds).ConfigureAwait(true);
        Close(saved: true);
    }, showBusy: true);

    [RelayCommand]
    private void ClearSearch() => SearchText = "";

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        var term = SearchText.Trim();
        VisibleTracks.Clear();
        foreach (var t in _all)
        {
            if (term.Length == 0 ||
                t.Title.Contains(term, StringComparison.CurrentCultureIgnoreCase) ||
                (t.Artist?.Contains(term, StringComparison.CurrentCultureIgnoreCase) ?? false))
            {
                VisibleTracks.Add(t);
            }
        }
    }

    private void OnChoiceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TrackChoice.IsSelected))
        {
            OnSelectionChanged();
        }
    }

    private void OnSelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(SelectionText));
    }
}
