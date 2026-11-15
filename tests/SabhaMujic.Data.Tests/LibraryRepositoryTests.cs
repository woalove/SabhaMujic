using Dapper;
using SabhaMujic.Contracts;
using SabhaMujic.Data.Repositories;

namespace SabhaMujic.Data.Tests;

public sealed class CategoryRepositoryTests : IAsyncLifetime
{
    private readonly TestDb _t = new();

    public Task InitializeAsync() => _t.InitializeAsync();

    public Task DisposeAsync() => _t.DisposeAsync();

    [Fact]
    public async Task Crud_with_track_counts()
    {
        var bhajan = await _t.Categories.CreateAsync(" Bhajan ");
        var kirtan = await _t.Categories.CreateAsync("Kirtan");
        Assert.Equal(new CategoryDto(bhajan.Id, "Bhajan", 0, 0), bhajan);
        Assert.Equal(1, kirtan.SortOrder);

        await _t.AddTrackAsync("One", categoryId: bhajan.Id);
        await _t.AddTrackAsync("Two", categoryId: bhajan.Id);
        await _t.AddTrackAsync("Three", categoryId: kirtan.Id);
        await _t.AddTrackAsync("Loose");

        var list = await _t.Categories.ListAsync();
        Assert.Equal([("Bhajan", 2), ("Kirtan", 1)], list.Select(c => (c.Name, c.TrackCount)));
        Assert.Equal(2, (await _t.Categories.GetAsync(bhajan.Id))!.TrackCount);
        Assert.Null(await _t.Categories.GetAsync(Guid.NewGuid()));

        var renamed = await _t.Categories.RenameAsync(kirtan.Id, "Kirtans");
        Assert.Equal("Kirtans", renamed!.Name);
        Assert.Equal(1, renamed.TrackCount);
        Assert.Null(await _t.Categories.RenameAsync(Guid.NewGuid(), "x"));
        Assert.Equal("kirtans", (await _t.Categories.RenameAsync(kirtan.Id, "kirtans"))!.Name); // re-casing its own name is allowed

        Assert.True(await _t.Categories.DeleteAsync(kirtan.Id));
        Assert.False(await _t.Categories.DeleteAsync(kirtan.Id));
        Assert.Single(await _t.Categories.ListAsync());
    }

    [Fact]
    public async Task Names_are_unique_case_insensitively()
    {
        var a = await _t.Categories.CreateAsync("Aarti");
        var b = await _t.Categories.CreateAsync("Dhun");
        await AssertEx.ThrowsCode(ErrorCode.Conflict, () => _t.Categories.CreateAsync("AARTI"));
        await AssertEx.ThrowsCode(ErrorCode.Conflict, () => _t.Categories.RenameAsync(b.Id, "aarti"));
        await AssertEx.ThrowsCode(ErrorCode.InvalidArgument, () => _t.Categories.CreateAsync(""));
        Assert.Equal("Aarti", (await _t.Categories.GetAsync(a.Id))!.Name);
    }

    [Fact]
    public async Task Reorder_puts_listed_first_and_keeps_the_rest()
    {
        var a = await _t.Categories.CreateAsync("A");
        var b = await _t.Categories.CreateAsync("B");
        var c = await _t.Categories.CreateAsync("C");
        var d = await _t.Categories.CreateAsync("D");

        await _t.Categories.ReorderAsync([c.Id, a.Id]);
        var list = await _t.Categories.ListAsync();
        Assert.Equal(["C", "A", "B", "D"], list.Select(x => x.Name));
        Assert.Equal([0, 1, 2, 3], list.Select(x => x.SortOrder));

        await _t.Categories.ReorderAsync([d.Id, c.Id, b.Id, a.Id]);
        Assert.Equal(["D", "C", "B", "A"], (await _t.Categories.ListAsync()).Select(x => x.Name));

        await AssertEx.ThrowsCode(ErrorCode.NotFound, () => _t.Categories.ReorderAsync([a.Id, Guid.NewGuid()]));
        await AssertEx.ThrowsCode(ErrorCode.InvalidArgument, () => _t.Categories.ReorderAsync([a.Id, a.Id]));
        Assert.Equal(["D", "C", "B", "A"], (await _t.Categories.ListAsync()).Select(x => x.Name));
    }

