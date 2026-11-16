using Microsoft.Data.Sqlite;
using SabhaMujic.Contracts;
using SabhaMujic.Data.Repositories;
using SabhaMujic.Reliability;

namespace SabhaMujic.Server.Tests.Reliability;

/// <summary>
/// M6 fault injection against the real service: broken and vanished files in the queue, audio device loss, a locked
/// database, and stopping the service in the middle of an import. Each test runs its own host on a temp data root.
/// </summary>
public sealed class FaultInjectionTests
{
    [Fact]
    public async Task Corrupt_and_truncated_files_in_the_middle_of_the_queue_are_skipped_with_an_error_and_playback_continues()
    {
        await using var h = await ReliabilityHost.StartAsync(speed: 10);
        var a = await h.ImportAsync(SoakWav.Through(4, 1), "A");
        var corrupt = await h.ImportAsync(SoakWav.Through(4, 2), "Corrupt");
        var truncated = await h.ImportAsync(SoakWav.Through(8, 3), "Truncated");
        var d = await h.ImportAsync(SoakWav.Loopable(30, 4), "D");
        await h.WaitForAnalysisAsync();

        // Behind the database's back: garbage over one file, the other cut to 40 % of what its header promises.
        await File.WriteAllBytesAsync(await h.StoredPathAsync(corrupt), SoakWav.Garbage(50_000, 9));
        var truncatedPath = await h.StoredPathAsync(truncated);
        var bytes = await File.ReadAllBytesAsync(truncatedPath);
        await File.WriteAllBytesAsync(truncatedPath, bytes[..(44 + (int)((bytes.Length - 44) * 0.4) / 2 * 2)]);
        h.Host.Resolver.Invalidate(corrupt);
        h.Host.Resolver.Invalidate(truncated);

        var states = new StateLog(h.Admin);
        await h.Admin.Playback.SetCrossfadeAsync(0);
        var queue = await h.Admin.Playback.EnqueueAsync([a, corrupt, truncated, d], playNext: false);
        await h.Admin.Playback.PlayQueueItemAsync(queue.Items[0].QueueItemId);

        await AssertEx.Eventually(() => h.Host.Engine.GetState() is { Status: PlaybackStatus.Playing } s && s.CurrentTrackId == d, "D playing", 20_000);
        Assert.True(states.Any(s => s.ErrorMessage?.Contains(corrupt.ToString(), StringComparison.Ordinal) == true),
            "clients were told the corrupt track was skipped");
        Assert.True(states.Any(s => s.CurrentTrackId == truncated && s.Status == PlaybackStatus.Playing), "the truncated file played as far as it goes");
        Assert.DoesNotContain(states.Last!.ErrorMessage ?? string.Empty, "/"); // never a file path

        // Still healthy afterwards: D keeps playing and the position advances.
        var before = h.Host.Engine.GetState().PositionSeconds;
        await AssertEx.Eventually(() => h.Host.Engine.GetState().PositionSeconds > before + 0.5, "position advances");
        Assert.Empty(h.UnexpectedErrors());
    }

    [Fact]
    public async Task A_file_deleted_behind_the_database_is_skipped_and_the_next_item_plays()
    {
        await using var h = await ReliabilityHost.StartAsync(speed: 10);
        var a = await h.ImportAsync(SoakWav.Through(4, 11), "A");
        var gone = await h.ImportAsync(SoakWav.Through(4, 12), "Gone");
        var c = await h.ImportAsync(SoakWav.Loopable(30, 13), "C");
        await h.WaitForAnalysisAsync();
        File.Delete(await h.StoredPathAsync(gone));

        var states = new StateLog(h.Admin);
        await h.Admin.Playback.SetCrossfadeAsync(2);
        var queue = await h.Admin.Playback.EnqueueAsync([a, gone, c], playNext: false);
        await h.Admin.Playback.PlayQueueItemAsync(queue.Items[0].QueueItemId);

        await AssertEx.Eventually(() => h.Host.Engine.GetState() is { Status: PlaybackStatus.Playing } s && s.CurrentTrackId == c, "C playing", 20_000);
        Assert.True(states.Any(s => s.ErrorMessage?.Contains(gone.ToString(), StringComparison.Ordinal) == true));
        Assert.False(states.Any(s => s.CurrentTrackId == gone && s.Status == PlaybackStatus.Playing));

        Assert.Empty(h.UnexpectedErrors());
    }

