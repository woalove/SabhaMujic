using System.Collections.Concurrent;
using System.Security.Cryptography;
using SabhaMujic.Client;
using SabhaMujic.Contracts;
using SabhaMujic.Server.Application;

namespace SabhaMujic.Server.Tests;

public sealed class ImportTests : IAsyncLifetime
{
    private TestServer _server = null!;

    public async Task InitializeAsync() => _server = await TestServer.StartAsync();

    public async Task DisposeAsync() => await _server.DisposeAsync();

    private static async Task<Guid> UploadAsync(PipeSabhaClient client, string fileName, byte[] bytes, int chunk = 100_000)
    {
        var id = await client.Admin.BeginImportAsync(fileName, bytes.Length);
        for (var offset = 0; offset < bytes.Length; offset += chunk)
        {
            await client.Admin.UploadChunkAsync(id, offset, bytes[offset..Math.Min(bytes.Length, offset + chunk)]);
        }

        return id;
    }

    [Fact]
    public async Task Happy_path_stores_adds_and_queues_analysis()
    {
        var admin = await _server.ConnectAdminAsync();
        var watcher = await _server.ConnectVolunteerAsync();
        var progress = new ConcurrentQueue<ImportProgressDto>();
        var library = new ConcurrentQueue<LibraryChangedDto>();
        admin.Events.ImportProgressReceived += progress.Enqueue;
        watcher.Events.ImportProgressReceived += progress.Enqueue; // must not get another session's progress
        watcher.Events.LibraryChangeReceived += library.Enqueue;
        var category = await admin.Admin.CreateCategoryAsync("Kirtan");

        var bytes = RandomNumberGenerator.GetBytes(250_000);
        var id = await UploadAsync(admin, "C:\\music\\Jay Swaminarayan.MP3", bytes);
        var result = await admin.Admin.CompleteImportAsync(id, new ImportMetadata("", "Mandal", category.Id));

        Assert.False(result.WasDuplicate);
        var track = await admin.Library.GetTrackAsync(result.TrackId);
        Assert.Equal("Jay Swaminarayan", track.Title);
        Assert.Equal("Mandal", track.Artist);
        Assert.Equal(category.Id, track.CategoryId);
        Assert.Equal(123.5, track.DurationSeconds);
        Assert.Equal(AnalysisStatus.Pending, track.Analysis);
        Assert.Equal([result.TrackId], _server.AnalysisQueue.Queued);

        var file = await _server.App.Tracks.GetFileInfoAsync(result.TrackId);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)) + ".mp3", file!.FileKey);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(_server.Store.GetPhysicalPath(file.FileKey)));
        Assert.Equal(0, _server.Uploads.ActiveCount);
        Assert.Empty(Directory.EnumerateFiles(_server.Uploads.StagingDirectory));

        await AssertEx.Eventually(() => progress.Any(p => p.Stage == ImportService.StageDone), "Done progress");
        await AssertEx.Eventually(() => library.Any(c => c.Kind == LibraryChangeKind.TrackAdded && c.EntityId == result.TrackId), "TrackAdded");
        Assert.All(progress, p => Assert.Equal(id, p.UploadId));
        Assert.Equal(progress.Count, progress.Distinct().Count());
        Assert.Contains(progress, p => p.Stage == ImportService.StageUploading && p.BytesReceived == bytes.Length);
    }

    [Fact]
    public async Task Duplicate_content_returns_the_existing_track_and_keeps_one_file()
    {
        var admin = await _server.ConnectAdminAsync();
        var bytes = RandomNumberGenerator.GetBytes(50_000);
        var first = await admin.Admin.CompleteImportAsync(await UploadAsync(admin, "a.mp3", bytes), new ImportMetadata("A", null, null));
        var second = await admin.Admin.CompleteImportAsync(await UploadAsync(admin, "b.mp3", bytes), new ImportMetadata("B", null, null));
        var third = await admin.Admin.CompleteImportAsync(await UploadAsync(admin, "c.wav", bytes), new ImportMetadata("C", null, null));

        Assert.True(second.WasDuplicate);
        Assert.True(third.WasDuplicate);
        Assert.Equal(first.TrackId, second.TrackId);
        Assert.Equal(first.TrackId, third.TrackId);
        Assert.Equal(1, _server.StoredFileCount());
        Assert.Equal("A", (await admin.Library.GetTrackAsync(first.TrackId)).Title);
        Assert.Single(_server.AnalysisQueue.Queued);
    }

    [Fact]
    public async Task Unsupported_audio_stores_nothing()
    {
        var admin = await _server.ConnectAdminAsync();
        _server.Probe.Result = AudioProbeResult.Unsupported("not an MP3 stream");
        var id = await UploadAsync(admin, "fake.mp3", RandomNumberGenerator.GetBytes(10_000));

        var ex = await AssertEx.ThrowsCode(ErrorCode.UnsupportedFormat, () => admin.Admin.CompleteImportAsync(id, new ImportMetadata("x", null, null)));
        Assert.Contains("not an MP3 stream", ex.Message);
        Assert.Equal(0, _server.StoredFileCount());
        Assert.Equal(0, (await admin.Library.GetTracksAsync(new TrackQuery())).Total);
        Assert.Equal(0, _server.Uploads.ActiveCount);
        Assert.Empty(Directory.EnumerateFiles(_server.Uploads.StagingDirectory));
        Assert.Empty(_server.AnalysisQueue.Queued);
    }

    [Fact]
    public async Task Unknown_category_keeps_the_upload_for_a_retry()
    {
        var admin = await _server.ConnectAdminAsync();
        var id = await UploadAsync(admin, "a.flac", RandomNumberGenerator.GetBytes(1_000));
        await AssertEx.ThrowsCode(ErrorCode.NotFound, () => admin.Admin.CompleteImportAsync(id, new ImportMetadata("x", null, Guid.NewGuid())));
        Assert.Equal(1, _server.Uploads.ActiveCount);
        var result = await admin.Admin.CompleteImportAsync(id, new ImportMetadata("x", null, null));
        Assert.False(result.WasDuplicate);
    }

    [Fact]
    public async Task Volunteers_cannot_import()
    {
        var volunteer = await _server.ConnectVolunteerAsync();
        await AssertEx.ThrowsCode(ErrorCode.Forbidden, () => volunteer.Admin.BeginImportAsync("a.mp3", 10));
        Assert.Equal(0, _server.Uploads.ActiveCount);

        // Nor can they touch an admin's upload, even knowing its id.
        var admin = await _server.ConnectAdminAsync();
        var id = await admin.Admin.BeginImportAsync("a.mp3", 10);
        await AssertEx.ThrowsCode(ErrorCode.Forbidden, () => volunteer.Admin.UploadChunkAsync(id, 0, new byte[10]));
        await AssertEx.ThrowsCode(ErrorCode.Forbidden, () => volunteer.Admin.CancelImportAsync(id));
    }

    [Fact]
    public async Task Uploads_belong_to_the_session_that_began_them()
    {
        var admin = await _server.ConnectAdminAsync();
        var otherAdmin = await _server.ConnectAdminAsync(); // same user, different session
        var id = await admin.Admin.BeginImportAsync("a.mp3", 10);
        await AssertEx.ThrowsCode(ErrorCode.Forbidden, () => otherAdmin.Admin.UploadChunkAsync(id, 0, new byte[10]));
    }

    [Fact]
    public async Task Cancel_cleans_up_and_limits_are_enforced_over_the_pipe()
    {
        var admin = await _server.ConnectAdminAsync();
        var id = await admin.Admin.BeginImportAsync("a.mp3", 1_000);
        await admin.Admin.UploadChunkAsync(id, 0, new byte[500]);
        Assert.Single(Directory.EnumerateFiles(_server.Uploads.StagingDirectory));
        await admin.Admin.CancelImportAsync(id);
        Assert.Equal(0, _server.Uploads.ActiveCount);
        Assert.Empty(Directory.EnumerateFiles(_server.Uploads.StagingDirectory));
        await AssertEx.ThrowsCode(ErrorCode.NotFound, () => admin.Admin.UploadChunkAsync(id, 500, new byte[1]));

        await AssertEx.ThrowsCode(ErrorCode.UnsupportedFormat, () => admin.Admin.BeginImportAsync("setup.exe", 10));
        await AssertEx.ThrowsCode(ErrorCode.TooLarge, () => admin.Admin.BeginImportAsync("big.mp3", AdminLimits.MaxUploadBytes + 1));
        var big = await admin.Admin.BeginImportAsync("ok.mp3", AdminLimits.MaxChunkBytes * 2L);
        await admin.Admin.UploadChunkAsync(big, 0, new byte[AdminLimits.MaxChunkBytes]); // a full 1 MiB chunk fits the frame limit
        await AssertEx.ThrowsCode(ErrorCode.TooLarge, () => admin.Admin.UploadChunkAsync(big, AdminLimits.MaxChunkBytes, new byte[AdminLimits.MaxChunkBytes + 1]));
        await AssertEx.ThrowsCode(ErrorCode.InvalidArgument, () => admin.Admin.UploadChunkAsync(big, 5, new byte[1]));
        await AssertEx.ThrowsCode(ErrorCode.InvalidArgument, () => admin.Admin.CompleteImportAsync(big, new ImportMetadata("x", null, null)));
    }

    [Fact]
    public async Task Deleting_a_track_removes_its_file_and_tells_the_engine()
    {
        var admin = await _server.ConnectAdminAsync();
        var result = await admin.Admin.CompleteImportAsync(await UploadAsync(admin, "a.wma", RandomNumberGenerator.GetBytes(5_000)),
            new ImportMetadata("A", null, null));
        Assert.Equal(1, _server.StoredFileCount());
        await admin.Admin.DeleteTrackAsync(result.TrackId);
        Assert.Equal(0, _server.StoredFileCount());
        Assert.Contains($"TrackChanged {LibraryChangeKind.TrackRemoved}", _server.Backend.Commands);
        await AssertEx.ThrowsCode(ErrorCode.NotFound, () => admin.Library.GetTrackAsync(result.TrackId));
        await AssertEx.ThrowsCode(ErrorCode.NotFound, () => admin.Admin.DeleteTrackAsync(result.TrackId));
    }

    [Fact]
    public async Task Loop_override_round_trips_through_the_api()
    {
        var admin = await _server.ConnectAdminAsync();
        var result = await admin.Admin.CompleteImportAsync(await UploadAsync(admin, "a.m4a", RandomNumberGenerator.GetBytes(5_000)),
            new ImportMetadata("A", null, null));
        var track = await admin.Admin.SetLoopOverrideAsync(result.TrackId, new LoopRegionDto(10, 60, 0.25, 0.1, false));
        Assert.Equal(new LoopRegionDto(10, 60, 0.25, 1.0, true), track.Loop);
        Assert.Equal(track, await admin.Library.GetTrackAsync(result.TrackId));
        track = await admin.Admin.SetLoopOverrideAsync(result.TrackId, null);
        Assert.Null(track.Loop);
        await AssertEx.ThrowsCode(ErrorCode.InvalidArgument, () => admin.Admin.SetLoopOverrideAsync(result.TrackId, new LoopRegionDto(60, 10, 0, 0, true)));

        await admin.Admin.ReanalyzeTrackAsync(result.TrackId);
        Assert.Equal(2, _server.AnalysisQueue.Queued.Count(id => id == result.TrackId));
    }
}
