using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SabhaMujic.Client;
using SabhaMujic.Contracts;
using SabhaMujic.Desktop.ViewModels.Admin;
using SabhaMujic.Desktop.ViewModels.Infrastructure;

namespace SabhaMujic.Desktop.ViewModels;

public enum NavPage
{
    Library,
    Playlists,
    Queue,
    Admin,
}

/// <summary>
/// Root view model: connection banner, session (login/logout), navigation, and the child screens.
/// Owns the order of loading: nothing is fetched until the client is connected and signed in, and everything
/// is re-fetched after a reconnect (events may have been missed while disconnected).
/// </summary>
public sealed partial class ShellViewModel : ViewModelBase, IAsyncDisposable
{
    private readonly ISabhaClient _client;
    private readonly UiServices _ui;
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _connectTask;

    public ShellViewModel(ISabhaClient client, UiServices ui)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));

        Tracks = new TrackInfoCache(client);
        Login = new LoginViewModel(client);
        NowPlaying = new NowPlayingViewModel(client, ui, Tracks);
        Queue = new QueueViewModel(client, ui, Tracks);
        Playlists = new PlaylistsViewModel(client, ui, Tracks);
        Library = new LibraryViewModel(client, ui, Tracks, Playlists);
        Admin = new AdminViewModel(client, ui);

        Login.LoggedIn += session => ApplySession(session);
        _client.ConnectionStateChanged += (_, state) => _ui.Dispatcher.Post(() => OnClientConnectionStateChanged(state));
        _client.SessionChanged += (_, session) => _ui.Dispatcher.Post(() => ApplySession(session));
        _client.Events.SessionEndedReceived += reason => _ui.Dispatcher.Post(() => OnSessionEnded(reason));
    }

    public TrackInfoCache Tracks { get; }

    public LoginViewModel Login { get; }

    public NowPlayingViewModel NowPlaying { get; }

    public QueueViewModel Queue { get; }

    public PlaylistsViewModel Playlists { get; }

    public LibraryViewModel Library { get; }

    public AdminViewModel Admin { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConnected), nameof(IsConnectionBannerVisible), nameof(ConnectionBannerText),
        nameof(IsConnectionProblem))]
    public partial ConnectionState ConnectionState { get; private set; }

    public bool IsConnected => ConnectionState == ConnectionState.Connected;

    public bool IsConnectionBannerVisible => !IsConnected || ConnectionError is not null;

    /// <summary>True when the banner reports a problem (lost or failed), false while simply connecting.</summary>
    public bool IsConnectionProblem => ConnectionError is not null ||
        ConnectionState is ConnectionState.Reconnecting or ConnectionState.Disconnected;

    /// <summary>A terminal connection failure (e.g. incompatible service version).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConnectionBannerVisible), nameof(ConnectionBannerText), nameof(IsConnectionProblem))]
    public partial string? ConnectionError { get; private set; }

    public string ConnectionBannerText => ConnectionError ?? ConnectionState switch
    {
        ConnectionState.Connecting => "Connecting to SabhaMujic service…",
        ConnectionState.Reconnecting =>
            "Connection to the SabhaMujic service was lost. Reconnecting… Playback continues on the service.",
        ConnectionState.Disconnected => "Not connected to the SabhaMujic service. Check that the service is running.",
        _ => "",
    };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSignedIn), nameof(IsAdmin), nameof(IsAdminVisible), nameof(UserDisplayText))]
    public partial SessionDto? Session { get; private set; }

    public bool IsSignedIn => Session is not null;

    public bool IsAdmin => Session?.Role == Role.Admin;

    /// <summary>The Admin navigation entry is shown only to admins.</summary>
    public bool IsAdminVisible => IsAdmin;

    public string UserDisplayText => Session is null ? "" : $"{Session.UserName} ({Session.Role})";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentContent))]
    public partial NavPage CurrentPage { get; private set; }

    public ViewModelBase CurrentContent => CurrentPage switch
    {
        NavPage.Playlists => Playlists,
        NavPage.Queue => Queue,
        NavPage.Admin => Admin,
        _ => Library,
    };

    /// <summary>Right-hand queue panel; persisted per user by the view.</summary>
    [ObservableProperty]
    public partial bool IsQueuePanelOpen { get; set; } = true;

    /// <summary>Starts connecting (the client keeps reconnecting by itself). Returns immediately.</summary>
    public void Start()
    {
        ConnectionState = _client.State;
        Login.IsConnected = IsConnected;
        if (_client.Session is { } existing)
        {
            ApplySession(existing);
        }

        _connectTask ??= ConnectAsync();
    }

    [RelayCommand]
    private void Navigate(NavPage page)
    {
        if (page == NavPage.Admin && !IsAdmin)
        {
            return;
        }

        CurrentPage = page;
        if (page == NavPage.Admin && IsConnected)
        {
            _ = Admin.LoadAsync();
        }
    }

    [RelayCommand]
    private void ToggleQueuePanel() => IsQueuePanelOpen = !IsQueuePanelOpen;

    [RelayCommand]
    private async Task LogoutAsync()
    {
        try
        {
            await _client.Auth.LogoutAsync().ConfigureAwait(true);
        }
        catch (Exception)
        {
            // Signing out locally must always work, even if the service is unreachable.
        }

        ApplySession(null);
    }

    private async Task ConnectAsync()
    {
        try
        {
            ConnectionError = null;
            await _client.ConnectAsync(_lifetime.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            ConnectionError = ErrorMessages.For(ex);
        }
    }

    private void OnClientConnectionStateChanged(ConnectionState state)
    {
        var previous = ConnectionState;
        ConnectionState = state;
        Login.IsConnected = state == ConnectionState.Connected;
        if (state == ConnectionState.Connected)
        {
            ConnectionError = null;
            if (previous != ConnectionState.Connected && Session is not null)
            {
                _ = LoadAllAsync(); // re-sync after (re)connect
            }
        }
    }

    private void ApplySession(SessionDto? session)
    {
        var previous = Session;
        if (previous?.Token == session?.Token && previous?.Role == session?.Role)
        {
            return;
        }

        Session = session;
        if (session is null)
        {
            Library.Reset();
            Queue.Reset();
            Playlists.Reset();
            NowPlaying.Reset();
            Admin.Reset();
            Tracks.Clear();
            CurrentPage = NavPage.Library;
            return;
        }

        if (CurrentPage == NavPage.Admin && !IsAdmin)
        {
            CurrentPage = NavPage.Library;
        }

        if (previous?.Token != session.Token && IsConnected)
        {
            _ = LoadAllAsync();
        }
    }

    private void OnSessionEnded(string reason)
    {
        Login.Notice = string.IsNullOrWhiteSpace(reason)
            ? "Your session has ended. Please sign in again."
            : $"Your session has ended ({reason}). Please sign in again.";
        ApplySession(null);
    }

    private async Task LoadAllAsync()
    {
        await NowPlaying.RefreshAsync().ConfigureAwait(true);
        await Queue.RefreshAsync().ConfigureAwait(true);
        await Playlists.LoadAsync().ConfigureAwait(true);
        await Library.LoadAsync().ConfigureAwait(true);
        if (CurrentPage == NavPage.Admin && IsAdmin)
        {
            await Admin.LoadAsync().ConfigureAwait(true);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);
        if (_connectTask is not null)
        {
            await _connectTask.ConfigureAwait(false);
        }

        _lifetime.Dispose();
    }
}
