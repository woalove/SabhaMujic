using System.Collections.Concurrent;
using SabhaMujic.Contracts;

namespace SabhaMujic.Server.Tests.Integration;

/// <summary>
/// The whole service on Linux: real <c>SabhaHost</c>, real <c>PlaybackEngine</c>, WAV decoder, accelerated null output,
/// real pipe clients. Covers first-run setup, import + analysis, crossfade, Smart Loop, disconnect continuity,
/// reconnect, restart with restored queue/settings, deleting the playing track, and a corrupt import.
/// </summary>
public sealed class EndToEndTests
{
    private const string AdminPin = "admin-pin";

    [Fact]
    public async Task Full_service_flow_on_the_real_engine()
    {
        await using var h = await HostHarness.StartAsync(speed: 10);

        // 1. First-run setup through the (elevated) desktop pipe.
        var admin = await h.ConnectAsync();
        Assert.True(await admin.Auth.IsSetupRequiredAsync());
        await admin.Auth.CompleteInitialSetupAsync("admin", AdminPin);

        // 2. Import two generated WAVs over the pipe.
        var tone = await HostHarness.ImportAsync(admin, "tone.wav", TestWav.Tone(seconds: 20, hz: 440), "Tone");
        var song = await HostHarness.ImportAsync(admin, "song.wav", TestWav.Song(), "Song");
        Assert.False(tone.WasDuplicate);
        Assert.False(song.WasDuplicate);
        Assert.Equal(2, h.StoredFileCount());

        // 3. Background analysis becomes Ready (trims + loop), and the duration is the decoded length.
        await AssertEx.Eventually(() => Ready(admin, tone.TrackId) && Ready(admin, song.TrackId), "analysis Ready", 60_000);
        var songDto = await admin.Library.GetTrackAsync(song.TrackId);
        Assert.NotNull(songDto.Loop);
        Assert.InRange(songDto.DurationSeconds, 39.9, 40.1);
        var toneAnalysis = await h.App.Analysis.GetAsync(tone.TrackId);
        Assert.InRange(toneAnalysis!.TrimStartSeconds!.Value, 0.3, 0.7);

        // 4. A volunteer plays without signing in (open use): Playing, then a crossfade into the second track.
        var volunteer = await h.ConnectAsync();
        var statuses = new ConcurrentQueue<(PlaybackStatus Status, Guid? Track)>();
        volunteer.Events.PlaybackStateReceived += s => statuses.Enqueue((s.Status, s.CurrentTrackId));
        var queue = await volunteer.Playback.EnqueueAsync([tone.TrackId, song.TrackId], playNext: false);
        await volunteer.Playback.PlayQueueItemAsync(queue.Items[0].QueueItemId);
        await AssertEx.Eventually(() => statuses.Any(s => s is { Status: PlaybackStatus.Playing } && s.Track == tone.TrackId), "tone playing");
        await AssertEx.Eventually(() => Current(h) == (PlaybackStatus.Playing, song.TrackId), "song playing after the tone", 20_000);
        Assert.Contains(statuses, s => s.Status == PlaybackStatus.Crossfading);

        // 5. Smart Loop keeps the song going well past its end (40 s of audio = 4 s here).
        await volunteer.Playback.SetLoopModeAsync(LoopMode.SmartLoop);
        await volunteer.Playback.SetVolumeAsync(0.5f);
        var positions = new List<double>();
        for (var i = 0; i < 60; i++)
        {
            var state = h.Host.Engine.GetState();
            Assert.Equal((PlaybackStatus.Playing, song.TrackId), (state.Status, state.CurrentTrackId));
            positions.Add(state.PositionSeconds);
            await Task.Delay(100);
        }

        Assert.Contains(positions.Zip(positions.Skip(1)), p => p.Second < p.First); // wrapped at the loop seam

        // 6. Disconnect continuity: every client vanishes, the engine keeps playing.
        await h.DisposeClientsAsync();
        await AssertEx.Eventually(() => h.Host.Pipe!.ConnectionCount == 0, "connections closed");
        var before = h.Host.Engine.GetState();
        await Task.Delay(500);
        var after = h.Host.Engine.GetState();
        Assert.Equal(PlaybackStatus.Playing, after.Status);
        Assert.Equal(song.TrackId, after.CurrentTrackId);
        Assert.NotEqual(before.PositionSeconds, after.PositionSeconds);

        // 7. A new client sees the live state.
        var observer = await h.ConnectAsync();
        var live = await observer.Playback.GetStateAsync();
        Assert.Equal((PlaybackStatus.Playing, song.TrackId, LoopMode.SmartLoop, 0.5f), (live.Status, live.CurrentTrackId, live.LoopMode, live.Volume));
        Assert.Equal(h.App.ServerEpoch, live.ServerEpoch);

        // 8. Restart on the same data root: queue (same item ids, current item) and settings come back; playback starts stopped.
        var queueBeforeRestart = await observer.Playback.GetQueueAsync();
        var epoch = h.App.ServerEpoch;
        await h.RestartAsync();
        Assert.NotEqual(epoch, h.App.ServerEpoch);
        admin = await h.ConnectAsync("admin", AdminPin);
        var restoredQueue = await admin.Playback.GetQueueAsync();
        Assert.Equal([tone.TrackId, song.TrackId], restoredQueue.Items.Select(i => i.TrackId));
        Assert.Equal(queueBeforeRestart.Items, restoredQueue.Items);
        Assert.Equal(1, restoredQueue.CurrentIndex);
        var restored = await admin.Playback.GetStateAsync();
        Assert.Equal((LoopMode.SmartLoop, 0.5f, 6.0), (restored.LoopMode, restored.Volume, restored.CrossfadeSeconds)); // persisted defaults win over engine options
        Assert.True(restored.Status is PlaybackStatus.Idle or PlaybackStatus.Stopped);
        Assert.Equal(song.TrackId, restored.CurrentTrackId);

        // 9. Deleting the playing track: the queue skips to the next item and the file leaves the store.
        await admin.Playback.SetLoopModeAsync(LoopMode.Off);
        queue = await admin.Playback.EnqueueAsync([tone.TrackId], playNext: false); // tone, song, tone
        await admin.Playback.PlayQueueItemAsync(queue.Items[1].QueueItemId);
        await AssertEx.Eventually(() => Current(h) == (PlaybackStatus.Playing, song.TrackId), "song playing again");
        var songPath = h.Host.Store.GetPhysicalPath((await h.App.Tracks.GetFileInfoAsync(song.TrackId))!.FileKey);
        Assert.True(File.Exists(songPath));

        var crossfades = new System.Collections.Concurrent.ConcurrentQueue<double>();
        void OnState(object? sender, PlaybackStateDto s) => crossfades.Enqueue(s.CrossfadeSeconds);
        h.Host.Engine.StateChanged += OnState;
        await admin.Admin.DeleteTrackAsync(song.TrackId);
        h.Host.Engine.StateChanged -= OnState;

        // R-9: the hard cut does not touch the crossfade setting (nothing ever pushes 0 to clients).
        Assert.All(crossfades, c => Assert.Equal(6.0, c));
        Assert.Equal(6.0, (await admin.Playback.GetStateAsync()).CrossfadeSeconds);
        Assert.False(File.Exists(songPath));
        Assert.Equal(1, h.StoredFileCount());
        Assert.DoesNotContain(song.TrackId, (await admin.Playback.GetQueueAsync()).Items.Select(i => i.TrackId));
        await AssertEx.Eventually(() => Current(h) == (PlaybackStatus.Playing, tone.TrackId), "skipped to the next item");

        // 10. A corrupt "WAV" is rejected by decoding and nothing is stored.
        var tracksBefore = (await admin.Library.GetTracksAsync(new TrackQuery())).Total;
        var ex = await Assert.ThrowsAsync<SabhaMujicException>(() => HostHarness.ImportAsync(admin, "broken.wav", TestWav.Garbage(), "Broken"));
        Assert.Equal(ErrorCode.UnsupportedFormat, ex.Code);
        Assert.Equal(1, h.StoredFileCount());
        Assert.Equal(tracksBefore, (await admin.Library.GetTracksAsync(new TrackQuery())).Total);
        Assert.Equal(0, h.Host.Uploads.ActiveCount);
    }

    private static (PlaybackStatus, Guid?) Current(HostHarness h)
    {
        var state = h.Host.Engine.GetState();
        return (state.Status, state.CurrentTrackId);
    }

    private static bool Ready(Client.PipeSabhaClient client, Guid trackId) =>
        client.Library.GetTrackAsync(trackId).GetAwaiter().GetResult().Analysis == AnalysisStatus.Ready;
}