    [Fact]
    public async Task Deleting_a_category_uncategorizes_its_tracks()
    {
        var cat = await _t.Categories.CreateAsync("Prarthana");
        var track = await _t.AddTrackAsync("Song", categoryId: cat.Id);
        Assert.True(await _t.Categories.DeleteAsync(cat.Id));
        var after = await _t.Tracks.GetAsync(track.Id);
        Assert.NotNull(after);
        Assert.Null(after.CategoryId);
    }
}

public sealed class TrackRepositoryTests : IAsyncLifetime
{
    private readonly TestDb _t = new();

    public Task InitializeAsync() => _t.InitializeAsync();

    public Task DisposeAsync() => _t.DisposeAsync();

    [Fact]
    public async Task Add_get_update_delete()
    {
        var cat = await _t.Categories.CreateAsync("Bhajan");
        var admin = await _t.Users.CreateAsync("admin", Role.Admin, TestDb.Secret());
        var hash = TestDb.NewHash();
        var added = await _t.Tracks.AddAsync(new NewTrack(hash, hash + ".flac", " Jay Swaminarayan ", "Kirtan Mandal", cat.Id, 245.5, 9_999, "jay.flac", admin.Id));

        Assert.False(added.WasDuplicate);
        Assert.Equal(hash + ".flac", added.FileKey);
        var expected = new TrackDto(added.Track.Id, "Jay Swaminarayan", "Kirtan Mandal", cat.Id, 245.5, AnalysisStatus.Pending, null, _t.Clock.GetUtcNow());
        Assert.Equal(expected, added.Track);
        Assert.Equal(expected, await _t.Tracks.GetAsync(added.Track.Id));
        Assert.Equal(new TrackFileInfo(added.Track.Id, hash + ".flac", hash, 9_999, "jay.flac"), await _t.Tracks.GetFileInfoAsync(added.Track.Id));
        Assert.Null(await _t.Tracks.GetAsync(Guid.NewGuid()));

        var updated = await _t.Tracks.UpdateMetadataAsync(new TrackUpdateDto(added.Track.Id, "New title", null, null));
        Assert.Equal(expected with { Title = "New title", Artist = null, CategoryId = null }, updated);
        Assert.Null(await _t.Tracks.UpdateMetadataAsync(new TrackUpdateDto(Guid.NewGuid(), "x", null, null)));
        await AssertEx.ThrowsCode(ErrorCode.NotFound, () => _t.Tracks.UpdateMetadataAsync(new TrackUpdateDto(added.Track.Id, "x", null, Guid.NewGuid())));
        await AssertEx.ThrowsCode(ErrorCode.InvalidArgument, () => _t.Tracks.UpdateMetadataAsync(new TrackUpdateDto(added.Track.Id, " ", null, null)));

        Assert.True(await _t.Tracks.SetDurationAsync(added.Track.Id, 300));
        Assert.Equal(300, (await _t.Tracks.GetAsync(added.Track.Id))!.DurationSeconds);

        Assert.Equal(hash + ".flac", await _t.Tracks.DeleteAsync(added.Track.Id));
        Assert.Null(await _t.Tracks.DeleteAsync(added.Track.Id));
        Assert.Null(await _t.Tracks.GetAsync(added.Track.Id));
    }

    [Fact]
    public async Task Duplicate_content_hash_returns_the_existing_track()
    {
        var hash = TestDb.NewHash();
        var first = await _t.Tracks.AddAsync(new NewTrack(hash, hash + ".mp3", "Original", null, null, 10, 100));
        var second = await _t.Tracks.AddAsync(new NewTrack(hash, hash + ".wav", "Copy", "Someone", null, 10, 100));

        Assert.True(second.WasDuplicate);
        Assert.Equal(first.Track, second.Track);
        Assert.Equal(hash + ".mp3", second.FileKey);
        Assert.Equal(1, (await _t.Tracks.QueryAsync(new TrackQuery())).Total);
    }

