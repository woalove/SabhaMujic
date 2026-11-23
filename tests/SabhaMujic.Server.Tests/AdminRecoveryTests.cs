using SabhaMujic.Contracts;

namespace SabhaMujic.Server.Tests;

/// <summary>Admin PIN recovery (<see cref="IAuthApi.ResetAdministratorAsync"/>) over the real pipe.</summary>
public sealed class AdminRecoveryTests
{
    private static readonly FixedCallerIdentity Elevated = new("PC\\owner", elevated: true);
    private static readonly FixedCallerIdentity NotElevated = new("PC\\volunteer", elevated: false);

    /// <summary>The denial itself is audited ("recovery.denied"); everything else must be unchanged.</summary>
    private static string StripAudit(string snapshot) =>
        string.Join('\n', snapshot.Split('\n').Where(line => !line.StartsWith("audit_log:", StringComparison.Ordinal)));

    [Fact]
    public async Task Non_elevated_desktop_callers_are_forbidden_without_side_effects()
    {
        await using var server = await TestServer.StartAsync(identity: NotElevated);
        var admin = await server.ConnectAdminAsync();
        var client = await server.ConnectAsync();
        var before = await server.SnapshotAsync();

        await AssertEx.ThrowsCode(ErrorCode.Forbidden, () => client.Auth.ResetAdministratorAsync(TestServer.AdminName, "new-pin-1"));
        await AssertEx.ThrowsCode(ErrorCode.Forbidden, () => client.Auth.ResetAdministratorAsync("brand-new-admin", "new-pin-1"));

        await Task.Delay(20);
        Assert.Equal(StripAudit(before), StripAudit(await server.SnapshotAsync()));
        Assert.Null(client.Session);
        await admin.Playback.GetStateAsync(); // the admin's session survives
        await server.ConnectAsync(TestServer.AdminName, TestServer.AdminSecret); // and the old password still works
        Assert.Contains(await server.App.AuditLog.QueryRecentAsync(), a => a.Action == "recovery.denied");
    }

