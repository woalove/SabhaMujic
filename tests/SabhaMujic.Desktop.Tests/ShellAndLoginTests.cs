using SabhaMujic.Client;
using SabhaMujic.Contracts;
using SabhaMujic.Desktop.Tests.Fakes;
using SabhaMujic.Desktop.ViewModels;
using static SabhaMujic.Desktop.Tests.TestHelpers;

namespace SabhaMujic.Desktop.Tests;

public class ShellAndLoginTests
{
    [Fact]
    public async Task Login_success_signs_in_and_loads_screens()
    {
        var (shell, client, _) = ConnectedShell();
        client.Auth.OnLogin = (user, secret) => FakeSabhaClient.MakeSession(Role.Volunteer, user);

        shell.Login.UserName = " sevak ";
        shell.Login.Secret = "1111";
        Assert.True(shell.Login.LoginCommand.CanExecute(null));
        await shell.Login.LoginCommand.ExecuteAsync(null);

        var call = Assert.Single(client.CallsTo("LoginAsync"));
        Assert.Equal(["sevak", "1111"], call.Args);
        Assert.True(shell.IsSignedIn);
        Assert.Equal("sevak (Volunteer)", shell.UserDisplayText);
        Assert.Equal("", shell.Login.Secret); // never kept around
        Assert.Null(shell.Login.ErrorMessage);
        Assert.Contains(client.Calls, c => c.Method == "GetStateAsync");
        Assert.Contains(client.Calls, c => c.Method == "GetQueueAsync");
        Assert.Contains(client.Calls, c => c.Method == "GetCategoriesAsync");
    }

    [Theory]
    [InlineData(ErrorCode.Unauthenticated, "Incorrect user name or PIN/password.")]
    [InlineData(ErrorCode.RateLimited, "Too many sign-in attempts. Please wait a minute and try again.")]
    public async Task Login_failure_shows_message(ErrorCode code, string expected)
    {
        var (shell, client, _) = ConnectedShell();
        client.Auth.OnLogin = (_, _) => throw new SabhaMujicException(code, "server text");
        var cleared = false;
        shell.Login.SecretCleared += () => cleared = true;

        shell.Login.UserName = "x";
        shell.Login.Secret = "bad";
        await shell.Login.LoginCommand.ExecuteAsync(null);

        Assert.False(shell.IsSignedIn);
        Assert.Equal(expected, shell.Login.ErrorMessage);
        Assert.Equal("", shell.Login.Secret);
        Assert.True(cleared);
    }

    [Fact]
    public void Login_is_disabled_until_connected_and_filled_in()
    {
        var client = new FakeSabhaClient();
        var shell = new ShellViewModel(client, new TestUi().Services);
        shell.Start();
        shell.Login.UserName = "a";
        shell.Login.Secret = "b";
        Assert.False(shell.Login.LoginCommand.CanExecute(null));

        client.SetConnectionState(ConnectionState.Connected);
        Assert.True(shell.Login.LoginCommand.CanExecute(null));

        shell.Login.Secret = "";
        Assert.False(shell.Login.LoginCommand.CanExecute(null));
    }

    [Theory]
    [InlineData(Role.Admin, true)]
    [InlineData(Role.Volunteer, false)]
    public void Admin_entry_is_visible_only_to_admins(Role role, bool visible)
    {
        var (shell, _, _) = ConnectedShell(role);

        Assert.Equal(visible, shell.IsAdminVisible);
        shell.NavigateCommand.Execute(NavPage.Admin);
        Assert.Equal(visible ? NavPage.Admin : NavPage.Library, shell.CurrentPage);
        Assert.Same(visible ? shell.Admin : shell.Library, shell.CurrentContent);
    }

    [Fact]
    public void Losing_admin_role_leaves_the_admin_page()
    {
        var (shell, client, _) = ConnectedShell(Role.Admin);
        shell.NavigateCommand.Execute(NavPage.Admin);

        client.SetSession(FakeSabhaClient.MakeSession(Role.Volunteer, "other"));

        Assert.False(shell.IsAdminVisible);
        Assert.Equal(NavPage.Library, shell.CurrentPage);
    }

