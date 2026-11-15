using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SabhaMujic.Client;
using SabhaMujic.Contracts;
using SabhaMujic.Desktop.ViewModels.Infrastructure;

namespace SabhaMujic.Desktop.ViewModels;

public sealed partial class PlaylistItemViewModel : ObservableObject
{
    public PlaylistItemViewModel(PlaylistsViewModel owner, PlaylistDto dto)
    {
        Owner = owner;
        Id = dto.Id;
        Update(dto);
    }

    public PlaylistsViewModel Owner { get; }

    public Guid Id { get; }

    [ObservableProperty]
    public partial string Name { get; private set; } = "";

    [ObservableProperty]
    public partial int TrackCount { get; private set; }

    public string CountText => TrackCount == 1 ? "1 track" : $"{TrackCount} tracks";

    internal void Update(PlaylistDto dto)
    {
        Name = dto.Name;
        TrackCount = dto.TrackIds.Count;
        OnPropertyChanged(nameof(CountText));
    }
}

public sealed partial class PlaylistTrackViewModel : ObservableObject
{
    public PlaylistTrackViewModel(PlaylistsViewModel owner, Guid trackId, int index)
    {
        Owner = owner;
        TrackId = trackId;
        Index = index;
    }

    public PlaylistsViewModel Owner { get; }

    public Guid TrackId { get; }

    public int Index { get; }

    public int Number => Index + 1;

    [ObservableProperty]
    public partial string Title { get; set; } = "…";

    [ObservableProperty]
    public partial string? Artist { get; set; }

    [ObservableProperty]
    public partial string DurationText { get; set; } = "";
}

/// <summary>Playlists: list, create, rename, delete, edit track order, and enqueue (append or replace).</summary>
public sealed partial class PlaylistsViewModel : ViewModelBase
{
    private readonly ISabhaClient _client;
    private readonly UiServices _ui;
    private readonly TrackInfoCache _tracks;
    private PlaylistDto? _selected;
    private bool _loaded;

