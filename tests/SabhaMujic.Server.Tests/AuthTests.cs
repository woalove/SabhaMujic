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
        await AssertEx.ThrowsCode(ErrorCode.Unauthenticated, () => client.Library.GetCategoriesAsync());

        var session = await client.Auth.LoginAsync("ADMIN", TestServer.AdminSecret);
        Assert.Equal(TestServer.AdminName, session.UserName);
        Assert.Equal(Role.Admin, session.Role);
        Assert.Equal(_server.Clock.GetUtcNow() + TimeSpan.FromHours(12), session.ExpiresAt);
        Assert.Equal(session, client.Session);
        Assert.Empty(await client.Library.GetCategoriesAsync());

        await client.Auth.LogoutAsync();
        Assert.Null(client.Session);
        await AssertEx.ThrowsCode(ErrorCode.Unauthenticated, () => client.Library.GetCategoriesAsync());

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
    public async Task Desktop_sessions_slide_and_expire()
    {
        var client = await _server.ConnectAdminAsync();
        _server.Clock.Advance(TimeSpan.FromHours(11));
        await client.Library.GetCategoriesAsync(); // slides the expiry to now + 12 h
        _server.Clock.Advance(TimeSpan.FromHours(11));
        await client.Library.GetCategoriesAsync();
        _server.Clock.Advance(TimeSpan.FromHours(12) + TimeSpan.FromMinutes(1));
        await AssertEx.ThrowsCode(ErrorCode.Unauthenticated, () => client.Library.GetCategoriesAsync());
        await AssertEx.ThrowsCode(ErrorCode.Unauthenticated, () => client.Auth.ResumeSessionAsync(client.Session!.Token));
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
        await AssertEx.ThrowsCode(ErrorCode.Unauthenticated, () => volunteer.Playback.GetStateAsync());
        await admin.Playback.GetStateAsync();
    }

    [Fact]
    public async Task Changing_another_users_secret_revokes_their_sessions()
    {
        var volunteer = await _server.ConnectVolunteerAsync();
        var admin = await _server.ConnectAdminAsync();
        await admin.Admin.SetUserSecretAsync(_server.VolunteerId, "new-secret");
        await AssertEx.Eventually(() => volunteer.Session is null, "volunteer receives SessionEnded");
        await AssertEx.ThrowsCode(ErrorCode.Unauthenticated, () => volunteer.Playback.GetStateAsync());
        await AssertEx.ThrowsCode(ErrorCode.Unauthenticated, () => volunteer.Auth.LoginAsync(TestServer.VolunteerName, TestServer.VolunteerSecret));
        await volunteer.Auth.LoginAsync(TestServer.VolunteerName, "new-secret");

        // Changing your own secret keeps your session.
        await admin.Admin.SetUserSecretAsync(_server.AdminId, "admin-new");
        await admin.Playback.GetStateAsync();
        await AssertEx.ThrowsCode(ErrorCode.InvalidArgument, () => admin.Admin.SetUserSecretAsync(_server.AdminId, "123"));
    }

    [Fact]
    public async Task Admin_cannot_delete_themselves_or_the_last_admin()
    {
        var admin = await _server.ConnectAdminAsync();
        await AssertEx.ThrowsCode(ErrorCode.Conflict, () => admin.Admin.DeleteUserAsync(_server.AdminId));

        // A paired admin device has no user account, so it can try to delete the only admin user.
        var code = await admin.Admin.CreatePairingCodeAsync(Role.Admin);
        var device = _server.App.CreateSession(ClientKind.Remote, "test-remote");
        await device.PairAsync(code.Code, "Admin iPad");
        await AssertEx.ThrowsCode(ErrorCode.Conflict, () => device.DeleteUserAsync(_server.AdminId));
        Assert.Equal(1, await _server.App.Users.CountAdminsAsync());

        // With a second admin, the first can be deleted by the second.
        var second = await admin.Admin.CreateUserAsync("admin2", Role.Admin, "admin2-pass");
        var admin2 = await _server.ConnectAsync("admin2", "admin2-pass");
        await admin2.Admin.DeleteUserAsync(_server.AdminId);
        Assert.Equal([second.Id], (await admin2.Admin.GetUsersAsync()).Where(u => u.Role == Role.Admin).Select(u => u.Id));
    }

    [Fact]
    public async Task Pairing_code_is_single_use_and_creates_a_long_lived_device_session()
    {
        var admin = await _server.ConnectAdminAsync();
        var code = await admin.Admin.CreatePairingCodeAsync(Role.Volunteer);
        Assert.Equal($"https://sabha.local:8443/pair#{code.Code}", code.PairingUrl);
        Assert.Equal("http://sabha.local:8080/", code.BootstrapUrl);
        Assert.Equal(_server.Clock.GetUtcNow().AddMinutes(5), code.ExpiresAt);

        var remote = _server.App.CreateSession(ClientKind.Remote, "192.168.1.20");
        var paired = await remote.PairAsync(code.Code, "Asha's iPhone");
        Assert.Equal(Role.Volunteer, paired.Session.Role);
        Assert.Equal("Asha's iPhone", paired.Device.Name);
        Assert.True(paired.Session.ExpiresAt > _server.Clock.GetUtcNow().AddDays(3000));
        await remote.GetStateAsync();
        await AssertEx.ThrowsCode(ErrorCode.Forbidden, () => remote.GetUsersAsync());

        var again = _server.App.CreateSession(ClientKind.Remote, "192.168.1.21");
        await AssertEx.ThrowsCode(ErrorCode.Unauthenticated, () => again.PairAsync(code.Code, "Other"));
        Assert.Single(await admin.Admin.GetDevicesAsync());

        // The device token also works on a pipe connection (e.g. for tooling) until it is revoked.
        var client = await _server.ConnectAsync();
        await client.Auth.ResumeSessionAsync(paired.Session.Token);
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Events.SessionEndedReceived += _ => ended.TrySetResult();
        await admin.Admin.RevokeDeviceAsync(paired.Device.Id);
        await ended.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await AssertEx.ThrowsCode(ErrorCode.Unauthenticated, () => client.Playback.GetStateAsync());
        await AssertEx.ThrowsCode(ErrorCode.Unauthenticated, () => remote.GetStateAsync());
        Assert.Empty(await admin.Admin.GetDevicesAsync());
    }

    [Fact]
    public async Task Pairing_is_rate_limited()
    {
        var admin = await _server.ConnectAdminAsync();
        var code = await admin.Admin.CreatePairingCodeAsync(Role.Volunteer);
        var wrong = code.Code == "000000" ? "000001" : "000000";
        var attacker = _server.App.CreateSession(ClientKind.Remote, "10.0.0.66");
        for (var i = 0; i < 5; i++)
        {
            await AssertEx.ThrowsCode(ErrorCode.Unauthenticated, () => attacker.PairAsync(wrong, "x"));
        }

        await AssertEx.ThrowsCode(ErrorCode.RateLimited, () => attacker.PairAsync(code.Code, "x"));

        // Another caller still can (until the global cap is reached).
        var honest = _server.App.CreateSession(ClientKind.Remote, "10.0.0.7");
        await honest.PairAsync(code.Code, "Phone");

        var expired = await admin.Admin.CreatePairingCodeAsync(Role.Volunteer);
        _server.Clock.Advance(TimeSpan.FromMinutes(6));
        await AssertEx.ThrowsCode(ErrorCode.Unauthenticated, () => _server.App.CreateSession(ClientKind.Remote, "10.0.0.8").PairAsync(expired.Code, "y"));
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
        await admin.Admin.CreateUserAsync("asha", Role.Volunteer, "asha-pass");

        var audit = await _server.App.AuditLog.QueryRecentAsync(10);
        Assert.Equal(["user.create", "category.rename", "category.create", "login"], audit.Take(4).Select(a => a.Action));
        Assert.All(audit.Take(4), a => Assert.StartsWith("admin (", a.Actor));
        Assert.All(audit.Take(4), a => Assert.Equal(ClientKind.Desktop, a.ClientKind));
    }
}