    [Fact]
    public async Task Concurrent_adds_of_the_same_content_create_one_track()
    {
        var hash = TestDb.NewHash();
        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(i =>
            Task.Run(() => _t.Tracks.AddAsync(new NewTrack(hash, hash + ".mp3", $"T{i}", null, null, 1, 1)))));
        Assert.Single(results, r => !r.WasDuplicate);
        Assert.Single(results.Select(r => r.Track.Id).Distinct());
    }

    [Fact]
    public async Task Add_validates_hash_key_and_references()
    {
        var hash = TestDb.NewHash();
        await AssertEx.ThrowsCode(ErrorCode.InvalidArgument, () => _t.Tracks.AddAsync(new NewTrack(hash.ToUpperInvariant(), hash + ".mp3", "t", null, null, 1, 1)));
        await AssertEx.ThrowsCode(ErrorCode.InvalidArgument, () => _t.Tracks.AddAsync(new NewTrack(hash, TestDb.NewHash() + ".mp3", "t", null, null, 1, 1)));
        await AssertEx.ThrowsCode(ErrorCode.InvalidArgument, () => _t.Tracks.AddAsync(new NewTrack(hash, "../" + hash + ".mp3", "t", null, null, 1, 1)));
        await AssertEx.ThrowsCode(ErrorCode.InvalidArgument, () => _t.Tracks.AddAsync(new NewTrack(hash, hash + ".mp3", "", null, null, 1, 1)));
        await AssertEx.ThrowsCode(ErrorCode.InvalidArgument, () => _t.Tracks.AddAsync(new NewTrack(hash, hash + ".mp3", "t", null, null, double.NaN, 1)));
        await AssertEx.ThrowsCode(ErrorCode.NotFound, () => _t.Tracks.AddAsync(new NewTrack(hash, hash + ".mp3", "t", null, Guid.NewGuid(), 1, 1)));
    }

    [Fact]
    public async Task Deleting_a_track_cascades_to_analysis_overrides_playlists_and_queue()
    {
        var keep = await _t.AddTrackAsync("Keep");
        var gone = await _t.AddTrackAsync("Gone");
        await _t.Analysis.UpsertAsync(new TrackAnalysis(gone.Id, AnalysisStatus.Ready, 1, 0, 100, 10, 90, 0.1, 0.9));
        await _t.Analysis.SetLoopOverrideAsync(gone.Id, new LoopOverride(5, 50, 0.2));
        var playlist = await _t.Playlists.CreateAsync("Mix", [gone.Id, keep.Id, gone.Id]);
        await _t.Playback.SaveQueueAsync([new QueueItemDto(Guid.NewGuid(), gone.Id), new QueueItemDto(Guid.NewGuid(), keep.Id)], 1);

        Assert.NotNull(await _t.Tracks.DeleteAsync(gone.Id));

        Assert.Null(await _t.Analysis.GetAsync(gone.Id));
        Assert.Null(await _t.Analysis.GetLoopOverrideAsync(gone.Id));
        Assert.Equal([keep.Id], (await _t.Playlists.GetAsync(playlist.Id))!.TrackIds);
        var queue = await _t.Playback.LoadQueueAsync();
        Assert.Equal([keep.Id], queue.Items.Select(i => i.TrackId));
        Assert.Equal(0, queue.CurrentIndex);
    }

    [Fact]
    public async Task Query_by_category_and_search_with_paging()
    {
        var cat = await _t.Categories.CreateAsync("Kirtan");
        await _t.AddTrackAsync("Bhaj Govindam", "Ensemble", cat.Id);
        await _t.AddTrackAsync("Aarti", "Bal Mandal", cat.Id);
        await _t.AddTrackAsync("Chesta", "Ensemble");
        await _t.AddTrackAsync("Dhun", null);

        var all = await _t.Tracks.QueryAsync(new TrackQuery());
        Assert.Equal(4, all.Total);
        Assert.Equal(["Aarti", "Bhaj Govindam", "Chesta", "Dhun"], all.Items.Select(t => t.Title));

        var inCat = await _t.Tracks.QueryAsync(new TrackQuery(CategoryId: cat.Id));
        Assert.Equal(["Aarti", "Bhaj Govindam"], inCat.Items.Select(t => t.Title));

        var byArtist = await _t.Tracks.QueryAsync(new TrackQuery(Search: "ensemble"));
        Assert.Equal(["Bhaj Govindam", "Chesta"], byArtist.Items.Select(t => t.Title));

        var both = await _t.Tracks.QueryAsync(new TrackQuery(cat.Id, "ENSEM"));
        Assert.Equal(["Bhaj Govindam"], both.Items.Select(t => t.Title));

        var byTitle = await _t.Tracks.QueryAsync(new TrackQuery(Search: "  dhu "));
        Assert.Equal(["Dhun"], byTitle.Items.Select(t => t.Title));

        var page = await _t.Tracks.QueryAsync(new TrackQuery(Skip: 1, Take: 2));
        Assert.Equal(4, page.Total);
        Assert.Equal(["Bhaj Govindam", "Chesta"], page.Items.Select(t => t.Title));

        var none = await _t.Tracks.QueryAsync(new TrackQuery(Search: "zzz"));
        Assert.Equal(0, none.Total);
        Assert.Empty(none.Items);

        await AssertEx.ThrowsCode(ErrorCode.InvalidArgument, () => _t.Tracks.QueryAsync(new TrackQuery(Skip: -1)));
        await AssertEx.ThrowsCode(ErrorCode.InvalidArgument, () => _t.Tracks.QueryAsync(new TrackQuery(Take: 0)));
    }

    [Fact]
    public async Task Search_treats_like_wildcards_literally()
    {
        await _t.AddTrackAsync("100% Bhakti");
        await _t.AddTrackAsync("1000 Bhakti");
        await _t.AddTrackAsync("raag_one");
        await _t.AddTrackAsync("raagXone");
        await _t.AddTrackAsync(@"back\slash");
        await _t.AddTrackAsync("backXslash");

        Assert.Equal(["100% Bhakti"], (await _t.Tracks.QueryAsync(new TrackQuery(Search: "0%"))).Items.Select(t => t.Title));
        Assert.Equal(["raag_one"], (await _t.Tracks.QueryAsync(new TrackQuery(Search: "g_o"))).Items.Select(t => t.Title));
        Assert.Equal([@"back\slash"], (await _t.Tracks.QueryAsync(new TrackQuery(Search: @"k\s"))).Items.Select(t => t.Title));
        Assert.Equal(["100% Bhakti"], (await _t.Tracks.QueryAsync(new TrackQuery(Search: "%"))).Items.Select(t => t.Title));
        Assert.Equal(["raag_one"], (await _t.Tracks.QueryAsync(new TrackQuery(Search: "_"))).Items.Select(t => t.Title));
        Assert.Equal(0, (await _t.Tracks.QueryAsync(new TrackQuery(Search: "'; DROP TABLE tracks; --"))).Total);
        Assert.Equal(6, (await _t.Tracks.QueryAsync(new TrackQuery())).Total);
    }

    [Fact]
    public async Task Search_is_case_insensitive_for_non_ascii_text()
    {
        await _t.AddTrackAsync("Ärzte Lied", "Émile");
        Assert.Single((await _t.Tracks.QueryAsync(new TrackQuery(Search: "ärzte"))).Items);
        Assert.Single((await _t.Tracks.QueryAsync(new TrackQuery(Search: "ÉMILE"))).Items);
    }

    [Fact]
    public async Task Track_dto_exposes_ready_analysis_and_prefers_manual_override()
    {
        var track = await _t.AddTrackAsync("Loop me");
        await _t.Analysis.UpsertAsync(new TrackAnalysis(track.Id, AnalysisStatus.Analyzing, 1));
        Assert.Equal(AnalysisStatus.Analyzing, (await _t.Tracks.GetAsync(track.Id))!.Analysis);
        Assert.Null((await _t.Tracks.GetAsync(track.Id))!.Loop);

        await _t.Analysis.UpsertAsync(new TrackAnalysis(track.Id, AnalysisStatus.Ready, 1, 0.5, 170, 20, 140, 0.25, 0.8));
        var ready = await _t.Tracks.GetAsync(track.Id);
        Assert.Equal(AnalysisStatus.Ready, ready!.Analysis);
        Assert.Equal(new LoopRegionDto(20, 140, 0.25, 0.8, false), ready.Loop);

        await _t.Analysis.SetLoopOverrideAsync(track.Id, new LoopOverride(30, 120, 0.1));
        Assert.Equal(new LoopRegionDto(30, 120, 0.1, 1.0, true), (await _t.Tracks.GetAsync(track.Id))!.Loop);
        Assert.Equal(new LoopRegionDto(30, 120, 0.1, 1.0, true), (await _t.Tracks.QueryAsync(new TrackQuery())).Items.Single().Loop);
    }
}