    [Fact]
    public void Navigation_switches_content()
    {
        var (shell, _, _) = ConnectedShell(Role.Volunteer);

        shell.NavigateCommand.Execute(NavPage.Playlists);
        Assert.Same(shell.Playlists, shell.CurrentContent);
        shell.NavigateCommand.Execute(NavPage.Queue);
        Assert.Same(shell.Queue, shell.CurrentContent);
    }

    [Fact]
    public void Connection_banner_reflects_each_state()
    {
        var client = new FakeSabhaClient();
        var shell = new ShellViewModel(client, new TestUi().Services);
        shell.Start();

        Assert.True(shell.IsConnectionBannerVisible);
        Assert.False(shell.IsConnectionProblem);
        Assert.Equal("Connecting to SabhaMujic service…", shell.ConnectionBannerText);

        client.SetConnectionState(ConnectionState.Connected);
        Assert.False(shell.IsConnectionBannerVisible);

        client.SetConnectionState(ConnectionState.Reconnecting);
        Assert.True(shell.IsConnectionBannerVisible);
        Assert.True(shell.IsConnectionProblem);
        Assert.StartsWith("Connection to the SabhaMujic service was lost. Reconnecting", shell.ConnectionBannerText);

        client.SetConnectionState(ConnectionState.Disconnected);
        Assert.True(shell.IsConnectionProblem);
        Assert.StartsWith("Not connected", shell.ConnectionBannerText);
    }

    [Fact]
    public void Reconnect_resynchronises_state_and_ignores_old_revision_counters()
    {
        var (shell, client, _) = ConnectedShell(Role.Volunteer);
        shell.NowPlaying.ApplyState(State(500));
        client.Calls.Clear();

        client.SetConnectionState(ConnectionState.Reconnecting);
        Assert.Empty(client.Calls);

        client.Playback.State = State(2, PlaybackStatus.Paused); // the service restarted: revisions start over
        client.SetConnectionState(ConnectionState.Connected);

        Assert.Contains(client.Calls, c => c.Method == "GetStateAsync");
        Assert.Contains(client.Calls, c => c.Method == "GetQueueAsync");
        Assert.Equal(PlaybackStatus.Paused, shell.NowPlaying.Status);
    }

    [Fact]
    public void Nothing_is_loaded_before_sign_in()
    {
        var (_, client, _) = ConnectedShell();
        client.Events.LibraryChanged(new LibraryChangedDto(LibraryChangeKind.TrackAdded, Guid.NewGuid()));
        client.Events.LibraryChanged(new LibraryChangedDto(LibraryChangeKind.PlaylistsChanged, null));

        // Only the anonymous first-run check is allowed before sign-in.
        Assert.All(client.Calls, c => Assert.Equal("IsSetupRequiredAsync", c.Method));
    }

    [Fact]
    public void Session_ended_returns_to_login_with_notice()
    {
        var (shell, client, _) = ConnectedShell(Role.Admin);
        shell.NavigateCommand.Execute(NavPage.Admin);

        client.Events.SessionEnded("device revoked");

        Assert.False(shell.IsSignedIn);
        Assert.Equal(NavPage.Library, shell.CurrentPage);
        Assert.Contains("device revoked", shell.Login.Notice);
    }

    [Fact]
    public async Task Logout_calls_service_and_clears_session_even_if_it_fails()
    {
        var (shell, client, _) = ConnectedShell(Role.Volunteer);

        await shell.LogoutCommand.ExecuteAsync(null);

        Assert.Single(client.CallsTo("LogoutAsync"));
        Assert.False(shell.IsSignedIn);
    }

    [Fact]
    public void Terminal_connect_failure_is_shown_in_the_banner()
    {
        var client = new FakeSabhaClient
        {
            OnConnect = _ => Task.FromException(new SabhaMujicException(ErrorCode.IncompatibleClient, "v2")),
        };
        var shell = new ShellViewModel(client, new TestUi().Services);

        shell.Start();

        Assert.True(shell.IsConnectionProblem);
        Assert.Contains("does not match", shell.ConnectionBannerText);
    }

    [Fact]
    public void Queue_panel_toggles()
    {
        var (shell, _, _) = ConnectedShell(Role.Volunteer);
        Assert.True(shell.IsQueuePanelOpen);
        shell.ToggleQueuePanelCommand.Execute(null);
        Assert.False(shell.IsQueuePanelOpen);
    }
}
