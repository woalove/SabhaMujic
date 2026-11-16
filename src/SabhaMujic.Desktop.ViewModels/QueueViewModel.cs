using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SabhaMujic.Client;
using SabhaMujic.Contracts;
using SabhaMujic.Desktop.ViewModels.Infrastructure;

namespace SabhaMujic.Desktop.ViewModels;

/// <summary>One row of the play queue.</summary>
public sealed partial class QueueItemViewModel : ObservableObject
{
    public QueueItemViewModel(QueueViewModel owner, QueueItemDto item, int index)
    {
        Owner = owner;
        QueueItemId = item.QueueItemId;
        TrackId = item.TrackId;
        Index = index;
    }

    public QueueViewModel Owner { get; }

    public Guid QueueItemId { get; }

    public Guid TrackId { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Number))]
    public partial int Index { get; set; }

    public int Number => Index + 1;

    [ObservableProperty]
    public partial string Title { get; set; } = "…";

    [ObservableProperty]
    public partial string? Artist { get; set; }

    [ObservableProperty]
    public partial string DurationText { get; set; } = "";

    [ObservableProperty]
    public partial bool IsCurrent { get; set; }

    internal void ApplyTrack(TrackDto? track)
    {
        Title = track?.Title ?? "Unknown track";
        Artist = track?.Artist;
        DurationText = track is null ? "" : Formatting.Time(track.DurationSeconds);
    }
}

/// <summary>
/// The play queue. Snapshots come from <see cref="IClientEvents.QueueChanged"/> and from the results of queue
/// commands; snapshots with a revision not newer than the last applied one are ignored.
/// </summary>
public sealed partial class QueueViewModel : ViewModelBase
{
    private readonly ISabhaClient _client;
    private readonly UiServices _ui;
    private readonly TrackInfoCache _tracks;
    private long? _lastRevision;