public sealed class TrackAnalysisRepositoryTests : IAsyncLifetime
{
    private readonly TestDb _t = new();

    public Task InitializeAsync() => _t.InitializeAsync();

    public Task DisposeAsync() => _t.DisposeAsync();

    [Fact]
    public async Task Upsert_and_get_round_trip()
    {
        var track = await _t.AddTrackAsync("A");
        Assert.Null(await _t.Analysis.GetAsync(track.Id));

        var pending = new TrackAnalysis(track.Id, AnalysisStatus.Pending, 1);
        await _t.Analysis.UpsertAsync(pending);
        Assert.Equal(pending, await _t.Analysis.GetAsync(track.Id));

        var ready = new TrackAnalysis(track.Id, AnalysisStatus.Ready, 2, 0.25, 199.5, 12.5, 150.75, 0.2, 0.93);
        await _t.Analysis.UpsertAsync(ready);
        Assert.Equal(ready, await _t.Analysis.GetAsync(track.Id));

        var failed = new TrackAnalysis(track.Id, AnalysisStatus.Failed, 2, Error: "decode error");
        await _t.Analysis.UpsertAsync(failed);
        Assert.Equal(failed, await _t.Analysis.GetAsync(track.Id));

        await AssertEx.ThrowsCode(ErrorCode.NotFound, () => _t.Analysis.UpsertAsync(new TrackAnalysis(Guid.NewGuid(), AnalysisStatus.Pending, 1)));
        await AssertEx.ThrowsCode(ErrorCode.InvalidArgument, () => _t.Analysis.UpsertAsync(ready with { LoopEndSeconds = 1 }));
        await AssertEx.ThrowsCode(ErrorCode.InvalidArgument, () => _t.Analysis.UpsertAsync(ready with { LoopEndSeconds = null }));
        await AssertEx.ThrowsCode(ErrorCode.InvalidArgument, () => _t.Analysis.UpsertAsync(ready with { Confidence = 1.5 }));
        await AssertEx.ThrowsCode(ErrorCode.InvalidArgument, () => _t.Analysis.UpsertAsync(ready with { TrimStartSeconds = double.PositiveInfinity }));
    }