    [Fact]
    public async Task Asking_for_an_unplayable_track_keeps_the_current_track_playing()
    {
        await using var h = await ReliabilityHost.StartAsync(speed: 5);
        var good = await h.ImportAsync(SoakWav.Loopable(60, 14), "Good");
        var gone = await h.ImportAsync(SoakWav.Through(4, 15), "Gone");
        await h.WaitForAnalysisAsync();
        File.Delete(await h.StoredPathAsync(gone));
        await h.Admin.Playback.EnqueueAsync([good], playNext: false);
        await h.Admin.Playback.ResumeAsync();
        await AssertEx.Eventually(() => h.Host.Engine.GetState() is { Status: PlaybackStatus.Playing } s && s.CurrentTrackId == good, "good playing");

        // A volunteer taps the broken track (loop mode Off, nothing after it in the queue).
        await h.Admin.Playback.PlayTrackAsync(gone);
        await h.Host.Engine.SyncAsync();

        var state = h.Host.Engine.GetState();
        Assert.Equal((PlaybackStatus.Playing, (Guid?)good), (state.Status, state.CurrentTrackId));
        Assert.DoesNotContain("None of the queued tracks", state.ErrorMessage ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Audio_device_loss_is_reported_to_clients_and_cleared_on_restore_while_playback_carries_on()
    {
        await using var h = await ReliabilityHost.StartAsync(speed: 5);
        var track = await h.ImportAsync(SoakWav.Loopable(40, 21), "Loop");
        var volunteer = await ConnectVolunteerAsync(h);
        var states = new StateLog(volunteer);
        await h.Admin.Playback.EnqueueAsync([track], playNext: false);
        await h.Admin.Playback.SetLoopModeAsync(LoopMode.SmartLoop);
        await h.Admin.Playback.ResumeAsync();
        await AssertEx.Eventually(() => h.Host.Engine.GetState().Status == PlaybackStatus.Playing, "playing");

        h.Output.SimulateDeviceLost("unplugged");
        await AssertEx.Eventually(() => states.Last?.ErrorMessage?.Contains("device lost", StringComparison.OrdinalIgnoreCase) == true, "device loss pushed");
        Assert.Equal(PlaybackStatus.Playing, states.Last!.Status);
        var position = h.Host.Engine.GetState().PositionSeconds;
        await AssertEx.Eventually(() => h.Host.Engine.GetState().PositionSeconds != position, "engine keeps running");

        // A second loss while already lost, then one restore, clears it.
        h.Output.SimulateDeviceLost("still unplugged");
        h.Output.SimulateDeviceRestored("plugged back in");
        await AssertEx.Eventually(() => states.Last is { ErrorMessage: null, Status: PlaybackStatus.Playing }, "device error cleared");
        Assert.Null((await volunteer.Playback.GetStateAsync()).ErrorMessage);
    }

    [Fact]
    public async Task A_database_locked_past_the_busy_timeout_fails_writes_cleanly_retries_saves_and_playback_never_stalls()
    {
        await using var h = await ReliabilityHost.StartAsync(speed: 5);
        var tracks = new[] { await h.ImportAsync(SoakWav.Loopable(40, 31), "One"), await h.ImportAsync(SoakWav.Through(20, 32), "Two") };
        await h.Admin.Playback.EnqueueAsync(tracks, playNext: false);
        await h.Admin.Playback.SetLoopModeAsync(LoopMode.RepeatQueue);
        await h.Admin.Playback.ResumeAsync();
        await AssertEx.Eventually(() => h.Host.Engine.GetState().Status == PlaybackStatus.Playing, "playing");

        // Held for 8 s: longer than the single 5 s busy timeout (busy_timeout pragma = command timeout, R-6). A client write
        // fails after about 5 s with Internal and the "storage busy" message (R-7); the debounced playback-state save fails
        // once (logged once) and is retried by itself after the lock is gone (BUG-01).
        var held = System.Diagnostics.Stopwatch.StartNew();
        Task playlist;
        using (var locker = await LockDatabaseAsync(h))
        {
            var enqueue = h.Admin.Playback.EnqueueAsync([tracks[1]], playNext: false); // engine now, debounced save later
            playlist = h.Admin.Playlists.CreatePlaylistAsync("During lock", tracks);   // direct DB write
            var positions = new List<double>();
            while (held.Elapsed < TimeSpan.FromSeconds(8))
            {
                positions.Add(h.Host.Engine.GetState().PositionSeconds);
                await Task.Delay(100);
            }

            Assert.True(h.Host.Engine.GetState().Status is PlaybackStatus.Playing or PlaybackStatus.Crossfading);
            Assert.True(positions.Distinct().Count() > 30, "the engine kept advancing during the lock");
            Assert.True(enqueue.IsCompletedSuccessfully, "queue edits do not wait for the database");
            var failure = await Assert.ThrowsAsync<SabhaMujicException>(() => playlist);
            Assert.Equal((ErrorCode.Internal, ServiceMessages.StorageUnavailable), (failure.Code, failure.Message));
            Assert.InRange(held.Elapsed, TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(9));
        }

        await AssertEx.Eventually(() => PersistedTrackIds(h).SequenceEqual(h.Host.Engine.GetQueue().Items.Select(i => i.TrackId)), "queue saved", 15_000);
        Assert.Single(h.Logs.Entries, l => l.Message.StartsWith("Saving the playback state failed", StringComparison.Ordinal));
        await h.Admin.Playlists.CreatePlaylistAsync("After lock", tracks); // the same write works once the lock is gone
        Assert.Empty(h.UnexpectedErrors("Saving the playback state failed"));
    }

    [Fact]
    public async Task A_failed_playback_state_save_is_retried_after_the_lock_is_released()
    {
        await using var h = await ReliabilityHost.StartAsync(speed: 5);
        var track = await h.ImportAsync(SoakWav.Loopable(40, 51), "One");
        await h.Admin.Playback.EnqueueAsync([track], playNext: false);
        await AssertEx.Eventually(() => PersistedTrackIds(h).Count == 1, "first save");

        using (var locker = await LockDatabaseAsync(h))
        {
            await h.Admin.Playback.EnqueueAsync([track], playNext: false);
            await AssertEx.Eventually(() => h.Logs.Entries.Any(l => l.Message.StartsWith("Saving the playback state failed", StringComparison.Ordinal)),
                "failed save logged", 45_000);
            Assert.Equal(2, h.Host.Engine.GetQueue().Items.Count);
            Assert.True(h.Host.Persistence!.IsDirty);
        }

        // No further change: the queue of two must still reach the database (a crash now would otherwise lose it).
        await AssertEx.Eventually(() => PersistedTrackIds(h).Count == 2, "failed save retried", 15_000);
    }

    [Fact]
    public async Task Stopping_the_service_during_an_import_leaves_no_partial_track_and_the_restart_is_clean()
    {
        await using var h = await ReliabilityHost.StartAsync(speed: 10);
        var keep = await h.ImportAsync(SoakWav.Through(5, 61), "Keep");
        var big = SoakWav.Through(120, 62); // ~11 MB: several chunks

        // 1. Mid-upload: half the chunks are in when the service stops.
        var uploadId = await h.Admin.Admin.BeginImportAsync("big.wav", big.Length);
        for (var offset = 0; offset < big.Length / 2; offset += AdminLimits.MaxChunkBytes)
        {
            await h.Admin.Admin.UploadChunkAsync(uploadId, offset, big[offset..Math.Min(big.Length, offset + AdminLimits.MaxChunkBytes)]);
        }

        var stop = h.Host.StopAsync();
        Assert.Same(stop, await Task.WhenAny(stop, Task.Delay(15_000)));
        await h.Host.DisposeAsync();
        await h.StartHostAsync();
        Assert.Equal(0, h.Host.Uploads.ActiveCount);
        Assert.Empty(Directory.EnumerateFiles(h.Host.Paths.Uploads));
        await AssertConsistentAsync(h, expectedTracks: 1);

        // 2. During completion (probe, store, insert): whatever the race, the library stays consistent.
        for (var round = 0; round < 3; round++)
        {
            var admin = await h.ConnectAsync(ReliabilityHost.AdminUser, ReliabilityHost.AdminSecret);
            var bytes = SoakWav.Through(60 + round, 70 + round);
            var id = await admin.Admin.BeginImportAsync($"race{round}.wav", bytes.Length);
            for (var offset = 0; offset < bytes.Length; offset += AdminLimits.MaxChunkBytes)
            {
                await admin.Admin.UploadChunkAsync(id, offset, bytes[offset..Math.Min(bytes.Length, offset + AdminLimits.MaxChunkBytes)]);
            }

            var complete = admin.Admin.CompleteImportAsync(id, new ImportMetadata($"Race {round}", null, null));
            await Task.Delay(round * 15);
            stop = h.Host.StopAsync();
            Assert.Same(stop, await Task.WhenAny(stop, Task.Delay(15_000)));
            try
            {
                await complete;
            }
            catch (SabhaMujicException ex)
            {
                Assert.True(Enum.IsDefined(ex.Code), $"defined error code, got {ex.Code}");
            }

            await h.Host.DisposeAsync();
            await h.StartHostAsync();
            await AssertConsistentAsync(h, expectedTracks: null);
        }

        Assert.NotNull(await h.App.Tracks.GetAsync(keep));
    }

    // ------------------------------------------------------------------ helpers

    private static async Task<SabhaMujic.Client.PipeSabhaClient> ConnectVolunteerAsync(ReliabilityHost h)
    {
        await h.CreateVolunteersAsync(1);
        return await h.ConnectVolunteerAsync(0);
    }

    private static List<Guid> PersistedTrackIds(ReliabilityHost h) =>
        new SqlitePlaybackStateRepository(h.Host.Database).LoadQueueAsync().GetAwaiter().GetResult().Items.Select(i => i.TrackId).ToList();

    /// <summary>Every track has its file and every stored file belongs to a track; nothing left in staging.</summary>
    private static async Task AssertConsistentAsync(ReliabilityHost h, int? expectedTracks)
    {
        var tracks = (await h.App.Tracks.QueryAsync(new TrackQuery(Take: 1000))).Items;
        var keys = new List<string>();
        foreach (var track in tracks)
        {
            var info = await h.App.Tracks.GetFileInfoAsync(track.Id);
            Assert.NotNull(info);
            Assert.True(h.Host.Store.Exists(info.FileKey), $"track {track.Title} has its file");
            keys.Add(info.FileKey);
        }

        var stored = Directory.EnumerateFiles(h.Host.Store.RootDirectory, "*", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Data.Storage.ProtectedStore.StagingFolderName, StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToList();
        Assert.Equal(keys.Order(), stored.Order()!);
        if (expectedTracks is { } expected)
        {
            Assert.Equal(expected, tracks.Count);
        }
    }

    /// <summary>Takes the database's write lock from a second connection (as another process or a backup tool would).</summary>
    private static async Task<DatabaseLock> LockDatabaseAsync(ReliabilityHost h)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = h.Host.Database.DatabasePath, Pooling = false }.ToString());
        await connection.OpenAsync();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "BEGIN EXCLUSIVE;";
            await command.ExecuteNonQueryAsync();
        }

        return new DatabaseLock(connection);
    }

    private sealed class DatabaseLock(SqliteConnection connection) : IDisposable
    {
        private bool _released;

        public void Release()
        {
            if (_released)
            {
                return;
            }

            _released = true;
            using var command = connection.CreateCommand();
            command.CommandText = "ROLLBACK;";
            command.ExecuteNonQuery();
        }

        public void Dispose()
        {
            Release();
            connection.Dispose();
        }
    }
}
