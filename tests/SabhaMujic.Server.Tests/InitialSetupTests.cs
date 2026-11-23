using SabhaMujic.Contracts;

namespace SabhaMujic.Server.Tests;

/// <summary>First-run setup (decision #24) over the real pipe.</summary>
public sealed class InitialSetupTests
{
    [Fact]
    public async Task Non_elevated_desktop_callers_are_forbidden()
    {
        await using var server = await TestServer.StartAsync(seedUsers: false, identity: new FixedCallerIdentity("PC\\volunteer", elevated: false));
        var client = await server.ConnectAsync();

        Assert.True(await client.Auth.IsSetupRequiredAsync());
        await AssertEx.ThrowsCode(ErrorCode.Forbidden, () => client.Auth.CompleteInitialSetupAsync("admin", "1234"));
        Assert.True(await client.Auth.IsSetupRequiredAsync());
        Assert.Null(client.Session);
        Assert.Contains(await server.App.AuditLog.QueryRecentAsync(), a => a.Action == "setup.denied");
    }

    [Fact]
    public async Task Elevated_administrator_creates_the_account_and_is_signed_in()
    {
        await using var server = await TestServer.StartAsync(seedUsers: false, identity: new FixedCallerIdentity("PC\\owner", elevated: true));
        var client = await server.ConnectAsync();

        var session = await client.Auth.CompleteInitialSetupAsync(" Ramesh ", "pass-4321");

        Assert.Equal("Ramesh", session.UserName);
        Assert.Equal(Role.Admin, session.Role);
        Assert.Equal(session, client.Session);
        Assert.False(await client.Auth.IsSetupRequiredAsync());
        var users = await client.Admin.GetUsersAsync(); // signed in as an admin
        Assert.Equal(("Ramesh", Role.Admin), (Assert.Single(users).UserName, users[0].Role));
        var audit = Assert.Single(await server.App.AuditLog.QueryRecentAsync(), a => a.Action == "setup.complete");
        Assert.Equal("Ramesh (PC\\owner)", audit.Actor);

        // Another fresh connection can now sign in normally.
        var other = await server.ConnectAsync("ramesh", "pass-4321");
        Assert.Equal(Role.Admin, other.Session!.Role);

        // Second attempt: already set up.
        await AssertEx.ThrowsCode(ErrorCode.Conflict, () => other.Auth.CompleteInitialSetupAsync("second", "pass-9999"));
        Assert.Single(await server.App.Users.ListAsync());
    }

    [Fact]
    public async Task Concurrent_setups_create_exactly_one_administrator()
    {
        await using var server = await TestServer.StartAsync(seedUsers: false, identity: new FixedCallerIdentity("PC\\owner", elevated: true));
        var clients = new List<Client.PipeSabhaClient>();
        for (var i = 0; i < 8; i++)
        {
            clients.Add(await server.ConnectAsync());
        }

        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = clients.Select((c, i) => Task.Run(async () =>
        {
            await start.Task;
            try
            {
                await c.Auth.CompleteInitialSetupAsync("admin" + i, "password-" + i);
                return (ErrorCode?)null;
            }
            catch (SabhaMujicException ex)
            {
                return ex.Code;
            }
        })).ToArray();
        start.SetResult();
        var results = await Task.WhenAll(attempts);

        Assert.Single(results, r => r is null);
        Assert.All(results.Where(r => r is not null), r => Assert.Equal(ErrorCode.Conflict, r));
        Assert.Single(await server.App.Users.ListAsync());
    }

    [Fact]
    public async Task Invalid_input_leaves_setup_required()
    {
        await using var server = await TestServer.StartAsync(seedUsers: false, identity: new FixedCallerIdentity("PC\\owner", elevated: true));
        var client = await server.ConnectAsync();
        var tooShort = await AssertEx.ThrowsCode(ErrorCode.InvalidArgument, () => client.Auth.CompleteInitialSetupAsync("admin", "1234567"));
        Assert.Contains("password", tooShort.Message);
        await AssertEx.ThrowsCode(ErrorCode.InvalidArgument, () => client.Auth.CompleteInitialSetupAsync("  ", "12345678"));
        Assert.True(await client.Auth.IsSetupRequiredAsync());
        await client.Auth.CompleteInitialSetupAsync("admin", "12345678");
    }

    [Fact]
    public async Task The_remote_can_never_complete_setup()
    {
        await using var server = await TestServer.StartAsync(seedUsers: false);
        // Even if a remote transport (wrongly) claimed elevation, CreateSession ignores it for non-desktop clients.
        var remote = server.App.CreateSession(ClientKind.Remote, "192.168.1.50", isElevatedAdministrator: true);
        Assert.False(remote.IsElevatedAdministrator);
        // The remote is for signed-in administrators only (contract v2): setup is not even reachable there.
        await AssertEx.ThrowsCode(ErrorCode.Unauthenticated, () => remote.IsSetupRequiredAsync());
        await AssertEx.ThrowsCode(ErrorCode.Unauthenticated, () => remote.CompleteInitialSetupAsync("admin", "admin-password"));
        Assert.Empty(await server.App.Users.ListAsync());
    }
}