    [Fact]
    public async Task Reanalysis_does_not_wipe_the_manual_override()
    {
        var track = await _t.AddTrackAsync("A");
        await _t.Analysis.UpsertAsync(new TrackAnalysis(track.Id, AnalysisStatus.Ready, 1, LoopStartSeconds: 1, LoopEndSeconds: 2));
        await _t.Analysis.SetLoopOverrideAsync(track.Id, new LoopOverride(10, 20, 0.3));
        await _t.Analysis.UpsertAsync(new TrackAnalysis(track.Id, AnalysisStatus.Analyzing, 2));
        await _t.Analysis.UpsertAsync(new TrackAnalysis(track.Id, AnalysisStatus.Ready, 2, LoopStartSeconds: 3, LoopEndSeconds: 4));
        Assert.Equal(new LoopOverride(10, 20, 0.3), await _t.Analysis.GetLoopOverrideAsync(track.Id));

        await _t.Analysis.SetLoopOverrideAsync(track.Id, new LoopOverride(11, 21, 0));
        Assert.Equal(new LoopOverride(11, 21, 0), await _t.Analysis.GetLoopOverrideAsync(track.Id));
        await _t.Analysis.SetLoopOverrideAsync(track.Id, null);
        Assert.Null(await _t.Analysis.GetLoopOverrideAsync(track.Id));
        Assert.Equal(new LoopRegionDto(3, 4, 0, 0, false), (await _t.Tracks.GetAsync(track.Id))!.Loop);

        await AssertEx.ThrowsCode(ErrorCode.NotFound, () => _t.Analysis.SetLoopOverrideAsync(Guid.NewGuid(), new LoopOverride(1, 2, 0)));
        await AssertEx.ThrowsCode(ErrorCode.InvalidArgument, () => _t.Analysis.SetLoopOverrideAsync(track.Id, new LoopOverride(5, 5, 0)));
        await AssertEx.ThrowsCode(ErrorCode.InvalidArgument, () => _t.Analysis.SetLoopOverrideAsync(track.Id, new LoopOverride(-1, 5, 0)));
    }

