using SabhaMujic.Audio.Output;
using SabhaMujic.Contracts;
using SabhaMujic.Data.Repositories;
using SabhaMujic.Server.Hosting;

namespace SabhaMujic.Server.Tests.Integration;

/// <summary>
/// Graceful stop and restore with a manually pumped output (no wall clock in the audio path): the final save holds the
/// item and position where the audio stopped, the restore brings back exactly that item (also with RepeatQueue), and the
/// stop fade never starts the next track.
/// </summary>
public sealed class GracefulStopTests : IAsyncLifetime
{
    private HostHarness _h = null!;
    private Guid[] _tracks = [];

    public async Task InitializeAsync()
    {
        _h = await HostHarness.StartAsync(speed: 10);
        var admin = await _h.ConnectAsync();
        await admin.Auth.CompleteInitialSetupAsync("admin", "admin-pin");
        _tracks =
        [
            (await HostHarness.ImportAsync(admin, "a.wav", TestWav.Tone(8, 440), "A")).TrackId,
            (await HostHarness.ImportAsync(admin, "b.wav", TestWav.Tone(8, 550), "B")).TrackId,
            (await HostHarness.ImportAsync(admin, "c.wav", TestWav.Tone(8, 660), "C")).TrackId,
        ];
        await _h.DisposeClientsAsync();
        await _h.Host.StopAsync();
        await _h.Host.DisposeAsync();
    }

    public async Task DisposeAsync()
    {
        await _h.StartHostAsync(); // so the harness has a host to dispose
        await _h.DisposeAsync();
    }

    private async Task<(SabhaHost Host, NullRealtimeOutput Output)> StartManualAsync()
    {
        NullRealtimeOutput? output = null;
        var options = _h.Options();
        var host = await SabhaHost.StartAsync(new SabhaHostOptions
        {
            DataRoot = options.DataRoot,
            PipeName = options.PipeName,
            StartPipe = false,
            Server = options.Server,
            Engine = options.Engine,
            PersistDebounce = options.PersistDebounce,
            StateHeartbeat = options.StateHeartbeat,
            OutputFactory = () => output = NullRealtimeOutput.CreateManual(),
        });
        return (host, output!);
    }

    private static async Task PumpAsync(SabhaHost host, NullRealtimeOutput output, double seconds)
    {
        for (var done = 0.0; done < seconds; done += 0.25)
        {
            output.Pump(TestFrames(0.25));
            await host.Engine.SyncAsync();
        }
    }

    private static int TestFrames(double seconds) => (int)(seconds * Audio.AudioFormat.SampleRate);

    [Theory]
    [InlineData(LoopMode.RepeatQueue)]
    [InlineData(LoopMode.Off)]
    public async Task Stopping_mid_track_restores_the_item_and_position_that_were_playing(LoopMode loopMode)
    {
        var (host, output) = await StartManualAsync();
        await host.Engine.SetLoopModeAsync(loopMode);
        await host.Engine.SetCrossfadeAsync(2);
        var queue = await host.Engine.EnqueueAsync(_tracks, playNext: false);
        await host.Engine.PlayQueueItemAsync(queue.Items[0].QueueItemId);
        await PumpAsync(host, output, 10.5); // A (8 s, 2 s crossfade) → B, 4.5 s into B
        var before = host.Engine.GetState();
        Assert.Equal((PlaybackStatus.Playing, (Guid?)_tracks[1]), (before.Status, before.CurrentTrackId));
        Assert.Equal(1, host.Engine.GetQueue().CurrentIndex);

        await host.StopAsync(); // nothing is pumped while it stops: the audio is frozen exactly here
        var saved = await new SqlitePlaybackStateRepository(host.Database).LoadQueueAsync();
        await host.DisposeAsync();
        Assert.Equal(1, saved.CurrentIndex);
        Assert.Equal(queue.Items.Select(i => i.QueueItemId), saved.Items.Select(i => i.QueueItemId));
        Assert.InRange(saved.PositionSeconds, before.PositionSeconds - 0.3, before.PositionSeconds + 0.3);

        (host, output) = await StartManualAsync();
        try
        {
            var restoredQueue = host.Engine.GetQueue();
            Assert.Equal(saved.CurrentIndex, restoredQueue.CurrentIndex);
            Assert.Equal(saved.Items.Select(i => i.QueueItemId), restoredQueue.Items.Select(i => i.QueueItemId));
            var restored = host.Engine.GetState();
            Assert.True(restored.Status is PlaybackStatus.Idle or PlaybackStatus.Stopped);
            Assert.Equal((loopMode, 2.0), (restored.LoopMode, restored.CrossfadeSeconds));

            // Resume continues B where it stopped, not A and not C.
            await host.Engine.ResumeAsync();
            await PumpAsync(host, output, 0.5);
            var resumed = host.Engine.GetState();
            Assert.Equal((PlaybackStatus.Playing, (Guid?)_tracks[1], (Guid?)saved.Items[1].QueueItemId),
                (resumed.Status, resumed.CurrentTrackId, resumed.CurrentQueueItemId));
            Assert.True(resumed.PositionSeconds >= saved.PositionSeconds, $"resumed at {resumed.PositionSeconds:F2}, saved {saved.PositionSeconds:F2}");
        }
        finally
        {
            await host.StopAsync();
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task The_stop_fade_never_starts_the_next_track_even_while_the_output_keeps_rendering()
    {
        var (host, output) = await StartManualAsync();
        await host.Engine.SetLoopModeAsync(LoopMode.RepeatQueue);
        await host.Engine.SetCrossfadeAsync(2);
        var queue = await host.Engine.EnqueueAsync(_tracks, playNext: false);
        await host.Engine.PlayQueueItemAsync(queue.Items[0].QueueItemId);
        await PumpAsync(host, output, 5); // 1 s before A's crossfade into B would begin
        var seen = new List<Guid?>();
        host.Engine.StateChanged += (_, s) =>
        {
            lock (seen)
            {
                seen.Add(s.CurrentTrackId);
            }
        };

        // The output keeps rendering (here: 20 s of audio, well past A's end) while the host stops.
        var stop = host.StopAsync();
        var pumped = 0;
        for (; pumped < 80 && !stop.IsCompleted; pumped++)
        {
            output.Pump(TestFrames(0.25));
            await Task.Delay(1);
        }

        await stop;
        Assert.True(pumped >= 8, $"only {pumped * 0.25} s rendered during the stop"); // past where A would crossfade into B
        var saved = await new SqlitePlaybackStateRepository(host.Database).LoadQueueAsync();
        await host.DisposeAsync();

        lock (seen)
        {
            Assert.DoesNotContain(_tracks[1], seen);
            Assert.DoesNotContain(_tracks[2], seen);
        }

        Assert.Equal(0, saved.CurrentIndex); // still A: the stop did not move on
    }
}
