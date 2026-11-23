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

    /// <param name="theme">The persisted colour theme; null keeps a dark theme in memory only.</param>
    public ShellViewModel(ISabhaClient client, UiServices ui, ThemeViewModel? theme = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
        Theme = theme ?? new ThemeViewModel();

        Tracks = new TrackInfoCache(client);
        Login = new LoginViewModel(client);
        Setup = new SetupViewModel(client, ui.Restarter);
        Recovery = new RecoveryViewModel(client, ui.Restarter);
        NowPlaying = new NowPlayingViewModel(client, ui, Tracks);
        Queue = new QueueViewModel(client, ui, Tracks);
        Playlists = new PlaylistsViewModel(client, ui, Tracks);
        Library = new LibraryViewModel(client, ui, Tracks, Playlists);
        Admin = new AdminViewModel(client, ui);
        NowPlaying.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(NowPlayingViewModel.CurrentTrackId))
            {
                Library.SetCurrentTrack(NowPlaying.CurrentTrackId);
            }
        };

        Login.LoggedIn += session =>
        {
            IsLoginOpen = false;
            ApplySession(session);
        };
        Login.Closed += () => IsLoginOpen = false;
        Setup.SetupCompleted += session =>
        {
            IsSetupRequired = false;
            ApplySession(session);
        };
        Setup.SetupNoLongerRequired += () => IsSetupRequired = false;
        Login.RecoveryRequested += OpenRecovery;
        Recovery.Closed += () => IsRecoveryOpen = false;
        Recovery.RecoveryCompleted += session =>
        {
            IsRecoveryOpen = false;
            ApplySession(session);
        };
        _client.ConnectionStateChanged += (_, state) => _ui.Dispatcher.Post(() => OnClientConnectionStateChanged(state));
        _client.SessionChanged += (_, session) => _ui.Dispatcher.Post(() => ApplySession(session));
        _client.Events.SessionEndedReceived += reason => _ui.Dispatcher.Post(() => OnSessionEnded(reason));
    }

    public TrackInfoCache Tracks { get; }

    /// <summary>Dark/light theme (header sun/moon toggle).</summary>
    public ThemeViewModel Theme { get; }

    public LoginViewModel Login { get; }

    /// <summary>First-run "create administrator" form, shown instead of <see cref="Login"/> while no accounts exist.</summary>
    public SetupViewModel Setup { get; }

    /// <summary>"Forgot admin password?" screen, shown instead of <see cref="Login"/> while <see cref="IsRecoveryOpen"/>.</summary>
    public RecoveryViewModel Recovery { get; }

    /// <summary>True while the service reports a fresh install (no accounts). Checked after each connect while signed out.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoginVisible), nameof(IsSetupVisible), nameof(IsRecoveryVisible), nameof(IsMainVisible))]
    public partial bool IsSetupRequired { get; private set; }

    /// <summary>True after "Forgot admin password?" (or <see cref="RecoveryViewModel.StartupArgument"/>) until the user goes back or signs in.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoginVisible), nameof(IsRecoveryVisible), nameof(IsMainVisible))]
    public partial bool IsRecoveryOpen { get; private set; }

    /// <summary>True after "Admin sign-in" until the administrator signs in or goes back.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoginVisible), nameof(IsMainVisible))]
    public partial bool IsLoginOpen { get; private set; }

    /// <summary>The administrator sign-in form (open use needs no sign-in, so only on request).</summary>
    public bool IsLoginVisible => IsLoginOpen && !IsSignedIn && !IsSetupRequired && !IsRecoveryOpen;

    public bool IsSetupVisible => !IsSignedIn && IsSetupRequired;

    /// <summary>First-run setup wins over recovery: with no accounts there is nothing to recover.</summary>
    public bool IsRecoveryVisible => !IsSignedIn && !IsSetupRequired && IsRecoveryOpen;

    /// <summary>
    /// The player (library, playlists, queue, now playing). Open use: shown without signing in, whenever no setup,
    /// sign-in or recovery form is showing.
    /// </summary>
    public bool IsMainVisible => !IsLoginVisible && !IsSetupVisible && !IsRecoveryVisible;

    /// <summary>Shown while signed out: the entry to the administrator sign-in.</summary>
    public bool IsAdminSignInAvailable => !IsSignedIn;

    public NowPlayingViewModel NowPlaying { get; }

    public QueueViewModel Queue { get; }

    public PlaylistsViewModel Playlists { get; }

    public LibraryViewModel Library { get; }

    public AdminViewModel Admin { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConnected), nameof(IsConnectionBannerVisible), nameof(ConnectionBannerText),
        nameof(IsConnectionProblem), nameof(ConnectionStatusText))]
    public partial ConnectionState ConnectionState { get; private set; }

    /// <summary>Short label for the header status pill (dot colour comes from <see cref="ConnectionState"/>).</summary>
    public string ConnectionStatusText => ConnectionError is not null
        ? "Not connected"
        : ConnectionState switch
        {
            ConnectionState.Connected => "Live",
            ConnectionState.Connecting => "Connecting…",
            ConnectionState.Reconnecting => "Reconnecting…",
            _ => "Disconnected",
        };

    public bool IsConnected => ConnectionState == ConnectionState.Connected;

    public bool IsConnectionBannerVisible => !IsConnected || ConnectionError is not null;

    /// <summary>True when the banner reports a problem (lost or failed), false while simply connecting.</summary>
    public bool IsConnectionProblem => ConnectionError is not null ||
        ConnectionState is ConnectionState.Reconnecting or ConnectionState.Disconnected;

    /// <summary>A terminal connection failure (e.g. incompatible service version).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConnectionBannerVisible), nameof(ConnectionBannerText), nameof(IsConnectionProblem),
        nameof(ConnectionStatusText))]
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
    [NotifyPropertyChangedFor(nameof(IsSignedIn), nameof(IsAdmin), nameof(IsAdminVisible), nameof(UserDisplayText), nameof(IsLoginVisible),
        nameof(IsSetupVisible), nameof(IsRecoveryVisible), nameof(IsMainVisible), nameof(IsAdminSignInAvailable),
        nameof(AccountName), nameof(AccountInitial))]
    public partial SessionDto? Session { get; private set; }

    /// <summary>Account chip label: "Admin sign-in" when signed out, else the administrator's name.</summary>
    public string AccountName => Session?.UserName ?? "Admin sign-in";

    /// <summary>Initial in the account chip's avatar (empty when signed out: the chip shows a key icon).</summary>
    public string AccountInitial =>
        Session?.UserName is { Length: > 0 } name ? char.ToUpper(name.TrimStart()[0], System.Globalization.CultureInfo.CurrentCulture).ToString() : "";

    partial void OnSessionChanged(SessionDto? value) => Library.CanManageCategories = value?.Role == Role.Admin;

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
        Setup.IsConnected = IsConnected;
        Recovery.IsConnected = IsConnected;
        if (_client.Session is { } existing)
        {
            ApplySession(existing);
        }
        else if (IsConnected)
        {
            _ = CheckSetupAsync();
            _ = LoadAllAsync(); // open use: the player works without signing in
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

    /// <summary>"Admin sign-in": shows the sign-in form (user name and password).</summary>
    [RelayCommand]
    private void OpenAdminSignIn()
    {
        if (IsSignedIn)
        {
            return;
        }

        Login.ErrorMessage = null;
        IsLoginOpen = true;
    }

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
        Setup.IsConnected = state == ConnectionState.Connected;
        Recovery.IsConnected = state == ConnectionState.Connected;
        if (state == ConnectionState.Connected)
        {
            ConnectionError = null;
            if (previous != ConnectionState.Connected && Session is null)
            {
                _ = CheckSetupAsync();
            }

            if (previous != ConnectionState.Connected)
            {
                _ = LoadAllAsync(); // re-sync after (re)connect; open use needs no session
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
            // Back to open use: the player keeps working; only administration closes.
            Admin.Reset();
            if (CurrentPage == NavPage.Admin)
            {
                CurrentPage = NavPage.Library;
            }

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

    /// <summary>Shows the "Forgot admin password?" screen (from the sign-in link, or at startup with <c>--recover-admin</c>).</summary>
    public void OpenRecovery()
    {
        Recovery.Clear();
        IsRecoveryOpen = true;
    }

    private async Task CheckSetupAsync() => IsSetupRequired = await Setup.IsSetupRequiredAsync().ConfigureAwait(true);

    /// <summary>The administrator session ended (idle timeout, revoked): back to open use, with a note.</summary>
    private void OnSessionEnded(string reason)
    {
        var note = string.IsNullOrWhiteSpace(reason)
            ? "The administrator session has ended."
            : $"The administrator session has ended ({reason}).";
        Login.Notice = note + " Sign in again to continue administering.";
        StatusMessage = note + " SabhaMujic stays available for playing music.";
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
