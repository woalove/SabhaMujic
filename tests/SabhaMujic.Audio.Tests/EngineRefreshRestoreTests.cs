using SabhaMujic.Audio.Analysis;
using SabhaMujic.Contracts;

namespace SabhaMujic.Audio.Tests;

/// <summary><see cref="Engine.PlaybackEngine.RefreshTrackAsync"/> and <see cref="Engine.PlaybackEngine.RestoreQueueAsync"/>.</summary>
public sealed class EngineRefreshRestoreTests
{
    private static TrackAnalysis Loop(double seconds, double start, double end) => new()
    {
        Version = LoopAnalyzer.AnalysisVersion,
        TotalFrames = TestSignals.Frames(seconds),
        LeadingTrimFrame = 0,
        TrailingTrimFrame = TestSignals.Frames(seconds),
        Loop = new LoopRegion(TestSignals.Frames(start), TestSignals.Frames(end), TestSignals.Frames(0.1), 0.9) { SeamCorrelation = 1 },
    };

    [Fact]
    public async Task Refresh_applies_a_new_loop_region_before_the_next_seam()
    {
        using var h = new EngineHarness();
        var track = h.AddTone(20, 375, Loop(20, 2, 18));
        await h.Engine.SetLoopModeAsync(LoopMode.SmartLoop);
        await h.Engine.PlayTrackAsync(track);
        await h.PlayFor(1);

        // The admin sets a shorter loop: 4..8 s. It must take effect for the seam at 8 s, not the old one at 18 s.
        h.Resolver.Set(track, Loop(20, 4, 8).WithLoopOverride(new LoopRegionDto(4, 8, 0.1, 1, true)));
        await h.Engine.RefreshTrackAsync(track);

        var positions = new List<double>();
        for (var s = 0; s < 12; s++)
        {
            await h.PlayFor(1);
            positions.Add(h.State.PositionSeconds);
            Assert.Equal(PlaybackStatus.Playing, h.State.Status);
        }

        Assert.All(positions, p => Assert.InRange(p, 0, 8.05));
        Assert.Contains(Enumerable.Range(1, positions.Count - 1), i => positions[i] < positions[i - 1]);
    }

    [Fact]
    public async Task Refresh_of_another_track_changes_nothing()
    {
        using var h = new EngineHarness();
        var playing = h.AddTone(20, 375, Loop(20, 2, 18));
        var other = h.AddTone(5, 440);
        await h.Engine.SetLoopModeAsync(LoopMode.SmartLoop);
        await h.Engine.PlayTrackAsync(playing);
        await h.PlayFor(1);
        await h.Engine.RefreshTrackAsync(other);
        await h.Engine.RefreshTrackAsync(Guid.NewGuid());
        await h.PlayFor(10);
        Assert.InRange(h.State.PositionSeconds, 10.5, 11.5);
    }

    [Fact]
    public async Task Restore_keeps_queue_item_ids_and_resumes_at_the_saved_position()
    {
        using var h = new EngineHarness();
        var a = h.AddTone(10, 440);
        var b = h.AddTone(30, 550);
        QueueItemDto[] items = [new(Guid.NewGuid(), a), new(Guid.NewGuid(), b)];

        var queue = await h.Engine.RestoreQueueAsync(items, currentIndex: 1, positionSeconds: 12.5);

        Assert.Equal(items, queue.Items);
        Assert.Equal(1, queue.CurrentIndex);
        Assert.Equal(items, h.Engine.GetQueue().Items);
        Assert.True(h.State.Status is PlaybackStatus.Idle or PlaybackStatus.Stopped);
        Assert.Equal(b, h.State.CurrentTrackId);
        Assert.Equal(items[1].QueueItemId, h.State.CurrentQueueItemId);

        await h.Engine.ResumeAsync();
        await h.PlayFor(0.5);
        Assert.Equal(PlaybackStatus.Playing, h.State.Status);
        Assert.Equal(b, h.State.CurrentTrackId);
        Assert.InRange(h.State.PositionSeconds, 12.9, 13.2);

        // The restored position is used once: stopping and resuming starts the item from the beginning.
        await h.Engine.StopAsync();
        await h.Engine.ResumeAsync();
        await h.PlayFor(0.5);
        Assert.InRange(h.State.PositionSeconds, 0.4, 0.7);
    }

    [Fact]
    public async Task Restore_without_position_starts_the_current_item_from_the_top()
    {
        using var h = new EngineHarness();
        var a = h.AddTone(10, 440);
        await h.Engine.RestoreQueueAsync([new(Guid.NewGuid(), a)], 0);
        await h.Engine.ResumeAsync();
        await h.PlayFor(0.5);
        Assert.InRange(h.State.PositionSeconds, 0.4, 0.7);
    }

    [Fact]
    public async Task A_position_past_the_end_is_clamped_and_playing_another_item_drops_it()
    {
        using var h = new EngineHarness();
        var a = h.AddTone(5, 440);
        var b = h.AddTone(5, 550);
        QueueItemDto[] items = [new(Guid.NewGuid(), a), new(Guid.NewGuid(), b)];
        await h.Engine.RestoreQueueAsync(items, 0, positionSeconds: 3);
        await h.Engine.PlayQueueItemAsync(items[1].QueueItemId);
        await h.PlayFor(0.5);
        Assert.Equal(b, h.State.CurrentTrackId);
        Assert.InRange(h.State.PositionSeconds, 0.4, 0.7);

        await h.Engine.StopAsync();
        await h.Engine.RestoreQueueAsync(items, 0, positionSeconds: 999);
        await h.Engine.ResumeAsync();
        await h.Sync();
        Assert.Equal(a, h.State.CurrentTrackId);
        Assert.InRange(h.State.PositionSeconds, 4.8, 5.0);
    }

    [Fact]
    public async Task Restore_is_refused_while_playing_and_validates_its_input()
    {
        using var h = new EngineHarness();
        var a = h.AddTone(10, 440);
        QueueItemDto[] items = [new(Guid.NewGuid(), a)];
        await AssertCode(ErrorCode.InvalidArgument, () => h.Engine.RestoreQueueAsync(items, 1));
        await AssertCode(ErrorCode.InvalidArgument, () => h.Engine.RestoreQueueAsync([items[0], items[0]], 0));
        await AssertCode(ErrorCode.InvalidArgument, () => h.Engine.RestoreQueueAsync(items, 0, positionSeconds: -1));

        await h.Engine.PlayTrackAsync(a);
        await AssertCode(ErrorCode.Conflict, () => h.Engine.RestoreQueueAsync(items, 0));
    }

    private static async Task AssertCode(ErrorCode code, Func<Task> action)
    {
        var ex = await Assert.ThrowsAsync<SabhaMujicException>(action);
        Assert.Equal(code, ex.Code);
    }
}
