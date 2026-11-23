using System.Collections.Concurrent;
using System.Security.Cryptography;
using SabhaMujic.Client.Rpc;
using SabhaMujic.Contracts;
using SabhaMujic.Core.Security;
using SabhaMujic.Data.Repositories;

namespace SabhaMujic.Server.Tests;

/// <summary>
/// Contract v2 scenarios over the real pipe: open use on the desktop (no sign-in for the player) with administration
/// behind an admin sign-in, and category membership managed from the category side.
/// </summary>
public sealed class AccessV2Tests : IAsyncLifetime
{
    private TestServer _server = null!;

    public async Task InitializeAsync() => _server = await TestServer.StartAsync();

    public async Task DisposeAsync() => await _server.DisposeAsync();

    private async Task<Guid> AddTrackAsync(string title, params Guid[] categoryIds)
    {
        var hash = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        return (await _server.App.Tracks.AddAsync(new NewTrack(hash, hash + ".mp3", title, null, categoryIds, 60, 1))).Track.Id;
    }

    [Fact]
    public async Task Open_use_plays_queues_and_edits_playlists_but_every_admin_operation_is_denied_without_side_effects()
    {
        var category = await _server.App.Categories.CreateAsync("Bhajan");
        var a = await AddTrackAsync("A", category.Id);
        var b = await AddTrackAsync("B");
        var client = await _server.ConnectAsync(); // never signs in
        Assert.Null(client.Session);

        // The player: browse, queue, play, settings.
        Assert.Equal([a], (await client.Library.GetTracksAsync(new TrackQuery(CategoryId: category.Id))).Items.Select(t => t.Id));
        Assert.Equal(1, Assert.Single(await client.Library.GetCategoriesAsync()).TrackCount);
        var queue = await client.Playback.EnqueueAsync([a, b], playNext: false);
        await client.Playback.PlayQueueItemAsync(queue.Items[0].QueueItemId);
        await client.Playback.SetVolumeAsync(0.4f);
        await client.Playback.RemoveFromQueueAsync(queue.Items[1].QueueItemId);
        Assert.Single((await client.Playback.GetQueueAsync()).Items);

        // Playlists.
        var playlist = await client.Playlists.CreatePlaylistAsync("Sabha", [a]);
        await client.Playlists.SetPlaylistTracksAsync(playlist.Id, [b, a]);
        await client.Playlists.RenamePlaylistAsync(playlist.Id, "Sunday sabha");
        Assert.Equal([b, a], (await client.Playlists.GetPlaylistAsync(playlist.Id)).TrackIds);
        await client.Playback.EnqueuePlaylistAsync(playlist.Id, replaceQueue: true);

        // Every administrative operation: Unauthenticated, and nothing changes anywhere.
        var before = await _server.SnapshotAsync();
        var denied = 0;
        foreach (var api in RpcProtocol.ApiInterfaces)
        {
            foreach (var method in api.GetMethods().Where(m => Authorizer.RequiredLevel(OperationMap.For(api.Name, m.Name)) == AccessLevel.Admin))
            {
                var error = await AuthorizationMatrixTests.InvokeAsync(client, api, method);
                Assert.True(error?.Code == ErrorCode.Unauthenticated, $"{api.Name}.{method.Name}: {error?.Code.ToString() ?? "allowed"}");
                denied++;
            }
        }

        await Task.Delay(50);
        Assert.Equal(before, await _server.SnapshotAsync());
        Assert.True(denied >= 20, $"only {denied} admin operations");
        Assert.Null(client.Session);
    }