    [Fact]
    public async Task Elevated_administrator_resets_the_password_signs_out_old_sessions_and_signs_in()
    {
        await using var server = await TestServer.StartAsync(identity: Elevated);
        var second = await server.App.Users.CreateAsync("second-admin", Role.Admin, server.App.Auth.HashSecret("second-pin"));
        var otherAdmin = await server.ConnectAsync("second-admin", "second-pin");
        var usersChanged = new TaskCompletionSource<LibraryChangedDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        otherAdmin.Events.LibraryChangeReceived += c =>
        {
            if (c.Kind == LibraryChangeKind.UsersChanged)
            {
                usersChanged.TrySetResult(c);
            }
        };
        var oldSession = await server.ConnectAdminAsync();
        var ended = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        oldSession.Events.SessionEndedReceived += reason => ended.TrySetResult(reason);
        var client = await server.ConnectAsync();

        var session = await client.Auth.ResetAdministratorAsync(" ADMIN ", "fresh-pin-9");

        Assert.Equal(TestServer.AdminName, session.UserName); // the stored spelling
        Assert.Equal(Role.Admin, session.Role);
        Assert.Equal(session, client.Session);
        await client.Admin.GetUsersAsync(); // signed in as an admin

        Assert.Contains("password was reset", await ended.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        await AssertEx.Eventually(() => oldSession.Session is null, "the old session is signed out");
        await AssertEx.ThrowsCode(ErrorCode.Unauthenticated, () => oldSession.Admin.GetUsersAsync());
        Assert.Equal(server.AdminId, (await usersChanged.Task.WaitAsync(TimeSpan.FromSeconds(10))).EntityId);
        await otherAdmin.Playback.GetStateAsync(); // other accounts are untouched

        await AssertEx.ThrowsCode(ErrorCode.Unauthenticated, () => server.ConnectAsync(TestServer.AdminName, TestServer.AdminSecret));
        await server.ConnectAsync(TestServer.AdminName, "fresh-pin-9");
        Assert.Equal(3, (await server.App.Users.ListAsync()).Count);
        Assert.Equal(second.Id, (await server.App.Users.GetByNameAsync("second-admin"))!.Id);

        var audit = Assert.Single(await server.App.AuditLog.QueryRecentAsync(), a => a.Action == "recovery.admin_reset");
        Assert.Equal("admin (PC\\owner)", audit.Actor);
    }

    [Fact]
    public async Task An_unknown_name_creates_a_new_administrator()
    {
        await using var server = await TestServer.StartAsync(identity: Elevated);
        var client = await server.ConnectAsync();

        var session = await client.Auth.ResetAdministratorAsync("Rescue", "rescue-pin");

        Assert.Equal(("Rescue", Role.Admin), (session.UserName, session.Role));
        Assert.Equal(Role.Admin, (await server.App.Users.GetByNameAsync("rescue"))!.Role);
        Assert.Equal(2, await server.App.Users.CountAdminsAsync());
        await server.ConnectAsync(TestServer.AdminName, TestServer.AdminSecret); // the existing admin is untouched
        Assert.Contains(await server.App.AuditLog.QueryRecentAsync(), a => a.Action == "recovery.admin_created");
    }

    [Fact]
    public async Task A_volunteer_name_or_bad_input_changes_nothing()
    {
        await using var server = await TestServer.StartAsync(identity: Elevated);
        var client = await server.ConnectAsync();
        var before = await server.SnapshotAsync();

        await AssertEx.ThrowsCode(ErrorCode.Conflict, () => client.Auth.ResetAdministratorAsync(TestServer.VolunteerName, "new-pin-1"));
        await AssertEx.ThrowsCode(ErrorCode.InvalidArgument, () => client.Auth.ResetAdministratorAsync(TestServer.AdminName, "1234567"));
        await AssertEx.ThrowsCode(ErrorCode.InvalidArgument, () => client.Auth.ResetAdministratorAsync("   ", "new-pin-1"));

        Assert.Equal(StripAudit(before), StripAudit(await server.SnapshotAsync()));
        Assert.Null(client.Session);
        await server.ConnectAsync(TestServer.VolunteerName, TestServer.VolunteerSecret);
    }

    [Fact]
    public async Task The_remote_can_never_recover()
    {
        await using var server = await TestServer.StartAsync();
        var before = await server.SnapshotAsync();
        // Even if a remote transport (wrongly) claimed elevation, CreateSession ignores it for non-desktop clients.
        var remote = server.App.CreateSession(ClientKind.Remote, "192.168.1.50", isElevatedAdministrator: true);

        // The remote is for signed-in administrators only (contract v2): recovery is not even reachable there.
        await AssertEx.ThrowsCode(ErrorCode.Unauthenticated, () => remote.ResetAdministratorAsync(TestServer.AdminName, "new-pin-1"));
        await AssertEx.ThrowsCode(ErrorCode.Unauthenticated, () => remote.ResetAdministratorAsync("intruder", "new-pin-1"));

        Assert.Equal(StripAudit(before), StripAudit(await server.SnapshotAsync()));
        await server.ConnectAsync(TestServer.AdminName, TestServer.AdminSecret);
    }

    [Fact]
    public async Task Attempts_are_rate_limited_per_windows_account()
    {
        await using var server = await TestServer.StartAsync(identity: NotElevated);
        var client = await server.ConnectAsync();
        for (var i = 0; i < 5; i++)
        {
            await AssertEx.ThrowsCode(ErrorCode.Forbidden, () => client.Auth.ResetAdministratorAsync(TestServer.AdminName, "new-pin-1"));
        }

        await AssertEx.ThrowsCode(ErrorCode.RateLimited, () => client.Auth.ResetAdministratorAsync(TestServer.AdminName, "new-pin-1"));
        Assert.Contains(await server.App.AuditLog.QueryRecentAsync(), a => a.Action == "recovery.rate_limited");

        // Remote attempts never count against a local account.
        var remote = server.App.CreateSession(ClientKind.Remote, "PC\\volunteer", isElevatedAdministrator: false);
        await AssertEx.ThrowsCode(ErrorCode.Unauthenticated, () => remote.ResetAdministratorAsync(TestServer.AdminName, "new-pin-1"));
        await AssertEx.ThrowsCode(ErrorCode.RateLimited, () => client.Auth.ResetAdministratorAsync(TestServer.AdminName, "new-pin-1"));
    }
}