    [Fact]
    public async Task Lists_tracks_needing_analysis_for_a_version()
    {
        var none = await _t.AddTrackAsync("none");
        _t.Clock.Advance(TimeSpan.FromSeconds(1));
        var pending = await _t.AddTrackAsync("pending");
        _t.Clock.Advance(TimeSpan.FromSeconds(1));
        var oldReady = await _t.AddTrackAsync("old-ready");
        _t.Clock.Advance(TimeSpan.FromSeconds(1));
        var currentReady = await _t.AddTrackAsync("current-ready");
        _t.Clock.Advance(TimeSpan.FromSeconds(1));
        var currentFailed = await _t.AddTrackAsync("current-failed");
        _t.Clock.Advance(TimeSpan.FromSeconds(1));
        var oldFailed = await _t.AddTrackAsync("old-failed");
        _t.Clock.Advance(TimeSpan.FromSeconds(1));
        var analyzing = await _t.AddTrackAsync("analyzing");

        await _t.Analysis.UpsertAsync(new TrackAnalysis(pending.Id, AnalysisStatus.Pending, 2));
        await _t.Analysis.UpsertAsync(new TrackAnalysis(oldReady.Id, AnalysisStatus.Ready, 1));
        await _t.Analysis.UpsertAsync(new TrackAnalysis(currentReady.Id, AnalysisStatus.Ready, 2));
        await _t.Analysis.UpsertAsync(new TrackAnalysis(currentFailed.Id, AnalysisStatus.Failed, 2, Error: "x"));
        await _t.Analysis.UpsertAsync(new TrackAnalysis(oldFailed.Id, AnalysisStatus.Failed, 1, Error: "x"));
        await _t.Analysis.UpsertAsync(new TrackAnalysis(analyzing.Id, AnalysisStatus.Analyzing, 2));

        Assert.Equal([none.Id, pending.Id, oldReady.Id, oldFailed.Id], await _t.Analysis.ListTracksNeedingAnalysisAsync(2));
        Assert.Equal([none.Id, pending.Id], await _t.Analysis.ListTracksNeedingAnalysisAsync(2, limit: 2));

        Assert.Equal(1, await _t.Analysis.ResetInterruptedAsync());
        Assert.Contains(analyzing.Id, await _t.Analysis.ListTracksNeedingAnalysisAsync(2));
    }
}

public sealed class PlaylistRepositoryTests : IAsyncLifetime
{
    private readonly TestDb _t = new();

    public Task InitializeAsync() => _t.InitializeAsync();

    public Task DisposeAsync() => _t.DisposeAsync();

    [Fact]
    public async Task Crud_and_ordered_tracks()
    {
        var a = await _t.AddTrackAsync("a");
        var b = await _t.AddTrackAsync("b");
        var c = await _t.AddTrackAsync("c");

        var created = await _t.Playlists.CreateAsync(" Sunday ", [c.Id, a.Id, c.Id]);
        Assert.Equal("Sunday", created.Name);
        Assert.Equal([c.Id, a.Id, c.Id], created.TrackIds);
        var fetched = await _t.Playlists.GetAsync(created.Id);
        Assert.Equal((created.Id, created.Name, created.UpdatedAt), (fetched!.Id, fetched.Name, fetched.UpdatedAt));
        Assert.Equal(created.TrackIds, fetched.TrackIds);

        var empty = await _t.Playlists.CreateAsync("Aarti");
        Assert.Empty(empty.TrackIds);
        Assert.Equal(["Aarti", "Sunday"], (await _t.Playlists.ListAsync()).Select(p => p.Name));
        Assert.Equal([c.Id, a.Id, c.Id], (await _t.Playlists.ListAsync()).Single(p => p.Id == created.Id).TrackIds);

        _t.Clock.Advance(TimeSpan.FromMinutes(1));
        var set = await _t.Playlists.SetTracksAsync(created.Id, [b.Id, a.Id]);
        Assert.Equal([b.Id, a.Id], set!.TrackIds);
        Assert.Equal(_t.Clock.GetUtcNow(), set.UpdatedAt);
        Assert.Null(await _t.Playlists.SetTracksAsync(Guid.NewGuid(), [a.Id]));

        var renamed = await _t.Playlists.RenameAsync(created.Id, "Sabha");
        Assert.Equal("Sabha", renamed!.Name);
        Assert.Equal([b.Id, a.Id], renamed.TrackIds);
        Assert.Null(await _t.Playlists.RenameAsync(Guid.NewGuid(), "x"));

        Assert.True(await _t.Playlists.DeleteAsync(created.Id));
        Assert.False(await _t.Playlists.DeleteAsync(created.Id));
        Assert.Null(await _t.Playlists.GetAsync(created.Id));
        await using var conn = await _t.Database.OpenConnectionAsync();
        Assert.Equal(0, await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM playlist_items WHERE playlist_id = @Id;", new { created.Id }));
    }

    [Fact]
    public async Task Setting_tracks_is_transactional()
    {
        var a = await _t.AddTrackAsync("a");
        var b = await _t.AddTrackAsync("b");
        var p = await _t.Playlists.CreateAsync("P", [a.Id, b.Id]);

        await AssertEx.ThrowsCode(ErrorCode.NotFound, () => _t.Playlists.SetTracksAsync(p.Id, [b.Id, Guid.NewGuid()]));
        Assert.Equal([a.Id, b.Id], (await _t.Playlists.GetAsync(p.Id))!.TrackIds);

        await AssertEx.ThrowsCode(ErrorCode.NotFound, () => _t.Playlists.CreateAsync("Q", [a.Id, Guid.NewGuid()]));
        Assert.Single(await _t.Playlists.ListAsync());
        await AssertEx.ThrowsCode(ErrorCode.InvalidArgument, () => _t.Playlists.CreateAsync(""));
    }
}

public sealed class PlaybackStateRepositoryTests : IAsyncLifetime
{
    private readonly TestDb _t = new();

