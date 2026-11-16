using System.Diagnostics;
using Microsoft.Data.Sqlite;
using SabhaMujic.Contracts;
using SabhaMujic.Data;

namespace SabhaMujic.Server.Tests;

/// <summary>QA items R-4 (best-effort session touch), R-6 (one 5 s busy limit), R-7 (storage message) and R-10 (uploads end with the session).</summary>
public sealed class StorageResilienceTests
{
    private static async Task<SqliteConnection> LockAsync(TestServer server)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = server.Database.DatabasePath, Pooling = false }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "BEGIN EXCLUSIVE;";
        await command.ExecuteNonQueryAsync();
        return connection;
    }

    [Theory]
    [InlineData(5, true)] // SQLITE_BUSY
    [InlineData(6, true)] // SQLITE_LOCKED
    [InlineData(13, true)] // SQLITE_FULL
    [InlineData(11, false)] // SQLITE_CORRUPT: still the generic message
    public async Task Busy_or_full_storage_reaches_clients_as_internal_with_a_specific_safe_message(int sqliteCode, bool specific)
    {
        await using var server = await TestServer.StartAsync();
        var client = await server.ConnectVolunteerAsync();
        server.Backend.ThrowOnPause = new SqliteException("SQLite Error: secret detail at /var/lib/sabhamujic/data.db", sqliteCode);

        var ex = await AssertEx.ThrowsCode(ErrorCode.Internal, () => client.Playback.PauseAsync());

        Assert.Equal(specific ? ServiceMessages.StorageUnavailable : "An internal error occurred in the SabhaMujic service.", ex.Message);
        Assert.DoesNotContain("secret", ex.Message);
        Assert.DoesNotContain("/var/lib", ex.Message);
    }

    [Fact]
    public void Disk_full_io_errors_and_wrapped_errors_are_recognised()
    {
        var full = OperatingSystem.IsWindows() ? new IOException("full", unchecked((int)0x80070070)) : new IOException("full", 28);
        Assert.True(StorageErrors.IsBusyOrFull(full));
        Assert.True(StorageErrors.IsBusyOrFull(new InvalidOperationException("wrapped", new SqliteException("busy", 5))));
        Assert.True(StorageErrors.IsBusyOrFull(new AggregateException(new SqliteException("full", 13))));
        Assert.False(StorageErrors.IsBusyOrFull(new IOException("other")));
        Assert.False(StorageErrors.IsBusyOrFull(new SqliteException("constraint", 19)));
        Assert.False(StorageErrors.IsBusyOrFull(null));
    }

    [Fact]
    public async Task A_locked_database_fails_a_write_after_about_five_seconds_not_thirty()
    {
        Assert.Equal(5, SqliteDatabase.BusyTimeoutSeconds);
        await using var server = await TestServer.StartAsync();
        var admin = await server.ConnectAdminAsync();
        await using var locker = await LockAsync(server);

        var watch = Stopwatch.StartNew();
        var ex = await AssertEx.ThrowsCode(ErrorCode.Internal, () => admin.Admin.CreateCategoryAsync("During lock"));
        Assert.Equal(ServiceMessages.StorageUnavailable, ex.Message);
        Assert.InRange(watch.Elapsed, TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task A_failed_session_touch_never_fails_the_command_and_is_retried_later()
    {
        await using var server = await TestServer.StartAsync();
        var volunteer = await server.ConnectVolunteerAsync();
        server.Clock.Advance(TimeSpan.FromMinutes(5)); // the next call wants to record activity (a DB write)

        await using (var locker = await LockAsync(server))
        {
            await volunteer.Playback.PauseAsync(); // waits for the busy timeout once, then goes ahead
            var watch = Stopwatch.StartNew();
            await volunteer.Playback.GetStateAsync(); // touches are paused for a while: no second wait
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"second call took {watch.Elapsed}");
        }

        // Later (after the pause), the touch is tried again and succeeds: the session slides as usual.
        server.Clock.Advance(TimeSpan.FromHours(11));
        await volunteer.Playback.GetStateAsync();
        server.Clock.Advance(TimeSpan.FromHours(11));
        await volunteer.Playback.GetStateAsync(); // still valid: 22 h after login, thanks to the retried touch
    }

    [Fact]
    public async Task Uploads_end_with_logout_and_with_revocation()
    {
        await using var server = await TestServer.StartAsync();
        var admin = await server.ConnectAdminAsync();
        await admin.Admin.BeginImportAsync("a.mp3", 1000);
        Assert.Equal(1, server.Uploads.ActiveCount);
        await admin.Auth.LogoutAsync();
        Assert.Equal(0, server.Uploads.ActiveCount);

        // Another admin's session is revoked (PIN changed by someone else): its upload goes too, at once.
        var other = await server.App.Users.CreateAsync("other-admin", Role.Admin, server.App.Auth.HashSecret("other-pin"));
        var victim = await server.ConnectAsync("other-admin", "other-pin");
        await victim.Admin.BeginImportAsync("b.mp3", 1000);
        Assert.Equal(1, server.Uploads.ActiveCount);
        var admin2 = await server.ConnectAdminAsync();
        await admin2.Admin.SetUserSecretAsync(other.Id, "changed-pin");
        await AssertEx.Eventually(() => server.Uploads.ActiveCount == 0, "revoked session's upload cancelled");
        Assert.Empty(Directory.EnumerateFiles(server.Uploads.StagingDirectory));
    }
}
