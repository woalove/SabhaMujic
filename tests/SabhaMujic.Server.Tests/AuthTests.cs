using SabhaMujic.Contracts;
using SabhaMujic.Server.Application;

namespace SabhaMujic.Server.Tests;

public sealed class AuthTests : IAsyncLifetime
{
    private TestServer _server = null!;

    public async Task InitializeAsync() => _server = await TestServer.StartAsync();

    public async Task DisposeAsync() => await _server.DisposeAsync();

    [Fact]
    public async Task Hello_reports_the_contract_and_rejects_other_versions()
    {
        var client = await _server.ConnectAsync();
        Assert.Equal(ContractVersion.Current, client.ServerInfo!.ContractVersion);
        var hello = await client.Auth.HelloAsync(ContractVersion.Current);
        Assert.Equal(ContractVersion.Current, hello.ContractVersion);
        await AssertEx.ThrowsCode(ErrorCode.IncompatibleClient, () => client.Auth.HelloAsync(ContractVersion.Current + 1));
    }

    [Fact]
    public async Task Login_binds_the_session_and_logout_ends_it()
    {
        var client = await _server.ConnectAsync();
        Assert.Empty(await client.Library.GetCategoriesAsync()); // open use
        await AssertEx.ThrowsCode(ErrorCode.Unauthenticated, () => client.Admin.GetUsersAsync());

        var session = await client.Auth.LoginAsync("ADMIN", TestServer.AdminSecret);
        Assert.Equal(TestServer.AdminName, session.UserName);
        Assert.Equal(Role.Admin, session.Role);
        Assert.Equal(_server.Clock.GetUtcNow() + TimeSpan.FromMinutes(15), session.ExpiresAt);
        Assert.Equal(session, client.Session);
        Assert.NotEmpty(await client.Admin.GetUsersAsync());

        await client.Auth.LogoutAsync();
        Assert.Null(client.Session);
        await AssertEx.ThrowsCode(ErrorCode.Unauthenticated, () => client.Admin.GetUsersAsync());
        Assert.Empty(await client.Library.GetCategoriesAsync());

        // The token is dead for every connection.
        var other = await _server.ConnectAsync();
        await AssertEx.ThrowsCode(ErrorCode.Unauthenticated, () => other.Auth.ResumeSessionAsync(session.Token));
    }

    [Fact]
    public async Task Wrong_credentials_and_unknown_users_get_the_same_error()
    {
        var client = await _server.ConnectAsync();
        var wrong = await AssertEx.ThrowsCode(ErrorCode.Unauthenticated, () => client.Auth.LoginAsync(TestServer.AdminName, "nope-nope"));
        var unknown = await AssertEx.ThrowsCode(ErrorCode.Unauthenticated, () => client.Auth.LoginAsync("ghost", "nope-nope"));
        Assert.Equal(wrong.Message, unknown.Message);
        Assert.Null(client.Session);
    }

    [Fact]
    public async Task Login_is_rate_limited_per_user_name()
    {
        var client = await _server.ConnectAsync();
        for (var i = 0; i < 5; i++)
        {
            await AssertEx.ThrowsCode(ErrorCode.Unauthenticated, () => client.Auth.LoginAsync(TestServer.VolunteerName, "wrong-" + i));
        }

        // Even the correct secret is refused while limited; other users are unaffected.
        await AssertEx.ThrowsCode(ErrorCode.RateLimited, () => client.Auth.LoginAsync(TestServer.VolunteerName, TestServer.VolunteerSecret));
        await AssertEx.ThrowsCode(ErrorCode.RateLimited, () => client.Auth.LoginAsync("VOL", TestServer.VolunteerSecret));
        await client.Auth.LoginAsync(TestServer.AdminName, TestServer.AdminSecret);

        _server.Clock.Advance(TimeSpan.FromMinutes(5));
        var session = await client.Auth.LoginAsync(TestServer.VolunteerName, TestServer.VolunteerSecret);
        Assert.Equal(Role.Volunteer, session.Role);

        var audit = await _server.App.AuditLog.QueryRecentAsync(50);
        Assert.Contains(audit, a => a.Action == "login.rate_limited");
        Assert.Equal(5, audit.Count(a => a.Action == "login.failed"));
    }

