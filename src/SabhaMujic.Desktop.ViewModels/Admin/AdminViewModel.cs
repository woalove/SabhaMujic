using CommunityToolkit.Mvvm.ComponentModel;
using SabhaMujic.Client;
using SabhaMujic.Contracts;
using SabhaMujic.Desktop.ViewModels.Infrastructure;

namespace SabhaMujic.Desktop.ViewModels.Admin;

public enum AdminSection
{
    Import,
    Categories,
    Tracks,
    Users,
    Devices,
}

/// <summary>Admin area (only reachable with <see cref="Role.Admin"/>): import, categories, tracks, users, devices.</summary>
public sealed partial class AdminViewModel : ViewModelBase
{
    private readonly ISabhaClient _client;
    private readonly UiServices _ui;
    private bool _loaded;

    public AdminViewModel(ISabhaClient client, UiServices ui)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
        Import = new ImportViewModel(client, ui);
        Categories = new CategoriesAdminViewModel(client, ui);
        Tracks = new TracksAdminViewModel(client, ui);
        Users = new UsersAdminViewModel(client, ui);
        Devices = new DevicesAdminViewModel(client, ui);

        Categories.CategoriesLoaded += list =>
        {
            Import.SetCategories(list);
            Tracks.SetCategories(list);
        };
        _client.Events.LibraryChangeReceived += change => _ui.Dispatcher.Post(() => OnLibraryChanged(change));
    }

    public ImportViewModel Import { get; }

    public CategoriesAdminViewModel Categories { get; }

    public TracksAdminViewModel Tracks { get; }

    public UsersAdminViewModel Users { get; }

    public DevicesAdminViewModel Devices { get; }

    [ObservableProperty]
    public partial AdminSection SelectedSection { get; set; }

    /// <summary>Loads every section (called when the admin area is opened).</summary>
    public async Task LoadAsync()
    {
        await Categories.LoadAsync().ConfigureAwait(true);
        await Tracks.LoadAsync().ConfigureAwait(true);
        await Users.LoadAsync().ConfigureAwait(true);
        await Devices.LoadAsync().ConfigureAwait(true);
        _loaded = true;
    }

    public void Reset()
    {
        _loaded = false;
        Devices.Reset();
    }

    private void OnLibraryChanged(LibraryChangedDto change)
    {
        if (!_loaded)
        {
            return;
        }

        switch (change.Kind)
        {
            case LibraryChangeKind.CategoriesChanged:
                _ = Categories.LoadAsync();
                break;
            case LibraryChangeKind.TrackAdded:
                _ = Tracks.LoadAsync();
                break;
            case LibraryChangeKind.TrackUpdated or LibraryChangeKind.TrackRemoved or LibraryChangeKind.AnalysisUpdated
                when change.EntityId is { } id:
                Tracks.OnTrackChanged(id);
                break;
        }
    }
}
