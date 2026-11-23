using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SabhaMujic.Client;
using SabhaMujic.Contracts;
using SabhaMujic.Desktop.ViewModels.Infrastructure;

namespace SabhaMujic.Desktop.ViewModels.Admin;

/// <summary>
/// Admin: edit track metadata and categories (tags: any number per track), delete tracks, override or reset the Smart
/// Loop region, re-run analysis.
/// </summary>
public sealed partial class TracksAdminViewModel : ViewModelBase
{
    public const int MaxResults = 200;

    private readonly ISabhaClient _client;
    private readonly UiServices _ui;
    private readonly Debouncer _searchDebouncer;
    private int _requestSequence;

    public TracksAdminViewModel(ISabhaClient client, UiServices ui)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
        _searchDebouncer = new Debouncer(ui.Scheduler, LibraryViewModel.SearchDebounce);
    }

    public ObservableCollection<TrackDto> Tracks { get; } = [];

    /// <summary>Every category, checked when the selected track has it (multi-select).</summary>
    public ObservableCollection<CategoryChoice> EditCategories { get; } = [];

    [ObservableProperty]
    public partial string SearchText { get; set; } = "";

    [ObservableProperty]
    public partial string? ResultText { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(AnalysisText), nameof(CurrentLoopText))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(DeleteCommand), nameof(SaveLoopCommand),
        nameof(UseAutomaticLoopCommand), nameof(ReanalyzeCommand))]
    public partial TrackDto? SelectedTrack { get; set; }

    public bool HasSelection => SelectedTrack is not null;

    // Metadata editor.
    [ObservableProperty]
    public partial string EditTitle { get; set; } = "";

    [ObservableProperty]
    public partial string EditArtist { get; set; } = "";

    /// <summary>The checked categories, in category order.</summary>
    public IReadOnlyList<Guid> EditCategoryIds => EditCategories.Where(c => c.IsSelected).Select(c => c.Id).ToArray();

    // Loop override editor (seconds).
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LoopValidationError))]
    [NotifyCanExecuteChangedFor(nameof(SaveLoopCommand))]
    public partial double LoopStart { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LoopValidationError))]
    [NotifyCanExecuteChangedFor(nameof(SaveLoopCommand))]
    public partial double LoopEnd { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LoopValidationError))]
    [NotifyCanExecuteChangedFor(nameof(SaveLoopCommand))]
    public partial double LoopCrossfade { get; set; } = 0.2;

    public string AnalysisText => SelectedTrack?.Analysis switch
    {
        null => "",
        AnalysisStatus.Pending => "Waiting for analysis",
        AnalysisStatus.Analyzing => "Analyzing…",
        AnalysisStatus.Ready => "Analysis complete",
        AnalysisStatus.Failed => "Analysis failed (the whole trimmed track loops)",
        var s => s.ToString()!,
    };

    public string CurrentLoopText => SelectedTrack?.Loop is { } l
        ? string.Create(CultureInfo.CurrentCulture,
            $"{(l.IsManualOverride ? "Manual" : "Automatic")}: {Formatting.Time(l.StartSeconds)} → {Formatting.Time(l.EndSeconds)}, crossfade {l.CrossfadeSeconds:0.###} s, confidence {l.Confidence:P0}")
        : "No loop region yet";

    /// <summary>Why the loop override can't be saved, or null when it is valid.</summary>
    public string? LoopValidationError
    {
        get
        {
            if (SelectedTrack is null)
            {
                return null;
            }

            var duration = SelectedTrack.DurationSeconds;
            if (!double.IsFinite(LoopStart) || !double.IsFinite(LoopEnd) || !double.IsFinite(LoopCrossfade))
            {
                return "Enter numbers.";
            }

            if (LoopStart < 0)
            {
                return "Start must be 0 or more.";
            }

            if (LoopEnd <= LoopStart)
            {
                return "End must be after start.";
            }

            if (duration > 0 && LoopEnd > duration)
            {
                return $"End must be at most the track length ({duration:0.###} s).";
            }

            if (LoopCrossfade < 0 || LoopCrossfade > (LoopEnd - LoopStart) / 2)
            {
                return "Crossfade must be between 0 and half the loop length.";
            }

            return null;
        }
    }

    public void SetCategories(IReadOnlyList<CategoryDto> categories)
    {
        ArgumentNullException.ThrowIfNull(categories);
        // Keep unsaved ticks across a refresh of the list; new categories start unticked.
        var ticked = EditCategories.Count == 0 ? SelectedTrack?.CategoryIds.ToHashSet() ?? [] : EditCategoryIds.ToHashSet();
        EditCategories.Clear();
        foreach (var c in categories.OrderBy(c => c.SortOrder))
        {
            EditCategories.Add(new CategoryChoice(c.Id, c.Name, ticked.Contains(c.Id)));
        }
    }

    public Task<bool> LoadAsync() => TryAsync(SearchAsync, clearError: false);

    partial void OnSearchTextChanged(string value) => _searchDebouncer.Trigger(() => Run(SearchAsync));

    partial void OnSelectedTrackChanged(TrackDto? value)
    {
        EditTitle = value?.Title ?? "";
        EditArtist = value?.Artist ?? "";
        var tags = value?.CategoryIds.ToHashSet() ?? [];
        foreach (var choice in EditCategories)
        {
            choice.IsSelected = tags.Contains(choice.Id);
        }

        LoopStart = value?.Loop?.StartSeconds ?? 0;
        LoopEnd = value?.Loop?.EndSeconds ?? value?.DurationSeconds ?? 0;
        LoopCrossfade = value?.Loop?.CrossfadeSeconds ?? 0.2;
    }

    [RelayCommand]
    private Task RefreshAsync() => TryAsync(SearchAsync);

    private bool CanSave() => SelectedTrack is not null;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private Task SaveAsync()
    {
        if (SelectedTrack is not { } track)
        {
            return Task.CompletedTask;
        }

        if (string.IsNullOrWhiteSpace(EditTitle))
        {
            ErrorMessage = "The title can't be empty.";
            return Task.CompletedTask;
        }

        var update = new TrackUpdateDto(
            track.Id,
            EditTitle.Trim(),
            string.IsNullOrWhiteSpace(EditArtist) ? null : EditArtist.Trim(),
            EditCategoryIds);
        return TryAsync(async () =>
        {
            var updated = await _client.Admin.UpdateTrackAsync(update).ConfigureAwait(true);
            ReplaceTrack(updated);
            StatusMessage = "Saved.";
        });
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task DeleteAsync()
    {
        if (SelectedTrack is not { } track)
        {
            return;
        }

        var confirmed = await _ui.Dialogs.ConfirmAsync(
            "Delete track",
            $"Permanently delete \"{track.Title}\" from the library? It is also removed from playlists and the queue.",
            "Delete").ConfigureAwait(true);
        if (!confirmed)
        {
            return;
        }

        await TryAsync(async () =>
        {
            await _client.Admin.DeleteTrackAsync(track.Id).ConfigureAwait(true);
            Tracks.Remove(track);
            SelectedTrack = null;
            StatusMessage = "Deleted.";
        }).ConfigureAwait(true);
    }

    private bool CanSaveLoop() => SelectedTrack is not null && LoopValidationError is null;

    [RelayCommand(CanExecute = nameof(CanSaveLoop))]
    private Task SaveLoopAsync()
    {
        if (SelectedTrack is not { } track || LoopValidationError is not null)
        {
            return Task.CompletedTask;
        }

        var region = new LoopRegionDto(LoopStart, LoopEnd, LoopCrossfade, Confidence: 1.0, IsManualOverride: true);
        return TryAsync(async () =>
        {
            ReplaceTrack(await _client.Admin.SetLoopOverrideAsync(track.Id, region).ConfigureAwait(true));
            StatusMessage = "Manual loop saved.";
        });
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private Task UseAutomaticLoopAsync()
    {
        if (SelectedTrack is not { } track)
        {
            return Task.CompletedTask;
        }

        return TryAsync(async () =>
        {
            ReplaceTrack(await _client.Admin.SetLoopOverrideAsync(track.Id, null).ConfigureAwait(true));
            StatusMessage = "Using the automatic loop.";
        });
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private Task ReanalyzeAsync()
    {
        if (SelectedTrack is not { } track)
        {
            return Task.CompletedTask;
        }

        return TryAsync(async () =>
        {
            await _client.Admin.ReanalyzeTrackAsync(track.Id).ConfigureAwait(true);
            StatusMessage = "Re-analysis started. The loop updates when it finishes.";
        });
    }

    /// <summary>Refreshes one track after a library change event.</summary>
    public void OnTrackChanged(Guid trackId) => Run(async () =>
    {
        if (Tracks.Any(t => t.Id == trackId))
        {
            try
            {
                ReplaceTrack(await _client.Library.GetTrackAsync(trackId).ConfigureAwait(true));
            }
            catch (SabhaMujicException ex) when (ex.Code == ErrorCode.NotFound)
            {
                var gone = Tracks.FirstOrDefault(t => t.Id == trackId);
                if (gone is not null)
                {
                    Tracks.Remove(gone);
                }
            }
        }
    });

    private async Task SearchAsync()
    {
        var sequence = ++_requestSequence;
        var search = string.IsNullOrWhiteSpace(SearchText) ? null : SearchText.Trim();
        var result = await _client.Library.GetTracksAsync(new TrackQuery(null, search, 0, MaxResults)).ConfigureAwait(true);
        if (sequence != _requestSequence)
        {
            return;
        }

        var selected = SelectedTrack?.Id;
        Tracks.Clear();
        foreach (var t in result.Items)
        {
            Tracks.Add(t);
        }

        SelectedTrack = Tracks.FirstOrDefault(t => t.Id == selected);
        ResultText = result.Total > result.Items.Count
            ? $"Showing {result.Items.Count} of {result.Total}. Type in the search box to narrow down."
            : $"{result.Total} track(s)";
    }

    private void ReplaceTrack(TrackDto updated)
    {
        var index = Tracks.ToList().FindIndex(t => t.Id == updated.Id);
        var wasSelected = SelectedTrack?.Id == updated.Id;
        if (index >= 0)
        {
            Tracks[index] = updated;
        }

        if (wasSelected)
        {
            SelectedTrack = updated;
        }
    }
}
