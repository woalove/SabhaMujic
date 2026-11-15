using Dapper;
using SabhaMujic.Contracts;
using SabhaMujic.Core.Security;
using SabhaMujic.Data.Repositories;

namespace SabhaMujic.Data.Tests;

public sealed class UserRepositoryTests : IAsyncLifetime
{
    private readonly TestDb _t = new();

    public Task InitializeAsync() => _t.InitializeAsync();

    public Task DisposeAsync() => _t.DisposeAsync();

    [Fact]
    public async Task Create_get_list_update_delete()
    {
        var hash = TestDb.Secret();
        var created = await _t.Users.CreateAsync("  Ramesh ", Role.Admin, hash);
        Assert.Equal("Ramesh", created.UserName);
        Assert.Equal(_t.Clock.GetUtcNow(), created.CreatedAt);

        Assert.Equal(created, await _t.Users.GetByIdAsync(created.Id));
        Assert.Equal(created, await _t.Users.GetByNameAsync("ramesh"));
        Assert.Equal(created, await _t.Users.GetByNameAsync("RAMESH"));
        Assert.Null(await _t.Users.GetByNameAsync("nobody"));
        Assert.Null(await _t.Users.GetByIdAsync(Guid.NewGuid()));

        var volunteer = await _t.Users.CreateAsync("Asha", Role.Volunteer, TestDb.Secret());
        var list = await _t.Users.ListAsync();
        Assert.Equal(["Asha", "Ramesh"], list.Select(u => u.UserName));
        Assert.Equal(new UserDto(volunteer.Id, "Asha", Role.Volunteer, volunteer.CreatedAt), list[0]);

        _t.Clock.Advance(TimeSpan.FromMinutes(1));
        var newHash = TestDb.Secret("new-secret");
        Assert.True(await _t.Users.UpdateSecretHashAsync(created.Id, newHash));
        var updated = await _t.Users.GetByIdAsync(created.Id);
        Assert.True(SecretHasher.Verify("new-secret", updated!.SecretHash));
        Assert.Equal(_t.Clock.GetUtcNow(), updated.UpdatedAt);
        Assert.False(await _t.Users.UpdateSecretHashAsync(Guid.NewGuid(), newHash));

        Assert.True(await _t.Users.DeleteAsync(volunteer.Id));
        Assert.False(await _t.Users.DeleteAsync(volunteer.Id));
        Assert.Single(await _t.Users.ListAsync());
    }

    [Theory]
    [InlineData("admin", "ADMIN")]
    [InlineData("Ödön", "öDÖN")]
    [InlineData("ｆｕｌｌ", "FULL")]
    public async Task User_names_are_unique_case_insensitively(string first, string second)
    {
        await _t.Users.CreateAsync(first, Role.Admin, TestDb.Secret());
        await AssertEx.ThrowsCode(ErrorCode.Conflict, () => _t.Users.CreateAsync(second, Role.Volunteer, TestDb.Secret()));
        Assert.NotNull(await _t.Users.GetByNameAsync(second));
    }

    [Fact]
    public async Task Plain_secrets_and_bad_names_are_rejected()
    {
        await AssertEx.ThrowsCode(ErrorCode.InvalidArgument, () => _t.Users.CreateAsync("a", Role.Admin, "1234"));
        await AssertEx.ThrowsCode(ErrorCode.InvalidArgument, () => _t.Users.CreateAsync(" ", Role.Admin, TestDb.Secret()));
        await AssertEx.ThrowsCode(ErrorCode.InvalidArgument, () => _t.Users.CreateAsync(new string('x', 65), Role.Admin, TestDb.Secret()));
        await AssertEx.ThrowsCode(ErrorCode.InvalidArgument, () => _t.Users.CreateAsync("a\nb", Role.Admin, TestDb.Secret()));
        await AssertEx.ThrowsCode(ErrorCode.InvalidArgument, () => _t.Users.CreateAsync("ok", (Role)7, TestDb.Secret()));
    }