    [Fact]
    public async Task Setting_a_categorys_tracks_replaces_its_membership_keeps_other_tags_and_raises_events()
    {
        var admin = await _server.ConnectAdminAsync();
        var bhajan = await admin.Admin.CreateCategoryAsync("Bhajan");
        var kirtan = await admin.Admin.CreateCategoryAsync("Kirtan");
        var t1 = await AddTrackAsync("One", bhajan.Id, kirtan.Id);
        var t2 = await AddTrackAsync("Two", bhajan.Id);
        var t3 = await AddTrackAsync("Three");
        var watcher = await _server.ConnectAsync(); // open use sees library changes too
        var seen = new ConcurrentQueue<LibraryChangedDto>();
        watcher.Events.LibraryChangeReceived += seen.Enqueue;
        var backendBefore = _server.Backend.Commands.Count;

        var updated = await admin.Admin.SetCategoryTracksAsync(bhajan.Id, [t2, t3, t3]);

        Assert.Equal((bhajan.Id, "Bhajan", 2), (updated.Id, updated.Name, updated.TrackCount));
        Assert.Equal([kirtan.Id], (await admin.Library.GetTrackAsync(t1)).CategoryIds); // its other tag is kept
        Assert.Equal([bhajan.Id], (await admin.Library.GetTrackAsync(t2)).CategoryIds);
        Assert.Equal([bhajan.Id], (await admin.Library.GetTrackAsync(t3)).CategoryIds);
        Assert.Equal([t3, t2], (await watcher.Library.GetTracksAsync(new TrackQuery(CategoryId: bhajan.Id))).Items.Select(t => t.Id));
        Assert.Equal([2, 1], (await watcher.Library.GetCategoriesAsync()).Select(c => c.TrackCount));

        await AssertEx.Eventually(() => seen.Any(c => c.Kind == LibraryChangeKind.CategoriesChanged), "categories changed");
        await AssertEx.Eventually(() => seen.Count(c => c.Kind == LibraryChangeKind.TrackUpdated) == 2, "one update per changed track");
        Assert.Equal(new HashSet<Guid?> { t1, t3 }, seen.Where(c => c.Kind == LibraryChangeKind.TrackUpdated).Select(c => c.EntityId).ToHashSet());
        Assert.Equal(backendBefore, _server.Backend.Commands.Count); // playback is not involved
        Assert.Contains(await _server.App.AuditLog.QueryRecentAsync(5), e => e.Action == "category.set_tracks");

        // An empty list clears the category; the tracks keep their other tags.
        Assert.Equal(0, (await admin.Admin.SetCategoryTracksAsync(bhajan.Id, [])).TrackCount);
        Assert.Equal([kirtan.Id], (await admin.Library.GetTrackAsync(t1)).CategoryIds);
        Assert.Empty((await admin.Library.GetTrackAsync(t2)).CategoryIds);
    }

    [Fact]
    public async Task Setting_a_categorys_tracks_validates_everything_before_changing_anything()
    {
        var admin = await _server.ConnectAdminAsync();
        var bhajan = await admin.Admin.CreateCategoryAsync("Bhajan");
        var t1 = await AddTrackAsync("One", bhajan.Id);
        var before = await _server.SnapshotAsync();

        await AssertEx.ThrowsCode(ErrorCode.NotFound, () => admin.Admin.SetCategoryTracksAsync(Guid.NewGuid(), [t1]));
        await AssertEx.ThrowsCode(ErrorCode.InvalidArgument, () => admin.Admin.SetCategoryTracksAsync(bhajan.Id, [Guid.NewGuid()]));
        await AssertEx.ThrowsCode(ErrorCode.InvalidArgument, () => admin.Admin.SetCategoryTracksAsync(bhajan.Id, [t1, Guid.NewGuid()]));
        var anonymous = await _server.ConnectAsync();
        await AssertEx.ThrowsCode(ErrorCode.Unauthenticated, () => anonymous.Admin.SetCategoryTracksAsync(bhajan.Id, []));
        var volunteer = await _server.ConnectVolunteerAsync();
        await AssertEx.ThrowsCode(ErrorCode.Forbidden, () => volunteer.Admin.SetCategoryTracksAsync(bhajan.Id, []));

        await Task.Delay(20);
        Assert.Equal(StripAudit(before), StripAudit(await _server.SnapshotAsync()));
        Assert.Equal([bhajan.Id], (await admin.Library.GetTrackAsync(t1)).CategoryIds);
    }

    [Fact]
    public async Task Deleting_a_category_unlinks_its_tracks_and_import_takes_several_tags()
    {
        var admin = await _server.ConnectAdminAsync();
        var bhajan = await admin.Admin.CreateCategoryAsync("Bhajan");
        var kirtan = await admin.Admin.CreateCategoryAsync("Kirtan");
        var id = await admin.Admin.BeginImportAsync("song.mp3", 1_000);
        await admin.Admin.UploadChunkAsync(id, 0, RandomNumberGenerator.GetBytes(1_000));
        var imported = await admin.Admin.CompleteImportAsync(id, new ImportMetadata("Song", null, [kirtan.Id, bhajan.Id, kirtan.Id]));
        Assert.Equal([bhajan.Id, kirtan.Id], (await admin.Library.GetTrackAsync(imported.TrackId)).CategoryIds); // category order, no duplicates

        await admin.Admin.DeleteCategoryAsync(bhajan.Id);

        Assert.Equal([kirtan.Id], (await admin.Library.GetTrackAsync(imported.TrackId)).CategoryIds);
        Assert.Equal(1, Assert.Single(await admin.Library.GetCategoriesAsync()).TrackCount);
        var renamed = await admin.Admin.UpdateTrackAsync(new TrackUpdateDto(imported.TrackId, "Song", null, []));
        Assert.Empty(renamed.CategoryIds);
        Assert.Equal(0, Assert.Single(await admin.Library.GetCategoriesAsync()).TrackCount);
    }

    private static string StripAudit(string snapshot) =>
        string.Join('\n', snapshot.Split('\n').Where(line => !line.StartsWith("audit_log:", StringComparison.Ordinal) && !line.StartsWith("sessions:", StringComparison.Ordinal)));
}