    public PlaylistsViewModel(ISabhaClient client, UiServices ui, TrackInfoCache tracks)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
        _tracks = tracks ?? throw new ArgumentNullException(nameof(tracks));
        _client.Events.LibraryChangeReceived += change => _ui.Dispatcher.Post(() => OnLibraryChanged(change));
    }

    public ObservableCollection<PlaylistItemViewModel> Playlists { get; } = [];

    /// <summary>Tracks of <see cref="SelectedPlaylist"/>, in playlist order.</summary>
    public ObservableCollection<PlaylistTrackViewModel> Tracks { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    public partial PlaylistItemViewModel? SelectedPlaylist { get; set; }

    public bool HasSelection => SelectedPlaylist is not null;

    [ObservableProperty]
    public partial PlaylistTrackViewModel? SelectedTrack { get; set; }

    public Task<bool> LoadAsync() => TryAsync(async () =>
    {
        var list = await _client.Playlists.GetPlaylistsAsync().ConfigureAwait(true);
        ApplyList(list);
        _loaded = true;
    }, clearError: false);

    public void Reset()
    {
        _loaded = false;
        Playlists.Clear();
        Tracks.Clear();
        _selected = null;
        SelectedPlaylist = null;
        ErrorMessage = StatusMessage = null;
    }

    /// <summary>Appends tracks to a playlist (used by the library's "Add to playlist").</summary>
    public async Task<bool> AddTracksAsync(Guid playlistId, IReadOnlyList<Guid> trackIds)
    {
        ArgumentNullException.ThrowIfNull(trackIds);
        PlaylistDto? updated = null;
        var ok = await TryAsync(async () =>
        {
            var current = await _client.Playlists.GetPlaylistAsync(playlistId).ConfigureAwait(true);
            updated = await _client.Playlists
                .SetPlaylistTracksAsync(playlistId, [.. current.TrackIds, .. trackIds])
                .ConfigureAwait(true);
        }).ConfigureAwait(true);

        if (ok && updated is not null)
        {
            ApplyUpdated(updated);
            StatusMessage = $"Added to \"{updated.Name}\".";
        }

        return ok;
    }

    [RelayCommand]
    private async Task CreateAsync()
    {
        var name = await _ui.Dialogs.PromptTextAsync("New playlist", "Playlist name:").ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        await TryAsync(async () =>
        {
            var created = await _client.Playlists.CreatePlaylistAsync(name.Trim()).ConfigureAwait(true);
            var row = new PlaylistItemViewModel(this, created);
            Playlists.Add(row);
            SelectedPlaylist = row;
        }).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task RenameAsync(PlaylistItemViewModel? playlist)
    {
        playlist ??= SelectedPlaylist;
        if (playlist is null)
        {
            return;
        }

        var name = await _ui.Dialogs.PromptTextAsync("Rename playlist", "New name:", playlist.Name).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(name) || name.Trim() == playlist.Name)
        {
            return;
        }

        await TryAsync(async () =>
        {
            var updated = await _client.Playlists.RenamePlaylistAsync(playlist.Id, name.Trim()).ConfigureAwait(true);
            ApplyUpdated(updated);
        }).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task DeleteAsync(PlaylistItemViewModel? playlist)
    {
        playlist ??= SelectedPlaylist;
        if (playlist is null)
        {
            return;
        }

        var confirmed = await _ui.Dialogs.ConfirmAsync(
            "Delete playlist",
            $"Delete the playlist \"{playlist.Name}\"? The tracks stay in the library.",
            "Delete").ConfigureAwait(true);
        if (!confirmed)
        {
            return;
        }

        await TryAsync(async () =>
        {
            await _client.Playlists.DeletePlaylistAsync(playlist.Id).ConfigureAwait(true);
            Playlists.Remove(playlist);
            if (SelectedPlaylist == playlist)
            {
                SelectedPlaylist = null;
            }
        }).ConfigureAwait(true);
    }

    [RelayCommand]
    private Task EnqueueAppendAsync(PlaylistItemViewModel? playlist)
    {
        playlist ??= SelectedPlaylist;
        return playlist is null ? Task.CompletedTask : TryAsync(async () =>
        {
            await _client.Playback.EnqueuePlaylistAsync(playlist.Id, replaceQueue: false).ConfigureAwait(true);
            StatusMessage = $"Added \"{playlist.Name}\" to the end of the queue.";
        });
    }

    [RelayCommand]
    private async Task EnqueueReplaceAsync(PlaylistItemViewModel? playlist)
    {
        playlist ??= SelectedPlaylist;
        if (playlist is null)
        {
            return;
        }

        var confirmed = await _ui.Dialogs.ConfirmAsync(
            "Replace queue",
            $"Replace the whole queue with \"{playlist.Name}\"?",
            "Replace").ConfigureAwait(true);
        if (!confirmed)
        {
            return;
        }

        await TryAsync(async () =>
        {
            await _client.Playback.EnqueuePlaylistAsync(playlist.Id, replaceQueue: true).ConfigureAwait(true);
            StatusMessage = $"The queue now contains \"{playlist.Name}\".";
        }).ConfigureAwait(true);
    }

    /// <summary>Moves a track inside the selected playlist (drag-and-drop or up/down buttons) and saves the new order.</summary>
    public Task MoveTrackAsync(int oldIndex, int newIndex)
    {
        if (_selected is not { } playlist || oldIndex < 0 || oldIndex >= playlist.TrackIds.Count)
        {
            return Task.CompletedTask;
        }

        newIndex = Math.Clamp(newIndex, 0, playlist.TrackIds.Count - 1);
        if (newIndex == oldIndex)
        {
            return Task.CompletedTask;
        }

        var order = playlist.TrackIds.ToList();
        var id = order[oldIndex];
        order.RemoveAt(oldIndex);
        order.Insert(newIndex, id);
        return SaveOrderAsync(playlist.Id, order, selectIndex: newIndex);
    }

    [RelayCommand]
    private Task MoveTrackUpAsync(PlaylistTrackViewModel? track) =>
        track is null ? Task.CompletedTask : MoveTrackAsync(track.Index, track.Index - 1);

    [RelayCommand]
    private Task MoveTrackDownAsync(PlaylistTrackViewModel? track) =>
        track is null ? Task.CompletedTask : MoveTrackAsync(track.Index, track.Index + 1);

    [RelayCommand]
    private Task RemoveTrackAsync(PlaylistTrackViewModel? track)
    {
        if (track is null || _selected is not { } playlist || track.Index >= playlist.TrackIds.Count)
        {
            return Task.CompletedTask;
        }

        var order = playlist.TrackIds.ToList();
        order.RemoveAt(track.Index);
        return SaveOrderAsync(playlist.Id, order, selectIndex: null);
    }

    partial void OnSelectedPlaylistChanged(PlaylistItemViewModel? value)
    {
        Tracks.Clear();
        _selected = null;
        if (value is not null)
        {
            Run(() => LoadSelectedAsync(value.Id));
        }
    }

    private Task SaveOrderAsync(Guid playlistId, List<Guid> order, int? selectIndex) => TryAsync(async () =>
    {
        var updated = await _client.Playlists.SetPlaylistTracksAsync(playlistId, order).ConfigureAwait(true);
        ApplyUpdated(updated);
        if (selectIndex is { } i && i < Tracks.Count)
        {
            SelectedTrack = Tracks[i];
        }
    });

    private async Task LoadSelectedAsync(Guid playlistId)
    {
        var dto = await _client.Playlists.GetPlaylistAsync(playlistId).ConfigureAwait(true);
        if (SelectedPlaylist?.Id == playlistId)
        {
            ApplySelected(dto);
        }
    }

    private void ApplyList(IReadOnlyList<PlaylistDto> list)
    {
        var selectedId = SelectedPlaylist?.Id;
        var existing = Playlists.ToDictionary(p => p.Id);
        Playlists.Clear();
        foreach (var dto in list)
        {
            if (existing.TryGetValue(dto.Id, out var row))
            {
                row.Update(dto);
            }
            else
            {
                row = new PlaylistItemViewModel(this, dto);
            }

            Playlists.Add(row);
        }

        var reselect = selectedId is null ? null : Playlists.FirstOrDefault(p => p.Id == selectedId);
        if (reselect is null)
        {
            SelectedPlaylist = null;
        }
        else if (!ReferenceEquals(reselect, SelectedPlaylist))
        {
            SelectedPlaylist = reselect;
        }
        else
        {
            Run(() => LoadSelectedAsync(reselect.Id));
        }
    }

    private void ApplyUpdated(PlaylistDto dto)
    {
        Playlists.FirstOrDefault(p => p.Id == dto.Id)?.Update(dto);
        if (SelectedPlaylist?.Id == dto.Id)
        {
            ApplySelected(dto);
        }
    }

    private void ApplySelected(PlaylistDto dto)
    {
        _selected = dto;
        Tracks.Clear();
        for (var i = 0; i < dto.TrackIds.Count; i++)
        {
            var row = new PlaylistTrackViewModel(this, dto.TrackIds[i], i);
            Tracks.Add(row);
            Run(() => FillTrackAsync(row));
        }
    }

    private async Task FillTrackAsync(PlaylistTrackViewModel row)
    {
        var track = await _tracks.GetAsync(row.TrackId).ConfigureAwait(true);
        row.Title = track?.Title ?? "Unknown track (removed)";
        row.Artist = track?.Artist;
        row.DurationText = track is null ? "" : Formatting.Time(track.DurationSeconds);
    }

    private void OnLibraryChanged(LibraryChangedDto change)
    {
        if (_loaded && change.Kind is LibraryChangeKind.PlaylistsChanged or LibraryChangeKind.TrackRemoved)
        {
            Run(async () => ApplyList(await _client.Playlists.GetPlaylistsAsync().ConfigureAwait(true)));
        }
    }
}