    [Fact]
    public async Task Admin_count_and_last_admin_protection()
    {
        var a1 = await _t.Users.CreateAsync("a1", Role.Admin, TestDb.Secret());
        var a2 = await _t.Users.CreateAsync("a2", Role.Admin, TestDb.Secret());
        await _t.Users.CreateAsync("v", Role.Volunteer, TestDb.Secret());
        Assert.Equal(2, await _t.Users.CountAdminsAsync());

        Assert.True(await _t.Users.DeleteAsync(a1.Id));
        Assert.Equal(1, await _t.Users.CountAdminsAsync());
        await AssertEx.ThrowsCode(ErrorCode.Conflict, () => _t.Users.DeleteAsync(a2.Id));
        Assert.NotNull(await _t.Users.GetByIdAsync(a2.Id));
        Assert.True(await _t.Users.DeleteAsync(a2.Id, preserveLastAdmin: false));
        Assert.Equal(0, await _t.Users.CountAdminsAsync());
    }

    [Fact]
    public async Task Deleting_a_user_removes_their_sessions()
    {
        var user = await _t.Users.CreateAsync("v", Role.Volunteer, TestDb.Secret());
        var hash = Tokens.HashToken(Tokens.NewToken());
        await _t.Sessions.CreateAsync(new NewSession(hash, user.Id, null, Role.Volunteer, ClientKind.Desktop, _t.Clock.GetUtcNow().AddHours(1)));
        Assert.True(await _t.Users.DeleteAsync(user.Id));

        await using var conn = await _t.Database.OpenConnectionAsync();
        Assert.Equal(0, await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM sessions;"));
    }
}

public sealed class SessionRepositoryTests : IAsyncLifetime
{
    private readonly TestDb _t = new();
    private UserRecord _user = null!;

    public async Task InitializeAsync()
    {
        await _t.InitializeAsync();
        _user = await _t.Users.CreateAsync("vol", Role.Volunteer, TestDb.Secret());
    }

    public Task DisposeAsync() => _t.DisposeAsync();

    private NewSession NewUserSession(string tokenHash, TimeSpan lifetime) =>
        new(tokenHash, _user.Id, null, Role.Volunteer, ClientKind.Desktop, _t.Clock.GetUtcNow() + lifetime);