    public Task InitializeAsync() => _t.InitializeAsync();

    public Task DisposeAsync() => _t.DisposeAsync();

    [Fact]
    public async Task Fresh_database_has_defaults_and_an_empty_queue()
    {
        Assert.Equal(PlaybackSettings.Default, await _t.Playback.LoadSettingsAsync());
        var queue = await _t.Playback.LoadQueueAsync();
        Assert.Empty(queue.Items);
        Assert.Equal(-1, queue.CurrentIndex);
    }

    [Fact]
    public async Task Settings_round_trip()
    {
        var settings = new PlaybackSettings(0.35f, true, LoopMode.SmartLoop, 7.5);
        await _t.Playback.SaveSettingsAsync(settings);
        Assert.Equal(settings, await _t.Playback.LoadSettingsAsync());

        var other = new PlaybackSettings(1f, false, LoopMode.RepeatQueue, 0);
        await _t.Playback.SaveSettingsAsync(other);
        Assert.Equal(other, await _t.Playback.LoadSettingsAsync());

        await AssertEx.ThrowsCode(ErrorCode.InvalidArgument, () => _t.Playback.SaveSettingsAsync(other with { Volume = 1.01f }));
        await AssertEx.ThrowsCode(ErrorCode.InvalidArgument, () => _t.Playback.SaveSettingsAsync(other with { Volume = float.NaN }));
        await AssertEx.ThrowsCode(ErrorCode.InvalidArgument, () => _t.Playback.SaveSettingsAsync(other with { CrossfadeSeconds = 12.5 }));
        await AssertEx.ThrowsCode(ErrorCode.InvalidArgument, () => _t.Playback.SaveSettingsAsync(other with { LoopMode = (LoopMode)9 }));
    }

    [Fact]
    public async Task Queue_round_trips_and_survives_a_new_database_instance()
    {
        var a = await _t.AddTrackAsync("a");
        var b = await _t.AddTrackAsync("b");
        QueueItemDto[] items = [new(Guid.NewGuid(), a.Id), new(Guid.NewGuid(), b.Id), new(Guid.NewGuid(), a.Id)];
        await _t.Playback.SaveQueueAsync(items, 2);

        // Simulate a service restart: a new SqliteDatabase over the same file.
        var reopened = new SqlitePlaybackStateRepository(new SqliteDatabase(_t.Database.DatabasePath));
        var loaded = await reopened.LoadQueueAsync();
        Assert.Equal(items, loaded.Items);
        Assert.Equal(2, loaded.CurrentIndex);

        await _t.Playback.SaveQueueAsync([items[1]], -1);
        loaded = await _t.Playback.LoadQueueAsync();
        Assert.Equal([items[1]], loaded.Items);
        Assert.Equal(-1, loaded.CurrentIndex);

        await _t.Playback.SaveQueueAsync([], -1);
        Assert.Empty((await _t.Playback.LoadQueueAsync()).Items);

        await AssertEx.ThrowsCode(ErrorCode.InvalidArgument, () => _t.Playback.SaveQueueAsync(items, 3));
        await AssertEx.ThrowsCode(ErrorCode.InvalidArgument, () => _t.Playback.SaveQueueAsync([items[0], items[0]], 0));
    }