    [Fact]
    public async Task Login_is_rate_limited_globally()
    {
        var server = await TestServer.StartAsync(options: new ServerOptions { SecretHashIterations = 1_000, LoginMaxFailuresGlobal = 3 });
        await using var _ = server;
        var client = await server.ConnectAsync();
        for (var i = 0; i < 3; i++)
        {
            await AssertEx.ThrowsCode(ErrorCode.Unauthenticated, () => client.Auth.LoginAsync("user" + i, "wrong-pass"));
        }

        await AssertEx.ThrowsCode(ErrorCode.RateLimited, () => client.Auth.LoginAsync(TestServer.AdminName, TestServer.AdminSecret));
    }

    [Fact]
    public async Task Desktop_admin_sessions_slide_and_end_after_15_idle_minutes()
    {
        var client = await _server.ConnectAdminAsync();
        var token = client.Session!.Token;
        var ended = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Events.SessionEndedReceived += reason => ended.TrySetResult(reason);
        _server.Clock.Advance(TimeSpan.FromMinutes(10));
        await client.Admin.GetUsersAsync(); // slides the expiry to now + 15 min
        _server.Clock.Advance(TimeSpan.FromMinutes(10));
        await client.Admin.GetUsersAsync();
        _server.Clock.Advance(TimeSpan.FromMinutes(15) + TimeSpan.FromSeconds(1));
        await AssertEx.ThrowsCode(ErrorCode.Unauthenticated, () => client.Admin.GetUsersAsync());

        Assert.Contains("15 minutes", await ended.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        await AssertEx.Eventually(() => client.Session is null, "client drops the ended session");
        await client.Playback.GetStateAsync(); // back to open use on the same connection
        await AssertEx.ThrowsCode(ErrorCode.Unauthenticated, () => client.Auth.ResumeSessionAsync(token));
    }

    [Fact]
    public async Task An_idle_desktop_admin_is_signed_out_without_making_a_call()
    {
        var client = await _server.ConnectAdminAsync();
        var ended = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Events.SessionEndedReceived += reason => ended.TrySetResult(reason);

        _server.Clock.Advance(TimeSpan.FromMinutes(14));
        Assert.Equal(0, _server.Pipe!.ExpireIdleSessions());
        _server.Clock.Advance(TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(1));
        Assert.Equal(1, _server.Pipe.ExpireIdleSessions());

        Assert.Equal("Signed out after 15 minutes without activity.", await ended.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        await AssertEx.Eventually(() => client.Session is null, "client drops the ended session");
        Assert.Equal(0, _server.Pipe.ExpireIdleSessions());
        await client.Playback.GetQueueAsync();
        await AssertEx.ThrowsCode(ErrorCode.Unauthenticated, () => client.Admin.GetUsersAsync());
        Assert.Equal(1, _server.Pipe.ConnectionCount);
    }

    [Fact]
    public async Task Resume_attaches_a_session_on_another_connection()
    {
        var first = await _server.ConnectVolunteerAsync();
        var second = await _server.ConnectAsync();
        var resumed = await second.Auth.ResumeSessionAsync(first.Session!.Token);
        Assert.Equal(Role.Volunteer, resumed.Role);
        Assert.Equal(TestServer.VolunteerName, resumed.UserName);
        await second.Playback.GetStateAsync();
        await AssertEx.ThrowsCode(ErrorCode.Unauthenticated, () => second.Auth.ResumeSessionAsync("not-a-token"));
        await AssertEx.ThrowsCode(ErrorCode.Unauthenticated, () => second.Auth.ResumeSessionAsync(""));
    }

    [Fact]
    public async Task Deleting_a_user_ends_their_sessions_live()
    {
        var volunteer = await _server.ConnectVolunteerAsync();
        var ended = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        volunteer.Events.SessionEndedReceived += reason => ended.TrySetResult(reason);
        var admin = await _server.ConnectAdminAsync();

        await admin.Admin.DeleteUserAsync(_server.VolunteerId);

        Assert.Contains("removed", await ended.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Null(volunteer.Session);
        await volunteer.Playback.GetStateAsync(); // back to open use
        await admin.Admin.GetUsersAsync();
    }

    [Fact]
    public async Task Changing_another_users_secret_revokes_their_sessions()
    {
        var volunteer = await _server.ConnectVolunteerAsync();
        var admin = await _server.ConnectAdminAsync();
        await admin.Admin.SetUserSecretAsync(_server.VolunteerId, "new-secret");
        await AssertEx.Eventually(() => volunteer.Session is null, "volunteer receives SessionEnded");
        await AssertEx.ThrowsCode(ErrorCode.Unauthenticated, () => volunteer.Auth.LoginAsync(TestServer.VolunteerName, TestServer.VolunteerSecret));
        await volunteer.Auth.LoginAsync(TestServer.VolunteerName, "new-secret");

        // Changing your own secret keeps this session but signs out your other sessions.
        var adminElsewhere = await _server.ConnectAdminAsync();
        await admin.Admin.SetUserSecretAsync(_server.AdminId, "admin-new");
        await admin.Playback.GetStateAsync();
        await AssertEx.Eventually(() => adminElsewhere.Session is null, "the other admin session receives SessionEnded");
        await AssertEx.ThrowsCode(ErrorCode.Unauthenticated, () => adminElsewhere.Admin.GetUsersAsync());
        await AssertEx.ThrowsCode(ErrorCode.InvalidArgument, () => admin.Admin.SetUserSecretAsync(_server.AdminId, "123"));
    }

    [Fact]
    public async Task Admin_cannot_delete_themselves_or_the_last_admin()
    {
        var admin = await _server.ConnectAdminAsync();
        await AssertEx.ThrowsCode(ErrorCode.Conflict, () => admin.Admin.DeleteUserAsync(_server.AdminId));

        // The same admin signed in on a phone cannot delete the only admin user either.
        var phone = _server.App.CreateSession(ClientKind.Remote, "test-remote");
        await phone.RemoteLoginAsync(TestServer.AdminName, TestServer.AdminSecret, rememberDevice: true, "Admin iPad");
        await AssertEx.ThrowsCode(ErrorCode.Conflict, () => phone.DeleteUserAsync(_server.AdminId));
        Assert.Equal(1, await _server.App.Users.CountAdminsAsync());

        // With a second admin, the first can be deleted by the second.
        var second = await admin.Admin.CreateUserAsync("admin2", Role.Admin, "admin2-pass");
        var admin2 = await _server.ConnectAsync("admin2", "admin2-pass");
        await admin2.Admin.DeleteUserAsync(_server.AdminId);
        Assert.Equal([second.Id], (await admin2.Admin.GetUsersAsync()).Where(u => u.Role == Role.Admin).Select(u => u.Id));
    }

    [Fact]
    public async Task Remote_sign_in_with_remember_creates_a_listed_device_with_a_long_sliding_session()
    {
        var remote = _server.App.CreateSession(ClientKind.Remote, "192.168.1.20");
        var signedIn = await remote.RemoteLoginAsync("ADMIN", TestServer.AdminSecret, rememberDevice: true, "Asha's iPhone");
        Assert.Equal((TestServer.AdminName, Role.Admin), (signedIn.UserName, signedIn.Role));
        Assert.Equal(_server.Clock.GetUtcNow().AddDays(180), signedIn.ExpiresAt);
        Assert.True(remote.IsRememberedDevice);
        await remote.GetUsersAsync();

        var admin = await _server.ConnectAdminAsync();
        var device = Assert.Single(await admin.Admin.GetDevicesAsync());
        Assert.Equal(("Asha's iPhone", Role.Admin), (device.Name, device.Role));
        var audit = await _server.App.AuditLog.QueryRecentAsync(10);
        Assert.Contains(audit, a => a.Action == "login.remembered_device" && a.ClientKind == ClientKind.Remote);

        // Sliding: in use, a remembered phone stays signed in far beyond 180 days in total.
        _server.Clock.Advance(TimeSpan.FromDays(150));
        await remote.GetStateAsync();
        _server.Clock.Advance(TimeSpan.FromDays(150));
        var again = _server.App.CreateSession(ClientKind.Remote, "192.168.1.20");
        var resumed = await again.ResumeSessionAsync(signedIn.Token);
        Assert.True(resumed.ExpiresAt > _server.Clock.GetUtcNow().AddDays(29));

        // Revoking the device (desktop "Forget phone") ends it everywhere.
        admin = await _server.ConnectAdminAsync();
        await admin.Admin.RevokeDeviceAsync(device.Id);
        await AssertEx.ThrowsCode(ErrorCode.Unauthenticated, () => remote.GetStateAsync());
        await AssertEx.ThrowsCode(ErrorCode.Unauthenticated, () => again.GetStateAsync());
        await AssertEx.ThrowsCode(ErrorCode.Unauthenticated, () => _server.App.CreateSession(ClientKind.Remote, "x").ResumeSessionAsync(signedIn.Token));
        Assert.Empty(await admin.Admin.GetDevicesAsync());
    }

    [Fact]
    public async Task Remote_sign_in_without_remember_creates_no_device_and_ends_after_12_hours()
    {
        var remote = _server.App.CreateSession(ClientKind.Remote, "192.168.1.21");
        var signedIn = await remote.RemoteLoginAsync(TestServer.AdminName, TestServer.AdminSecret, rememberDevice: false, "ignored");
        Assert.Equal(_server.Clock.GetUtcNow().AddHours(12), signedIn.ExpiresAt);
        Assert.False(remote.IsRememberedDevice);
        Assert.Empty(await _server.App.Devices.ListAsync());

        // Activity does not extend it.
        _server.Clock.Advance(TimeSpan.FromHours(11));
        await remote.GetUsersAsync();
        _server.Clock.Advance(TimeSpan.FromHours(1) + TimeSpan.FromMinutes(1));
        await AssertEx.ThrowsCode(ErrorCode.Unauthenticated, () => remote.GetStateAsync());
    }

    [Fact]
    public async Task Remote_sign_out_forgets_a_remembered_phone()
    {
        var remote = _server.App.CreateSession(ClientKind.Remote, "192.168.1.22");
        var signedIn = await remote.RemoteLoginAsync(TestServer.AdminName, TestServer.AdminSecret, rememberDevice: true, "Kitchen iPad");
        Assert.Single(await _server.App.Devices.ListAsync());

        await remote.LogoutAsync();

        Assert.Empty(await _server.App.Devices.ListAsync());
        await AssertEx.ThrowsCode(ErrorCode.Unauthenticated, () => _server.App.CreateSession(ClientKind.Remote, "x").ResumeSessionAsync(signedIn.Token));
        var admin = await _server.ConnectAdminAsync();
        Assert.Empty(await admin.Admin.GetDevicesAsync());
    }

    [Fact]
    public async Task Volunteer_accounts_cannot_sign_in_on_the_remote()
    {
        var remote = _server.App.CreateSession(ClientKind.Remote, "192.168.1.23");
        var denied = await AssertEx.ThrowsCode(ErrorCode.Forbidden,
            () => remote.RemoteLoginAsync(TestServer.VolunteerName, TestServer.VolunteerSecret, rememberDevice: true, "Vol's phone"));
        Assert.Contains("administrators", denied.Message);
        await AssertEx.ThrowsCode(ErrorCode.Forbidden, () => remote.LoginAsync(TestServer.VolunteerName, TestServer.VolunteerSecret));
        Assert.False(remote.Caller.IsAuthenticated);
        Assert.Empty(await _server.App.Devices.ListAsync());
        var audit = await _server.App.AuditLog.QueryRecentAsync(10);
        Assert.Equal(2, audit.Count(a => a.Action == "login.remote_denied"));
        await AssertEx.ThrowsCode(ErrorCode.Unauthenticated, () => remote.GetStateAsync());

        // A volunteer session created on the desktop cannot be carried over to the remote either.
        var desktop = await _server.ConnectVolunteerAsync();
        await AssertEx.ThrowsCode(ErrorCode.Forbidden, () => remote.ResumeSessionAsync(desktop.Session!.Token));
    }

    [Fact]
    public async Task Remote_sign_in_is_rate_limited()
    {
        var attacker = _server.App.CreateSession(ClientKind.Remote, "10.0.0.66");
        for (var i = 0; i < 5; i++)
        {
            await AssertEx.ThrowsCode(ErrorCode.Unauthenticated, () => attacker.RemoteLoginAsync(TestServer.AdminName, "wrong-pass-" + i, true, "x"));
        }

        await AssertEx.ThrowsCode(ErrorCode.RateLimited, () => attacker.RemoteLoginAsync(TestServer.AdminName, TestServer.AdminSecret, true, "x"));
        Assert.Empty(await _server.App.Devices.ListAsync());
        _server.Clock.Advance(TimeSpan.FromMinutes(5));
        await attacker.RemoteLoginAsync(TestServer.AdminName, TestServer.AdminSecret, true, "x");
    }

    [Fact]
    public async Task Passwords_need_at_least_eight_characters_and_new_accounts_are_administrators()
    {
        var admin = await _server.ConnectAdminAsync();
        var shortPassword = await AssertEx.ThrowsCode(ErrorCode.InvalidArgument, () => admin.Admin.CreateUserAsync("asha", Role.Admin, "1234567"));
        Assert.Contains("password", shortPassword.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PIN", shortPassword.Message, StringComparison.Ordinal);
        await AssertEx.ThrowsCode(ErrorCode.InvalidArgument, () => admin.Admin.SetUserSecretAsync(_server.VolunteerId, "1234567"));
        await AssertEx.ThrowsCode(ErrorCode.InvalidArgument, () => admin.Admin.CreateUserAsync("asha", Role.Volunteer, "12345678"));
        Assert.Null(await _server.App.Users.GetByNameAsync("asha"));

        var created = await admin.Admin.CreateUserAsync("asha", Role.Admin, "12345678");
        Assert.Equal(Role.Admin, created.Role);
        await _server.ConnectAsync("asha", "12345678");
    }

    [Fact]
    public async Task Initial_admin_is_only_created_on_an_empty_user_table()
    {
        Assert.False(await _server.App.Auth.EnsureInitialAdminAsync("other", "other-pass"));
        Assert.Null(await _server.App.Users.GetByNameAsync("other"));
    }

    [Fact]
    public async Task Admin_actions_are_audited_with_the_caller()
    {
        var admin = await _server.ConnectAdminAsync();
        var category = await admin.Admin.CreateCategoryAsync("Bhajan");
        await admin.Admin.RenameCategoryAsync(category.Id, "Bhajans");
        await admin.Admin.CreateUserAsync("asha", Role.Admin, "asha-pass");

        var audit = await _server.App.AuditLog.QueryRecentAsync(10);
        Assert.Equal(["user.create", "category.rename", "category.create", "login"], audit.Take(4).Select(a => a.Action));
        Assert.All(audit.Take(4), a => Assert.StartsWith("admin (", a.Actor));
        Assert.All(audit.Take(4), a => Assert.Equal(ClientKind.Desktop, a.ClientKind));
    }
}
