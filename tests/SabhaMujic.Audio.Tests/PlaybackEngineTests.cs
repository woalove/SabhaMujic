using SabhaMujic.Audio.Analysis;
using SabhaMujic.Audio.Engine;
using SabhaMujic.Contracts;
using Xunit.Abstractions;

namespace SabhaMujic.Audio.Tests;

public class PlaybackEngineTests(ITestOutputHelper output)
{
    private const double Eps = 0.02;

    [Fact]
    public async Task Play_starts_the_track_and_position_follows_rendered_audio()
    {
        using var h = new EngineHarness();
        var track = h.AddTone(10, 440);

        await h.Engine.PlayTrackAsync(track);
        var state = h.State;
        Assert.Equal(PlaybackStatus.Playing, state.Status);
        Assert.Equal(track, state.CurrentTrackId);
        Assert.Equal(10, state.DurationSeconds, 3);
        Assert.Single(h.Engine.GetQueue().Items);
        Assert.Equal(state.CurrentQueueItemId, h.Engine.GetQueue().Items[0].QueueItemId);

        var audio = new float[TestSignals.Frames(1) * 2];
        await h.PlayFor(1, audio);
        Assert.Equal(1.0, h.State.PositionSeconds, 3);
        Assert.InRange(audio.Skip(48_000).Take(2000).Max(), 0.39f, 0.41f); // the tone is audible
    }

    [Fact]
    public async Task Queue_advances_with_a_crossfade_through_the_crossfading_state()
    {
        using var h = new EngineHarness();
        var a = h.AddTone(20, 440);
        var b = h.AddTone(20, 660);
        await h.Engine.SetCrossfadeAsync(2);
        var queue = await h.Engine.EnqueueAsync([a, b], playNext: false);
        await h.Engine.PlayQueueItemAsync(queue.Items[0].QueueItemId);
        Assert.Equal(b, h.State.NextTrackId);

        await h.PlayFor(17.5);
        Assert.Equal(PlaybackStatus.Playing, h.State.Status);
        Assert.Equal(a, h.State.CurrentTrackId);

        await h.PlayFor(1); // the crossfade starts at 20 s - 2 s = 18 s
        var mid = h.State;
        Assert.Equal(PlaybackStatus.Crossfading, mid.Status);
        Assert.Equal(b, mid.CurrentTrackId);
        Assert.Equal(0.5, mid.PositionSeconds, 2);
        Assert.Equal(1, h.Engine.GetQueue().CurrentIndex);

        await h.PlayFor(2);
        var after = h.State;
        Assert.Equal(PlaybackStatus.Playing, after.Status);
        Assert.Equal(b, after.CurrentTrackId);
        Assert.Equal(2.5, after.PositionSeconds, 2);

        var statuses = h.States.Select(s => s.Status).ToList();
        int crossfading = statuses.IndexOf(PlaybackStatus.Crossfading);
        Assert.True(crossfading > 0);
        Assert.Contains(PlaybackStatus.Playing, statuses.Skip(crossfading));
    }

    [Fact]
    public async Task Pause_freezes_position_and_resume_continues()
    {
        using var h = new EngineHarness();
        await h.Engine.PlayTrackAsync(h.AddTone(10, 440));
        await h.PlayFor(1);

        await h.Engine.PauseAsync();
        Assert.Equal(PlaybackStatus.Paused, h.State.Status);
        var silent = new float[TestSignals.Frames(1) * 2];
        await h.PlayFor(1, silent);
        double frozen = h.State.PositionSeconds;
        Assert.InRange(frozen, 1.0, 1.0 + Eps);
        Assert.All(silent.Skip(2 * 1000), s => Assert.Equal(0f, s));

        await h.PlayFor(1);
        Assert.Equal(frozen, h.State.PositionSeconds, 6);

        await h.Engine.ResumeAsync();
        Assert.Equal(PlaybackStatus.Playing, h.State.Status);
        await h.PlayFor(1);
        Assert.Equal(frozen + 1, h.State.PositionSeconds, 3);
    }

