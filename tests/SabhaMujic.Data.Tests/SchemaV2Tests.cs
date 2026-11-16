using Dapper;
using SabhaMujic.Contracts;
using SabhaMujic.Core.Security;
using SabhaMujic.Data.Repositories;

namespace SabhaMujic.Data.Tests;

/// <summary>Migration 0002 (analysis details), batch track lookup and "revoke my other sessions".</summary>
public sealed class SchemaV2Tests : IAsyncLifetime
{
    private readonly TestDb _t = new();

    public Task InitializeAsync() => _t.InitializeAsync();

    public Task DisposeAsync() => _t.DisposeAsync();

    [Fact]
    public async Task A_version_1_database_upgrades_and_keeps_its_data()
    {
        using var dir = new TempDirectory();
        var db = new SqliteDatabase(Path.Combine(dir.Path, "v1.db"), new FakeClock());
        try
        {
            Assert.Equal(1, await db.MigrateAsync(SchemaMigrator.Embedded.Take(1).ToArray(), CancellationToken.None));
            var tracks = new SqliteTrackRepository(db);
            var analysis = new SqliteTrackAnalysisRepository(db);
            var hash = TestDb.NewHash();
            var track = (await tracks.AddAsync(new NewTrack(hash, hash + ".mp3", "Old", null, null, 60, 1))).Track;
            await using (var conn = await db.OpenConnectionAsync())
            {
                // Written by v1 code: no details column yet.
                await conn.ExecuteAsync(
                    """
                    INSERT INTO track_analysis (track_id, status, analysis_version, trim_start_seconds, trim_end_seconds,
                        loop_start_seconds, loop_end_seconds, loop_crossfade_seconds, confidence, error, updated_at)
                    VALUES (@Id, 2, 1, 0.5, 59, 10, 50, 0.2, 0.9, NULL, 0);
                    """, new { track.Id });
            }

            Assert.Equal(SqliteDatabase.LatestSchemaVersion, await db.MigrateAsync());
            Assert.Equal(2, SqliteDatabase.LatestSchemaVersion);

            var stored = await analysis.GetAsync(track.Id);
            Assert.Equal(new TrackAnalysis(track.Id, AnalysisStatus.Ready, 1, 0.5, 59, 10, 50, 0.2, 0.9), stored);
            Assert.Null(stored!.DetailsJson);
            Assert.Equal(new LoopRegionDto(10, 50, 0.2, 0.9, false), (await tracks.GetAsync(track.Id))!.Loop);
        }
        finally
        {
            db.ClearPool();
        }
    }

    [Fact]
    public async Task Analysis_details_round_trip_and_must_be_json()
    {
        var track = await _t.AddTrackAsync("T");
        const string details = """{"Version":1,"TotalFrames":480000,"Loop":{"SeamCorrelation":0.61,"IsFallback":false},"TempoBpm":120.5}""";
        var analysis = new TrackAnalysis(track.Id, AnalysisStatus.Ready, 1, 0, 10, 2, 8, 0.1, 0.7, DetailsJson: details);
        await _t.Analysis.UpsertAsync(analysis);
        Assert.Equal(analysis, await _t.Analysis.GetAsync(track.Id));

        // TrackDto mapping is unchanged by the details.
        Assert.Equal(new LoopRegionDto(2, 8, 0.1, 0.7, false), (await _t.Tracks.GetAsync(track.Id))!.Loop);

        await AssertEx.ThrowsCode(ErrorCode.InvalidArgument, () => _t.Analysis.UpsertAsync(analysis with { DetailsJson = "{not json" }));

        // Re-analysis without details clears them.
        await _t.Analysis.UpsertAsync(new TrackAnalysis(track.Id, AnalysisStatus.Pending, 0));
        Assert.Null((await _t.Analysis.GetAsync(track.Id))!.DetailsJson);
    }

    [Fact]
    public async Task Batch_lookup_is_one_ordered_query()
    {
        var a = await _t.AddTrackAsync("A");
        var b = await _t.AddTrackAsync("B");
        var c = await _t.AddTrackAsync("C");

        var found = await _t.Tracks.GetManyAsync([c.Id, Guid.NewGuid(), a.Id, c.Id, b.Id]);
        Assert.Equal([c.Id, a.Id, b.Id], found.Select(t => t.Id));
        Assert.Equal(a, found[1]);
        Assert.Empty(await _t.Tracks.GetManyAsync([]));
        Assert.Empty(await _t.Tracks.GetManyAsync([Guid.NewGuid()]));
        await AssertEx.ThrowsCode(ErrorCode.InvalidArgument,
            () => _t.Tracks.GetManyAsync(Enumerable.Range(0, SqliteTrackRepository.MaxBatch + 1).Select(_ => Guid.NewGuid()).ToArray()));

        var many = new List<Guid>();
        for (var i = 0; i < 300; i++)
        {
            many.Add((await _t.AddTrackAsync("T" + i)).Id);
        }

        many.Reverse();
        Assert.Equal(many, (await _t.Tracks.GetManyAsync(many)).Select(t => t.Id));
    }

    [Fact]
    public async Task Playback_position_is_saved_with_the_current_item()
    {
        var a = await _t.AddTrackAsync("A");
        var b = await _t.AddTrackAsync("B");
        QueueItemDto[] items = [new(Guid.NewGuid(), a.Id), new(Guid.NewGuid(), b.Id)];
        Assert.Equal(0, (await _t.Playback.LoadQueueAsync()).PositionSeconds);

        await _t.Playback.SaveQueueAsync(items, 1);
        await _t.Playback.SavePositionAsync(42.5);
        var loaded = await _t.Playback.LoadQueueAsync();
        Assert.Equal(items, loaded.Items);
        Assert.Equal((1, 42.5), (loaded.CurrentIndex, loaded.PositionSeconds));

        // When the current item is gone (its track was deleted), the position no longer applies.
        await _t.Tracks.DeleteAsync(b.Id);
        Assert.Equal(0, (await _t.Playback.LoadQueueAsync()).PositionSeconds);

        await AssertEx.ThrowsCode(ErrorCode.InvalidArgument, () => _t.Playback.SavePositionAsync(-1));
        await AssertEx.ThrowsCode(ErrorCode.InvalidArgument, () => _t.Playback.SavePositionAsync(double.NaN));
    }

    [Fact]
    public async Task Revoking_all_but_one_session()
    {
        var user = await _t.Users.CreateAsync("u", Role.Admin, TestDb.Secret());
        var other = await _t.Users.CreateAsync("o", Role.Volunteer, TestDb.Secret());
        var hashes = Enumerable.Range(0, 3).Select(_ => Tokens.HashToken(Tokens.NewToken())).ToArray();
        var expires = _t.Clock.GetUtcNow().AddHours(1);
        var keep = await _t.Sessions.CreateAsync(new NewSession(hashes[0], user.Id, null, Role.Admin, ClientKind.Desktop, expires));
        await _t.Sessions.CreateAsync(new NewSession(hashes[1], user.Id, null, Role.Admin, ClientKind.Remote, expires));
        await _t.Sessions.CreateAsync(new NewSession(hashes[2], other.Id, null, Role.Volunteer, ClientKind.Desktop, expires));

        Assert.Equal(1, await _t.Sessions.RevokeAllForUserExceptAsync(user.Id, keep.Id));
        Assert.NotNull(await _t.Sessions.GetActiveByTokenHashAsync(hashes[0]));
        Assert.Null(await _t.Sessions.GetActiveByTokenHashAsync(hashes[1]));
        Assert.NotNull(await _t.Sessions.GetActiveByTokenHashAsync(hashes[2]));
    }
}