    public QueueViewModel(ISabhaClient client, UiServices ui, TrackInfoCache tracks)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
        _tracks = tracks ?? throw new ArgumentNullException(nameof(tracks));
        _client.Events.QueueReceived += queue => _ui.Dispatcher.Post(() => ApplyQueue(queue));
        _tracks.Invalidated += id =>
        {
            foreach (var item in Items.Where(i => i.TrackId == id).ToArray())
            {
                Run(() => FillTrackAsync(item));
            }
        };
    }

    public ObservableCollection<QueueItemViewModel> Items { get; } = [];

    [ObservableProperty]
    public partial int CurrentIndex { get; private set; } = -1;

    [ObservableProperty]
    public partial QueueItemViewModel? SelectedItem { get; set; }

    [ObservableProperty]
    public partial string Summary { get; private set; } = "Queue is empty";

    public long? LastRevision => _lastRevision;

    /// <summary>Applies a snapshot unless it is stale. <paramref name="force"/> is used after (re)connecting.</summary>
    public void ApplyQueue(QueueDto queue, bool force = false)
    {
        ArgumentNullException.ThrowIfNull(queue);
        if (!force && _lastRevision is { } last && queue.Revision <= last)
        {
            return;
        }

        _lastRevision = queue.Revision;
        var selectedId = SelectedItem?.QueueItemId;
        var existing = Items.ToDictionary(i => i.QueueItemId);

        // Rebuild in order, reusing row view models (keeps resolved titles and avoids flicker).
        var rows = new List<QueueItemViewModel>(queue.Items.Count);
        var unresolved = new List<QueueItemViewModel>();
        for (var i = 0; i < queue.Items.Count; i++)
        {
            var dto = queue.Items[i];
            if (existing.TryGetValue(dto.QueueItemId, out var row))
            {
                row.Index = i;
            }
            else
            {
                row = new QueueItemViewModel(this, dto, i);
                if (_tracks.TryGet(dto.TrackId, out var known))
                {
                    row.ApplyTrack(known);
                }
                else
                {
                    unresolved.Add(row);
                }
            }

            row.IsCurrent = i == queue.CurrentIndex;
            rows.Add(row);
        }

        if (unresolved.Count > 0)
        {
            Run(() => FillTracksAsync(unresolved));
        }

        SyncCollection(rows);
        CurrentIndex = queue.CurrentIndex;
        SelectedItem = selectedId is null ? null : Items.FirstOrDefault(i => i.QueueItemId == selectedId);
        Summary = Items.Count switch
        {
            0 => "Queue is empty",
            1 => "1 track",
            var n => $"{n} tracks",
        };
    }

    public Task<bool> RefreshAsync() => TryAsync(async () =>
    {
        var queue = await _client.Playback.GetQueueAsync().ConfigureAwait(true);
        ApplyQueue(queue, force: true);
    });

    public void Reset()
    {
        _lastRevision = null;
        Items.Clear();
        CurrentIndex = -1;
        SelectedItem = null;
        Summary = "Queue is empty";
        ErrorMessage = StatusMessage = null;
    }

    /// <summary>
    /// Moves the item at <paramref name="oldIndex"/> so that it ends up at <paramref name="newIndex"/> in the
    /// resulting list (the semantics of <see cref="IPlaybackApi.MoveQueueItemAsync"/>). Used by drag-and-drop.
    /// </summary>
    public Task MoveItemAsync(int oldIndex, int newIndex)
    {
        if (oldIndex < 0 || oldIndex >= Items.Count)
        {
            return Task.CompletedTask;
        }

        newIndex = Math.Clamp(newIndex, 0, Items.Count - 1);
        if (newIndex == oldIndex)
        {
            return Task.CompletedTask;
        }

        var item = Items[oldIndex];
        return TryAsync(async () =>
        {
            var queue = await _client.Playback.MoveQueueItemAsync(item.QueueItemId, newIndex).ConfigureAwait(true);
            ApplyQueue(queue);
        });
    }

    [RelayCommand]
    private Task MoveUpAsync(QueueItemViewModel? item) =>
        item is null ? Task.CompletedTask : MoveItemAsync(item.Index, item.Index - 1);

    [RelayCommand]
    private Task MoveDownAsync(QueueItemViewModel? item) =>
        item is null ? Task.CompletedTask : MoveItemAsync(item.Index, item.Index + 1);

    [RelayCommand]
    private Task RemoveAsync(QueueItemViewModel? item) => item is null
        ? Task.CompletedTask
        : TryAsync(async () => ApplyQueue(await _client.Playback.RemoveFromQueueAsync(item.QueueItemId).ConfigureAwait(true)));

    [RelayCommand]
    private Task PlayItemAsync(QueueItemViewModel? item) => item is null
        ? Task.CompletedTask
        : TryAsync(() => _client.Playback.PlayQueueItemAsync(item.QueueItemId));

    [RelayCommand]
    private async Task ClearAsync()
    {
        if (Items.Count <= 1)
        {
            return;
        }

        var confirmed = await _ui.Dialogs.ConfirmAsync(
            "Clear queue",
            "Remove all tracks from the queue except the one playing now?",
            "Clear").ConfigureAwait(true);
        if (!confirmed)
        {
            return;
        }

        await TryAsync(async () => ApplyQueue(await _client.Playback.ClearQueueAsync().ConfigureAwait(true))).ConfigureAwait(true);
    }

    /// <summary>One batched lookup for every new row (instead of one request per track).</summary>
    private async Task FillTracksAsync(IReadOnlyList<QueueItemViewModel> rows)
    {
        var tracks = await _tracks.GetManyAsync(rows.Select(r => r.TrackId)).ConfigureAwait(true);
        foreach (var row in rows)
        {
            row.ApplyTrack(tracks.GetValueOrDefault(row.TrackId));
        }
    }

    private async Task FillTrackAsync(QueueItemViewModel row)
    {
        var track = await _tracks.GetAsync(row.TrackId).ConfigureAwait(true);
        row.ApplyTrack(track);
    }

    private void SyncCollection(List<QueueItemViewModel> rows)
    {
        // Minimal edits so the ListBox keeps scroll position and selection where possible.
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var at = Items.IndexOf(row);
            if (at == i)
            {
                continue;
            }

            if (at < 0)
            {
                Items.Insert(i, row);
            }
            else
            {
                Items.Move(at, i);
            }
        }

        while (Items.Count > rows.Count)
        {
            Items.RemoveAt(Items.Count - 1);
        }
    }
}