    [Fact]
    public async Task Stop_silences_output_and_resets_position()
    {
        using var h = new EngineHarness();
        await h.Engine.PlayTrackAsync(h.AddTone(10, 440));
        await h.PlayFor(2);

        await h.Engine.StopAsync();
        var state = h.State;
        Assert.Equal(PlaybackStatus.Stopped, state.Status);
        Assert.Equal(0, state.PositionSeconds);
        var audio = new float[TestSignals.Frames(0.5) * 2];
        await h.PlayFor(0.5, audio);
        Assert.All(audio.Skip(2 * 1000), s => Assert.Equal(0f, s));

        await h.Engine.ResumeAsync(); // play again from the current queue item
        Assert.Equal(PlaybackStatus.Playing, h.State.Status);
        await h.PlayFor(0.5);
        Assert.Equal(0.5, h.State.PositionSeconds, 3);
    }

    [Fact]
    public async Task Next_and_previous_follow_queue_and_restart_rules()
    {
        using var h = new EngineHarness();
        var t1 = h.AddTone(10, 440);
        var t2 = h.AddTone(10, 550);
        var t3 = h.AddTone(10, 660);
        await h.Engine.SetCrossfadeAsync(0);
        var queue = await h.Engine.EnqueueAsync([t1, t2, t3], false);

        // While stopped, next/previous only move the selection.
        await h.Engine.NextAsync();
        Assert.Equal(0, h.Engine.GetQueue().CurrentIndex);
        Assert.Equal(PlaybackStatus.Idle, h.State.Status);

        await h.Engine.PlayQueueItemAsync(queue.Items[0].QueueItemId);
        await h.PlayFor(1);
        await h.Engine.NextAsync();
        await h.PlayFor(0.5);
        Assert.Equal(t2, h.State.CurrentTrackId);
        Assert.Equal(0.5, h.State.PositionSeconds, 2);

        // Under 3 s: previous goes to the previous item.
        await h.Engine.PreviousAsync();
        await h.PlayFor(4);
        Assert.Equal(t1, h.State.CurrentTrackId);
        Assert.Equal(4, h.State.PositionSeconds, 2);

        // Over 3 s: previous restarts the current track.
        await h.Engine.PreviousAsync();
        await h.PlayFor(0.25);
        Assert.Equal(t1, h.State.CurrentTrackId);
        Assert.Equal(0.25, h.State.PositionSeconds, 2);
        Assert.Equal(0, h.Engine.GetQueue().CurrentIndex);

        // Next past the end (loop off) stops.
        await h.Engine.PlayQueueItemAsync(queue.Items[2].QueueItemId);
        await h.Engine.NextAsync();
        Assert.Equal(PlaybackStatus.Stopped, h.State.Status);
    }