    [Fact]
    public async Task Saving_skips_items_for_deleted_tracks_and_keeps_the_current_item()
    {
        var a = await _t.AddTrackAsync("a");
        var b = await _t.AddTrackAsync("b");
        QueueItemDto[] items = [new(Guid.NewGuid(), Guid.NewGuid()), new(Guid.NewGuid(), a.Id), new(Guid.NewGuid(), b.Id)];
        await _t.Playback.SaveQueueAsync(items, 2);
        var loaded = await _t.Playback.LoadQueueAsync();
        Assert.Equal([items[1], items[2]], loaded.Items);
        Assert.Equal(1, loaded.CurrentIndex);
    }
}

public sealed class SettingsAndAuditTests : IAsyncLifetime
{
    private readonly TestDb _t = new();

    public Task InitializeAsync() => _t.InitializeAsync();

    public Task DisposeAsync() => _t.DisposeAsync();

    [Fact]
    public async Task Settings_crud()
    {
        Assert.Null(await _t.Settings.GetAsync("remote.port"));
        await _t.Settings.SetAsync("remote.port", "8443");
        await _t.Settings.SetAsync("ui.theme", "dark");
        Assert.Equal("8443", await _t.Settings.GetAsync("remote.port"));
        await _t.Settings.SetAsync("remote.port", "9443");
        Assert.Equal("9443", await _t.Settings.GetAsync("remote.port"));
        Assert.Equal(new Dictionary<string, string> { ["remote.port"] = "9443", ["ui.theme"] = "dark" }, await _t.Settings.GetAllAsync());
        Assert.True(await _t.Settings.DeleteAsync("ui.theme"));
        Assert.False(await _t.Settings.DeleteAsync("ui.theme"));
        await AssertEx.ThrowsCode(ErrorCode.InvalidArgument, () => _t.Settings.SetAsync("", "x"));
        await AssertEx.ThrowsCode(ErrorCode.InvalidArgument, () => _t.Settings.GetAsync(new string('k', 201)));
    }

    [Fact]
    public async Task Audit_append_query_and_purge()
    {
        var t0 = _t.Clock.GetUtcNow();
        await _t.Audit.AppendAsync(new AuditEntry("admin (DESKTOP\\ram)", ClientKind.Desktop, "user.create", "user:asha", "role=Volunteer", t0));
        await _t.Audit.AppendAsync(new AuditEntry("device:iphone", ClientKind.Remote, "playback.pause", null, null, t0.AddMinutes(1)));
        await _t.Audit.AppendAsync(new AuditEntry("service", null, "service.start", null, new string('d', 5000), t0.AddMinutes(2)));

        var recent = await _t.Audit.QueryRecentAsync(10);
        Assert.Equal(["service.start", "playback.pause", "user.create"], recent.Select(r => r.Action));
        Assert.Equal(new AuditRecord(recent[2].Id, "admin (DESKTOP\\ram)", ClientKind.Desktop, "user.create", "user:asha", "role=Volunteer", t0), recent[2]);
        Assert.Null(recent[0].ClientKind);
        Assert.Equal(SqliteAuditLog.MaxDetailsLength, recent[0].Details!.Length);

        var older = await _t.Audit.QueryRecentAsync(10, beforeId: recent[0].Id);
        Assert.Equal(["playback.pause", "user.create"], older.Select(r => r.Action));
        Assert.Single(await _t.Audit.QueryRecentAsync(1));

        Assert.Equal(2, await _t.Audit.PurgeOlderThanAsync(t0.AddMinutes(2)));
        Assert.Single(await _t.Audit.QueryRecentAsync());
        await AssertEx.ThrowsCode(ErrorCode.InvalidArgument, () => _t.Audit.AppendAsync(new AuditEntry("", null, "x", null, null, t0)));
    }
}
