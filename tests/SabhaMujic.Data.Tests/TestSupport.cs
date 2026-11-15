using System.Security.Cryptography;
using SabhaMujic.Contracts;
using SabhaMujic.Core.Security;
using SabhaMujic.Data.Repositories;

namespace SabhaMujic.Data.Tests;

/// <summary>Settable clock. Starts at a fixed millisecond-precision instant.</summary>
public sealed class FakeClock : TimeProvider
{
    private DateTimeOffset _now = new(2026, 11, 15, 10, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>A temp directory deleted on dispose.</summary>
public sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sabhamujic-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>A migrated temp-file database plus every repository, for one test.</summary>
public sealed class TestDb : IAsyncLifetime
{
    private readonly TempDirectory _dir = new();

    public TestDb()
    {
        Database = new SqliteDatabase(System.IO.Path.Combine(_dir.Path, "db", "sabhamujic.db"), Clock);
        Users = new SqliteUserRepository(Database);
        Sessions = new SqliteSessionRepository(Database);
        Devices = new SqliteDeviceRepository(Database);
        PairingCodes = new SqlitePairingCodeRepository(Database);
        Categories = new SqliteCategoryRepository(Database);
        Tracks = new SqliteTrackRepository(Database);
        Analysis = new SqliteTrackAnalysisRepository(Database);
        Playlists = new SqlitePlaylistRepository(Database);
        Playback = new SqlitePlaybackStateRepository(Database);
        Settings = new SqliteSettingsRepository(Database);
        Audit = new SqliteAuditLog(Database);
    }

    public FakeClock Clock { get; } = new();

    public SqliteDatabase Database { get; }

    public SqliteUserRepository Users { get; }

    public SqliteSessionRepository Sessions { get; }

    public SqliteDeviceRepository Devices { get; }

    public SqlitePairingCodeRepository PairingCodes { get; }

    public SqliteCategoryRepository Categories { get; }

    public SqliteTrackRepository Tracks { get; }

    public SqliteTrackAnalysisRepository Analysis { get; }

    public SqlitePlaylistRepository Playlists { get; }

    public SqlitePlaybackStateRepository Playback { get; }

    public SqliteSettingsRepository Settings { get; }

    public SqliteAuditLog Audit { get; }

    public async Task InitializeAsync() => await Database.MigrateAsync();

    public Task DisposeAsync()
    {
        Database.ClearPool();
        _dir.Dispose();
        return Task.CompletedTask;
    }

    public static string NewHash() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    public static string Secret(string plain = "pin-1234") => SecretHasher.Hash(plain, iterations: 1_000);

    public async Task<TrackDto> AddTrackAsync(string title, string? artist = null, Guid? categoryId = null, string? hash = null)
    {
        hash ??= NewHash();
        var result = await Tracks.AddAsync(new NewTrack(hash, hash + ".mp3", title, artist, categoryId, 180, 1234));
        return result.Track;
    }
}

public static class AssertEx
{
    public static async Task<SabhaMujicException> ThrowsCode(ErrorCode code, Func<Task> action)
    {
        var ex = await Assert.ThrowsAsync<SabhaMujicException>(action);
        Assert.Equal(code, ex.Code);
        return ex;
    }

    public static SabhaMujicException ThrowsCode(ErrorCode code, Action action)
    {
        var ex = Assert.Throws<SabhaMujicException>(action);
        Assert.Equal(code, ex.Code);
        return ex;
    }
}