    [Fact]
    public async Task Seek_moves_the_position_within_the_track()
    {
        using var h = new EngineHarness();
        await h.Engine.PlayTrackAsync(h.AddTone(10, 440));
        await h.PlayFor(1);
        await h.Engine.SeekAsync(6.5);
        await h.PlayFor(0.5);
        Assert.Equal(7.0, h.State.PositionSeconds, 2);
        await Assert.ThrowsAsync<SabhaMujicException>(() => h.Engine.SeekAsync(-1));
        await Assert.ThrowsAsync<SabhaMujicException>(() => h.Engine.SetVolumeAsync(1.5f));
        await Assert.ThrowsAsync<SabhaMujicException>(() => h.Engine.SetCrossfadeAsync(13));
        await Assert.ThrowsAsync<SabhaMujicException>(() => h.Engine.PlayQueueItemAsync(Guid.NewGuid()));
        await Assert.ThrowsAsync<SabhaMujicException>(() => h.Engine.PlayTrackAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task Volume_and_mute_scale_the_output()
    {
        using var h = new EngineHarness();
        await h.Engine.PlayTrackAsync(h.AddTone(10, 440)); // amplitude 0.4
        await h.Engine.SetVolumeAsync(0.5f);
        var audio = new float[TestSignals.Frames(1) * 2];
        await h.PlayFor(1, audio);
        Assert.InRange(audio.Skip(20_000).Max(), 0.099f, 0.101f); // gain = volume² = 0.25
        Assert.Equal(0.5f, h.State.Volume);

        await h.Engine.SetMutedAsync(true);
        await h.PlayFor(1, audio);
        Assert.Equal(0f, audio.Skip(2000).Max());
        Assert.True(h.State.Muted);
        Assert.Equal(PlaybackStatus.Playing, h.State.Status);
    }

    [Fact]
    public async Task Smart_loop_keeps_looping_past_the_natural_end()
    {
        using var h = new EngineHarness();
        long total = TestSignals.Frames(20);
        var analysis = new TrackAnalysis
        {
            Version = LoopAnalyzer.AnalysisVersion,
            TotalFrames = total,
            LeadingTrimFrame = 0,
            TrailingTrimFrame = total,
            Loop = new LoopRegion(TestSignals.Frames(5), TestSignals.Frames(15), TestSignals.Frames(0.1), 0.9) { SeamCorrelation = 1 },
        };
        var track = h.AddTone(20, 375, analysis); // 128-sample period: the 10 s loop is phase-aligned
        await h.Engine.SetLoopModeAsync(LoopMode.SmartLoop);
        await h.Engine.PlayTrackAsync(track);

        var audio = new float[TestSignals.Frames(60) * 2];
        var positions = new List<double>();
        for (int s = 0; s < 60; s++)
        {
            int frames = TestSignals.Frames(1);
            h.Output.Pump(audio.AsSpan(s * frames * 2, frames * 2));
            await h.Sync();
            positions.Add(h.State.PositionSeconds);
            Assert.Equal(PlaybackStatus.Playing, h.State.Status);
            Assert.Equal(track, h.State.CurrentTrackId);
        }

        output.WriteLine(string.Join(", ", positions.Select(p => p.ToString("F2"))));
        Assert.All(positions, p => Assert.InRange(p, 0, 15.0));
        Assert.Contains(Enumerable.Range(1, positions.Count - 1), i => positions[i] < positions[i - 1]); // wrapped back
        Assert.InRange(positions[^1], 5.0, 15.0);

        // 60 s of a 20 s track: the audio never stopped and has no seam discontinuities.
        for (int w = 0; w < 600; w++)
        {
            float peak = 0;
            for (int i = w * 9600; i < (w + 1) * 9600; i++)
            {
                peak = Math.Max(peak, audio[i]);
            }

            Assert.True(peak > 0.3f, $"silence near {w / 10.0} s");
        }

        Assert.True(CrossfadeMixerTests.MaxDelta(audio, 5000, audio.Length / 2) <= CrossfadeMixerTests.MaxDelta(audio, 5000, 200_000) * 1.05);
    }

    [Fact]
    public async Task Queue_end_stops_with_loop_off_and_wraps_with_repeat_queue()
    {
        using var h = new EngineHarness();
        var a = h.AddTone(3, 440);
        var b = h.AddTone(3, 660);
        await h.Engine.SetCrossfadeAsync(1);
        var queue = await h.Engine.EnqueueAsync([a, b], false);

        await h.Engine.PlayQueueItemAsync(queue.Items[0].QueueItemId);
        await h.PlayFor(6);
        Assert.Equal(PlaybackStatus.Stopped, h.State.Status);
        Assert.Null(h.State.NextTrackId);

        await h.Engine.SetLoopModeAsync(LoopMode.RepeatQueue);
        await h.Engine.PlayQueueItemAsync(queue.Items[0].QueueItemId);
        var seen = new List<Guid?>();
        for (int i = 0; i < 12; i++)
        {
            await h.PlayFor(0.5);
            seen.Add(h.State.CurrentTrackId);
            Assert.NotEqual(PlaybackStatus.Stopped, h.State.Status);
        }

        // a → b → a again (wrapped).
        int firstB = seen.IndexOf(b);
        Assert.True(firstB > 0);
        Assert.Contains(a, seen.Skip(firstB));
    }

    [Fact]
    public async Task Unreadable_files_are_skipped_with_an_error_message()
    {
        using var h = new EngineHarness();
        var broken = h.AddBroken();
        var good = h.AddTone(5, 440);
        var queue = await h.Engine.EnqueueAsync([broken, good], false);

        await h.Engine.PlayQueueItemAsync(queue.Items[0].QueueItemId);
        var state = h.State;
        Assert.Equal(PlaybackStatus.Playing, state.Status);
        Assert.Equal(good, state.CurrentTrackId);
        Assert.Contains("could not be played", state.ErrorMessage);
        Assert.Contains(h.Errors, e => e.TrackId == broken);
        Assert.DoesNotContain(".wav", state.ErrorMessage);

        // A broken item in the middle of an automatic advance is skipped too.
        var first = h.AddTone(3, 440);
        var last = h.AddTone(5, 660);
        await h.Engine.SetCrossfadeAsync(1);
        var q2 = await h.Engine.ReplaceQueueAsync([first, h.AddBroken(), last], startPlayback: true);
        Assert.Equal(first, h.State.CurrentTrackId);
        await h.PlayFor(3.5);
        Assert.Equal(last, h.State.CurrentTrackId);
        Assert.Equal(2, h.Engine.GetQueue().CurrentIndex);
        Assert.Equal(3, q2.Items.Count);
    }

    [Fact]
    public async Task When_every_item_fails_the_engine_ends_in_error_instead_of_looping()
    {
        using var h = new EngineHarness();
        await h.Engine.SetLoopModeAsync(LoopMode.RepeatQueue);
        var queue = await h.Engine.EnqueueAsync([h.AddBroken(), h.AddBroken(), h.AddBroken()], false);

        await h.Engine.PlayQueueItemAsync(queue.Items[1].QueueItemId).WaitAsync(TimeSpan.FromSeconds(10));
        var state = h.State;
        Assert.Equal(PlaybackStatus.Error, state.Status);
        Assert.NotNull(state.ErrorMessage);
        await h.PlayFor(1);
        Assert.Equal(PlaybackStatus.Error, h.State.Status);

        // The engine still accepts commands.
        await h.Engine.StopAsync();
        Assert.Equal(PlaybackStatus.Stopped, h.State.Status);
    }

    /// <summary>BUG-02: a failed load of the requested track never interrupts what is playing.</summary>
    [Fact]
    public async Task An_unplayable_requested_track_does_not_interrupt_the_music()
    {
        using var h = new EngineHarness();
        var good = h.AddTone(30, 440);
        var later = h.AddTone(30, 550);
        var broken = h.AddBroken();
        await h.Engine.SetLoopModeAsync(LoopMode.RepeatQueue);
        var queue = await h.Engine.EnqueueAsync([good, later], false);
        await h.Engine.PlayQueueItemAsync(queue.Items[0].QueueItemId);
        await h.PlayFor(1);
        var before = h.State.PositionSeconds;

        // Play a broken track: it is not left in the queue, the current track carries on, the error names the track.
        int seen = h.States.Count;
        await h.Engine.PlayTrackAsync(broken);
        var state = h.State;
        Assert.Equal((PlaybackStatus.Playing, (Guid?)good), (state.Status, state.CurrentTrackId));
        Assert.Contains(broken.ToString(), state.ErrorMessage);
        Assert.Contains("keeps playing", state.ErrorMessage);
        Assert.DoesNotContain("None of the queued tracks", state.ErrorMessage);
        Assert.Equal([good, later], h.Engine.GetQueue().Items.Select(i => i.TrackId));
        Assert.DoesNotContain(h.States.Skip(seen), s => s.Status != PlaybackStatus.Playing); // never Loading/Error in between
        await h.PlayFor(1);
        Assert.True(h.State.PositionSeconds > before, "still advancing");

        // A broken queue item chosen explicitly: kept in the queue, but RepeatQueue does not wrap to some other item.
        var withBroken = await h.Engine.EnqueueAsync([broken], false);
        await h.Engine.PlayQueueItemAsync(withBroken.Items[^1].QueueItemId);
        Assert.Equal((PlaybackStatus.Playing, (Guid?)good), (h.State.Status, h.State.CurrentTrackId));
        Assert.Equal(0, h.Engine.GetQueue().CurrentIndex);
        Assert.Equal(3, h.Engine.GetQueue().Items.Count);

        // A good request still crossfades over as before; the error is cleared.
        await h.Engine.PlayTrackAsync(later);
        Assert.Equal(later, h.State.CurrentTrackId);
        Assert.Null(h.State.ErrorMessage);
    }

    [Fact]
    public async Task With_nothing_playing_an_unplayable_track_still_ends_in_error()
    {
        using var h = new EngineHarness();
        var broken = h.AddBroken();
        await h.Engine.PlayTrackAsync(broken);
        Assert.Equal(PlaybackStatus.Error, h.State.Status);
        Assert.Contains(broken.ToString(), h.State.ErrorMessage);
        Assert.Single(h.Engine.GetQueue().Items); // behaves as before: the item stays queued
    }

    [Fact]
    public async Task Revisions_increase_with_every_published_change()
    {
        using var h = new EngineHarness();
        long initial = h.State.Revision;
        var track = h.AddTone(5, 440);
        await h.Engine.PlayTrackAsync(track);
        await h.Engine.SetVolumeAsync(0.7f);
        await h.Engine.SetLoopModeAsync(LoopMode.RepeatQueue);
        await h.Engine.PauseAsync();

        var revisions = h.States.Select(s => s.Revision).ToList();
        Assert.True(revisions.Count >= 4);
        Assert.Equal(revisions.OrderBy(r => r).Distinct(), revisions);
        Assert.True(h.State.Revision > initial);
        Assert.Equal(revisions[^1], h.State.Revision);

        long queueRevision = h.Engine.GetQueue().Revision;
        await h.Engine.EnqueueAsync([track], false);
        Assert.True(h.Engine.GetQueue().Revision > queueRevision);
        Assert.True(h.Queues.Last().Revision > queueRevision);
    }

    [Fact]
    public async Task Queue_editing_commands_work_while_playing()
    {
        using var h = new EngineHarness();
        var t = Enumerable.Range(0, 4).Select(i => h.AddTone(6, 300 + 100 * i)).ToArray();
        await h.Engine.SetCrossfadeAsync(0);
        var queue = await h.Engine.EnqueueAsync(t, false);
        await h.Engine.PlayQueueItemAsync(queue.Items[0].QueueItemId);

        queue = await h.Engine.MoveQueueItemAsync(queue.Items[3].QueueItemId, 1);
        Assert.Equal(t[3], queue.Items[1].TrackId);
        Assert.Equal(t[3], h.State.NextTrackId);

        queue = await h.Engine.RemoveFromQueueAsync(queue.Items[1].QueueItemId);
        Assert.Equal(t[1], h.State.NextTrackId);

        // Removing the playing item moves on to the next.
        queue = await h.Engine.RemoveFromQueueAsync(queue.Items[0].QueueItemId);
        await h.PlayFor(0.2);
        Assert.Equal(t[1], h.State.CurrentTrackId);

        queue = await h.Engine.EnqueueAsync([t[0]], playNext: true);
        Assert.Equal(t[0], queue.Items[1].TrackId);

        queue = await h.Engine.ClearQueueAsync();
        Assert.Single(queue.Items);
        Assert.Equal(t[1], queue.Items[0].TrackId);
        Assert.Equal(PlaybackStatus.Playing, h.State.Status);
        await Assert.ThrowsAsync<SabhaMujicException>(() => h.Engine.RemoveFromQueueAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task Missing_analysis_is_computed_in_the_background_and_reported()
    {
        using var h = new EngineHarness(new PlaybackEngineOptions { AnalyzeMissingTracks = true });
        var analyzed = new TaskCompletionSource<TrackAnalyzedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Engine.TrackAnalyzed += (_, e) => analyzed.TrySetResult(e);
        var track = h.AddSignal(TestSignals.Concat(TestSignals.Silence(1), TestSignals.Song(4, 12, 4, 2)));

        await h.Engine.PlayTrackAsync(track);
        var result = await analyzed.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(track, result.TrackId);
        Assert.InRange(result.Analysis.LeadingTrimFrame, TestSignals.Frames(1) - 960, TestSignals.Frames(1) + 960);
        Assert.False(result.Analysis.Loop.IsFallback);
        await h.PlayFor(1);
        Assert.Equal(PlaybackStatus.Playing, h.State.Status);
    }

    [Fact]
    public async Task Device_loss_is_reported_in_the_state_and_cleared_on_restore()
    {
        using var h = new EngineHarness();
        await h.Engine.PlayTrackAsync(h.AddTone(5, 440));
        h.Output.SimulateDeviceLost();
        await h.Sync();
        await h.Sync();
        Assert.Contains("device", h.State.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        h.Output.SimulateDeviceRestored();
        await h.Sync();
        await h.Sync();
        Assert.Null(h.State.ErrorMessage);
    }
}