    [Fact]
    public async Task Create_and_get_by_token_hash()
    {
        var token = Tokens.NewToken();
        var created = await _t.Sessions.CreateAsync(NewUserSession(Tokens.HashToken(token), TimeSpan.FromHours(8)));
        var found = await _t.Sessions.GetActiveByTokenHashAsync(Tokens.HashToken(token));
        Assert.Equal(created, found);
        Assert.Equal(_user.Id, found!.UserId);
        Assert.Null(await _t.Sessions.GetActiveByTokenHashAsync(Tokens.HashToken(Tokens.NewToken())));
        Assert.Null(await _t.Sessions.GetActiveByTokenHashAsync(token)); // the raw token is not a hash
        Assert.Null(await _t.Sessions.GetActiveByTokenHashAsync(""));

        await using var conn = await _t.Database.OpenConnectionAsync();
        Assert.Equal(0, await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM sessions WHERE token_hash = @T;", new { T = token }));
    }

    [Fact]
    public async Task Expired_sessions_are_not_returned()
    {
        var hash = Tokens.HashToken(Tokens.NewToken());
        await _t.Sessions.CreateAsync(NewUserSession(hash, TimeSpan.FromMinutes(10)));
        _t.Clock.Advance(TimeSpan.FromMinutes(9));
        Assert.NotNull(await _t.Sessions.GetActiveByTokenHashAsync(hash));
        _t.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Null(await _t.Sessions.GetActiveByTokenHashAsync(hash));
    }

    [Fact]
    public async Task Revoked_sessions_are_not_returned()
    {
        var hash = Tokens.HashToken(Tokens.NewToken());
        var s = await _t.Sessions.CreateAsync(NewUserSession(hash, TimeSpan.FromHours(1)));
        Assert.True(await _t.Sessions.RevokeAsync(s.Id));
        Assert.False(await _t.Sessions.RevokeAsync(s.Id));
        Assert.Null(await _t.Sessions.GetActiveByTokenHashAsync(hash));
        Assert.False(await _t.Sessions.TouchAsync(s.Id));
    }

    [Fact]
    public async Task Revoke_all_for_user_and_device()
    {
        var h1 = Tokens.HashToken(Tokens.NewToken());
        var h2 = Tokens.HashToken(Tokens.NewToken());
        var h3 = Tokens.HashToken(Tokens.NewToken());
        await _t.Sessions.CreateAsync(NewUserSession(h1, TimeSpan.FromHours(1)));
        await _t.Sessions.CreateAsync(NewUserSession(h2, TimeSpan.FromHours(1)));
        var device = await _t.Devices.CreateAsync("iPhone", Role.Volunteer);
        await _t.Sessions.CreateAsync(new NewSession(h3, null, device.Id, Role.Volunteer, ClientKind.Remote, _t.Clock.GetUtcNow().AddDays(30)));

        Assert.Equal(2, await _t.Sessions.RevokeAllForUserAsync(_user.Id));
        Assert.Null(await _t.Sessions.GetActiveByTokenHashAsync(h1));
        Assert.Null(await _t.Sessions.GetActiveByTokenHashAsync(h2));
        Assert.NotNull(await _t.Sessions.GetActiveByTokenHashAsync(h3));

        Assert.Equal(1, await _t.Sessions.RevokeAllForDeviceAsync(device.Id));
        Assert.Null(await _t.Sessions.GetActiveByTokenHashAsync(h3));
    }

    [Fact]
    public async Task Touch_updates_last_seen_and_extends_expiry_forward_only()
    {
        var hash = Tokens.HashToken(Tokens.NewToken());
        var s = await _t.Sessions.CreateAsync(NewUserSession(hash, TimeSpan.FromHours(1)));
        _t.Clock.Advance(TimeSpan.FromMinutes(30));
        Assert.True(await _t.Sessions.TouchAsync(s.Id));
        var touched = await _t.Sessions.GetActiveByTokenHashAsync(hash);
        Assert.Equal(_t.Clock.GetUtcNow(), touched!.LastSeenAt);
        Assert.Equal(s.ExpiresAt, touched.ExpiresAt);

        var later = _t.Clock.GetUtcNow().AddHours(4);
        Assert.True(await _t.Sessions.TouchAsync(s.Id, later));
        Assert.Equal(later, (await _t.Sessions.GetActiveByTokenHashAsync(hash))!.ExpiresAt);
        Assert.True(await _t.Sessions.TouchAsync(s.Id, _t.Clock.GetUtcNow().AddMinutes(1)));
        Assert.Equal(later, (await _t.Sessions.GetActiveByTokenHashAsync(hash))!.ExpiresAt);
        Assert.False(await _t.Sessions.TouchAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task Purge_removes_expired_and_revoked_only()
    {
        var keep = Tokens.HashToken(Tokens.NewToken());
        await _t.Sessions.CreateAsync(NewUserSession(keep, TimeSpan.FromHours(2)));
        await _t.Sessions.CreateAsync(NewUserSession(Tokens.HashToken(Tokens.NewToken()), TimeSpan.FromMinutes(5)));
        var revoked = await _t.Sessions.CreateAsync(NewUserSession(Tokens.HashToken(Tokens.NewToken()), TimeSpan.FromHours(2)));
        await _t.Sessions.RevokeAsync(revoked.Id);
        _t.Clock.Advance(TimeSpan.FromMinutes(10));

        Assert.Equal(2, await _t.Sessions.PurgeExpiredAsync());
        Assert.NotNull(await _t.Sessions.GetActiveByTokenHashAsync(keep));
    }

    [Fact]
    public async Task Invalid_sessions_are_rejected()
    {
        var hash = Tokens.HashToken(Tokens.NewToken());
        await AssertEx.ThrowsCode(ErrorCode.InvalidArgument, () => _t.Sessions.CreateAsync(
            new NewSession(hash, null, null, Role.Volunteer, ClientKind.Desktop, _t.Clock.GetUtcNow().AddHours(1))));
        await AssertEx.ThrowsCode(ErrorCode.InvalidArgument, () => _t.Sessions.CreateAsync(NewUserSession("not-a-hash", TimeSpan.FromHours(1))));
        await AssertEx.ThrowsCode(ErrorCode.InvalidArgument, () => _t.Sessions.CreateAsync(NewUserSession(hash, TimeSpan.Zero)));
        await AssertEx.ThrowsCode(ErrorCode.NotFound, () => _t.Sessions.CreateAsync(
            new NewSession(hash, Guid.NewGuid(), null, Role.Volunteer, ClientKind.Desktop, _t.Clock.GetUtcNow().AddHours(1))));
        await _t.Sessions.CreateAsync(NewUserSession(hash, TimeSpan.FromHours(1)));
        await AssertEx.ThrowsCode(ErrorCode.Conflict, () => _t.Sessions.CreateAsync(NewUserSession(hash, TimeSpan.FromHours(1))));
    }
}

public sealed class DeviceAndPairingTests : IAsyncLifetime
{
    private readonly TestDb _t = new();

    public Task InitializeAsync() => _t.InitializeAsync();

    public Task DisposeAsync() => _t.DisposeAsync();

    [Fact]
    public async Task Device_create_list_get_touch_revoke()
    {
        var d1 = await _t.Devices.CreateAsync("Asha's iPhone", Role.Volunteer);
        _t.Clock.Advance(TimeSpan.FromSeconds(1));
        var d2 = await _t.Devices.CreateAsync("Admin iPad", Role.Admin);
        Assert.Equal(d1, await _t.Devices.GetAsync(d1.Id));
        Assert.Null(await _t.Devices.GetAsync(Guid.NewGuid()));
        Assert.Equal([d2.Id, d1.Id], (await _t.Devices.ListAsync()).Select(d => d.Id));

        _t.Clock.Advance(TimeSpan.FromMinutes(5));
        Assert.True(await _t.Devices.TouchAsync(d1.Id));
        var listed = (await _t.Devices.ListAsync()).Single(d => d.Id == d1.Id);
        Assert.Equal(_t.Clock.GetUtcNow(), listed.LastSeenAt);
        Assert.Equal(new DeviceDto(d1.Id, "Asha's iPhone", Role.Volunteer, d1.PairedAt, _t.Clock.GetUtcNow()), listed);

        var hash = Tokens.HashToken(Tokens.NewToken());
        await _t.Sessions.CreateAsync(new NewSession(hash, null, d1.Id, Role.Volunteer, ClientKind.Remote, _t.Clock.GetUtcNow().AddDays(30)));
        Assert.NotNull(await _t.Sessions.GetActiveByTokenHashAsync(hash));

        Assert.True(await _t.Devices.RevokeAsync(d1.Id));
        Assert.False(await _t.Devices.RevokeAsync(d1.Id));
        Assert.False(await _t.Devices.TouchAsync(d1.Id));
        Assert.True((await _t.Devices.GetAsync(d1.Id))!.IsRevoked);
        Assert.Null(await _t.Sessions.GetActiveByTokenHashAsync(hash));
        Assert.Equal([d2.Id], (await _t.Devices.ListAsync()).Select(d => d.Id));
        Assert.Equal(2, (await _t.Devices.ListAsync(includeRevoked: true)).Count);
    }

    [Fact]
    public async Task A_device_session_is_rejected_once_the_device_is_revoked_even_if_the_session_was_not()
    {
        var device = await _t.Devices.CreateAsync("Phone", Role.Volunteer);
        var hash = Tokens.HashToken(Tokens.NewToken());
        await _t.Sessions.CreateAsync(new NewSession(hash, null, device.Id, Role.Volunteer, ClientKind.Remote, _t.Clock.GetUtcNow().AddDays(1)));
        await using (var conn = await _t.Database.OpenConnectionAsync())
        {
            await conn.ExecuteAsync("UPDATE devices SET revoked_at = 1 WHERE id = @Id;", new { device.Id });
        }

        Assert.Null(await _t.Sessions.GetActiveByTokenHashAsync(hash));
    }

    [Fact]
    public async Task Pairing_code_is_single_use_and_stored_hashed()
    {
        var admin = await _t.Users.CreateAsync("admin", Role.Admin, TestDb.Secret());
        var code = await _t.PairingCodes.CreateAsync(Role.Volunteer, _t.Clock.GetUtcNow().AddMinutes(5), admin.Id);
        Assert.Matches("^[0-9]{6}$", code.Code);

        await using (var conn = await _t.Database.OpenConnectionAsync())
        {
            Assert.Equal(0, await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM pairing_codes WHERE code_hash = @C;", new { C = code.Code }));
            Assert.Equal(1, await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM pairing_codes WHERE code_hash = @C;",
                new { C = Tokens.HashToken(code.Code) }));
        }

        var consumed = await _t.PairingCodes.TryConsumeAsync(code.Code);
        Assert.Equal(new ConsumedPairingCode(code.Id, Role.Volunteer), consumed);
        Assert.Null(await _t.PairingCodes.TryConsumeAsync(code.Code));
        Assert.Null(await _t.PairingCodes.TryConsumeAsync("abcdef"));
        Assert.Null(await _t.PairingCodes.TryConsumeAsync("12345"));
    }

    [Fact]
    public async Task Expired_pairing_code_cannot_be_consumed_and_is_purged()
    {
        var code = await _t.PairingCodes.CreateAsync(Role.Admin, _t.Clock.GetUtcNow().AddMinutes(5));
        var used = await _t.PairingCodes.CreateAsync(Role.Admin, _t.Clock.GetUtcNow().AddMinutes(10));
        var live = await _t.PairingCodes.CreateAsync(Role.Admin, _t.Clock.GetUtcNow().AddMinutes(10));
        Assert.NotNull(await _t.PairingCodes.TryConsumeAsync(used.Code));
        _t.Clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Null(await _t.PairingCodes.TryConsumeAsync(code.Code));
        Assert.Equal(2, await _t.PairingCodes.PurgeAsync());
        Assert.NotNull(await _t.PairingCodes.TryConsumeAsync(live.Code));
        await AssertEx.ThrowsCode(ErrorCode.InvalidArgument, () => _t.PairingCodes.CreateAsync(Role.Admin, _t.Clock.GetUtcNow()));
    }

    [Fact]
    public async Task Concurrent_consumers_exactly_one_wins()
    {
        for (var round = 0; round < 3; round++)
        {
            var code = await _t.PairingCodes.CreateAsync(Role.Volunteer, _t.Clock.GetUtcNow().AddMinutes(5));
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var attempts = Enumerable.Range(0, 20).Select(_ => Task.Run(async () =>
            {
                await start.Task;
                return await _t.PairingCodes.TryConsumeAsync(code.Code);
            })).ToArray();
            start.SetResult();
            var results = await Task.WhenAll(attempts);
            Assert.Single(results, r => r is not null);
        }
    }

    [Fact]
    public async Task Active_pairing_codes_are_distinct()
    {
        var codes = new List<string>();
        for (var i = 0; i < 50; i++)
        {
            codes.Add((await _t.PairingCodes.CreateAsync(Role.Volunteer, _t.Clock.GetUtcNow().AddMinutes(5))).Code);
        }

        Assert.Equal(codes.Count, codes.Distinct().Count());
    }
}
