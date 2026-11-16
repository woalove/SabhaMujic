using SabhaMujic.Contracts;
using SabhaMujic.Desktop.Tests.Fakes;
using SabhaMujic.Desktop.ViewModels;
using SabhaMujic.Desktop.ViewModels.Infrastructure;
using static SabhaMujic.Desktop.Tests.TestHelpers;

namespace SabhaMujic.Desktop.Tests;

public class TrackBatchLookupTests
{
    private readonly FakeSabhaClient _client = new();

    [Fact]
    public async Task Many_tracks_are_fetched_in_batches_of_at_most_500()
    {
        var tracks = Enumerable.Range(0, 1_200).Select(i => _client.Library.AddTrack("T" + i)).ToList();
        var cache = new TrackInfoCache(_client);

        var result = await cache.GetManyAsync(tracks.Select(t => t.Id).Concat([tracks[0].Id]));

        Assert.Equal(1_200, result.Count);
        Assert.All(tracks, t => Assert.Equal(t, result[t.Id]));
        var calls = _client.CallsTo("GetTracksByIdsAsync").Select(c => (IReadOnlyList<Guid>)c.Args[0]!).ToList();
        Assert.Equal([500, 500, 200], calls.Select(c => c.Count));
        Assert.Empty(_client.CallsTo("GetTrackAsync"));

        // Everything is cached now.
        await cache.GetManyAsync(tracks.Take(10).Select(t => t.Id));
        Assert.Equal(3, _client.CallsTo("GetTracksByIdsAsync").Count());
        Assert.True(cache.TryGet(tracks[5].Id, out var cached));
        Assert.Equal(tracks[5], cached);
    }

    [Fact]
    public async Task Removed_tracks_map_to_null_and_are_remembered()
    {
        var kept = _client.Library.AddTrack("Kept");
        var gone = Guid.NewGuid();
        var cache = new TrackInfoCache(_client);

        var result = await cache.GetManyAsync([kept.Id, gone]);

        Assert.Equal(kept, result[kept.Id]);
        Assert.Null(result[gone]);
        Assert.Null(await cache.GetAsync(gone)); // no extra request
        Assert.Empty(_client.CallsTo("GetTrackAsync"));
    }

    [Fact]
    public async Task Concurrent_requests_share_one_lookup()
    {
        var track = _client.Library.AddTrack("Shared");
        var cache = new TrackInfoCache(_client);

        var first = cache.GetManyAsync([track.Id]);
        var single = cache.GetAsync(track.Id);
        Assert.Equal(track, (await first)[track.Id]);
        Assert.Equal(track, await single);
        Assert.Single(_client.CallsTo("GetTracksByIdsAsync"));
        Assert.Empty(_client.CallsTo("GetTrackAsync"));
    }

    [Fact]
    public async Task Playlist_rows_resolve_with_one_batched_lookup()
    {
        var a = _client.Library.AddTrack("Aarti");
        var b = _client.Library.AddTrack("Bhajan");
        var playlist = new PlaylistDto(Guid.NewGuid(), "Sunday", [a.Id, b.Id, a.Id, Guid.NewGuid()], DateTimeOffset.UtcNow);
        _client.Playlists.Items[playlist.Id] = playlist;
        var ui = new TestUi();
        var vm = new PlaylistsViewModel(_client, ui.Services, new TrackInfoCache(_client));
        await vm.LoadAsync();

        vm.SelectedPlaylist = vm.Playlists.Single();
        await AssertEventuallyAsync(() => vm.Tracks.Count == 4 && vm.Tracks.All(t => t.Title != "…"));

        Assert.Equal(["Aarti", "Bhajan", "Aarti", "Unknown track (removed)"], vm.Tracks.Select(t => t.Title));
        Assert.Single(_client.CallsTo("GetTracksByIdsAsync"));
        Assert.Empty(_client.CallsTo("GetTrackAsync"));
    }

    private static async Task AssertEventuallyAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }
}
