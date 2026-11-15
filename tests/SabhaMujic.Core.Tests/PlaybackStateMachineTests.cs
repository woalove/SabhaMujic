using SabhaMujic.Contracts;
using SabhaMujic.Core.Playback;

namespace SabhaMujic.Core.Tests;

public class PlaybackStateMachineTests
{
    private static PlaybackStateMachine In(params PlaybackTrigger[] path)
    {
        var sm = new PlaybackStateMachine();
        foreach (var t in path)
        {
            sm.Fire(t);
        }

        return sm;
    }

    [Fact]
    public void Starts_idle() => Assert.Equal(PlaybackStatus.Idle, new PlaybackStateMachine().State);

    [Fact]
    public void Normal_play_crossfade_pause_resume_stop_path()
    {
        var sm = In(PlaybackTrigger.Load, PlaybackTrigger.Loaded);
        Assert.Equal(PlaybackStatus.Playing, sm.State);

        sm.Fire(PlaybackTrigger.BeginCrossfade);
        Assert.Equal(PlaybackStatus.Crossfading, sm.State);

        sm.Fire(PlaybackTrigger.Pause);
        Assert.Equal(PlaybackStatus.Paused, sm.State);

        sm.Fire(PlaybackTrigger.Resume);
        Assert.Equal(PlaybackStatus.Crossfading, sm.State); // resumes where it paused

        sm.Fire(PlaybackTrigger.CrossfadeComplete);
        sm.Fire(PlaybackTrigger.Pause);
        sm.Fire(PlaybackTrigger.Resume);
        Assert.Equal(PlaybackStatus.Playing, sm.State);

        sm.Fire(PlaybackTrigger.Stop);
        Assert.Equal(PlaybackStatus.Stopped, sm.State);
    }

    [Fact]
    public void Load_failure_goes_to_error_and_can_recover()
    {
        var sm = In(PlaybackTrigger.Load, PlaybackTrigger.LoadFailed);
        Assert.Equal(PlaybackStatus.Error, sm.State);
        sm.Fire(PlaybackTrigger.Load);
        Assert.Equal(PlaybackStatus.Loading, sm.State);
    }

    [Fact]
    public void Queue_exhausted_stops()
    {
        var sm = In(PlaybackTrigger.Load, PlaybackTrigger.Loaded, PlaybackTrigger.QueueExhausted);
        Assert.Equal(PlaybackStatus.Stopped, sm.State);
    }

    /// <summary>Exhaustive: every (state, trigger) pair either follows the table or is rejected without changing state.</summary>
    [Fact]
    public void Every_state_trigger_pair_matches_the_table()
    {
        var reachPaths = new Dictionary<PlaybackStatus, PlaybackTrigger[]>
        {
            [PlaybackStatus.Idle] = [],
            [PlaybackStatus.Loading] = [PlaybackTrigger.Load],
            [PlaybackStatus.Playing] = [PlaybackTrigger.Load, PlaybackTrigger.Loaded],
            [PlaybackStatus.Crossfading] = [PlaybackTrigger.Load, PlaybackTrigger.Loaded, PlaybackTrigger.BeginCrossfade],
            [PlaybackStatus.Paused] = [PlaybackTrigger.Load, PlaybackTrigger.Loaded, PlaybackTrigger.Pause],
            [PlaybackStatus.Stopped] = [PlaybackTrigger.Load, PlaybackTrigger.Stop],
            [PlaybackStatus.Error] = [PlaybackTrigger.Load, PlaybackTrigger.LoadFailed],
        };
        Assert.Equal(Enum.GetValues<PlaybackStatus>().Length, reachPaths.Count);

        foreach (var (state, path) in reachPaths)
        {
            foreach (var trigger in Enum.GetValues<PlaybackTrigger>())
            {
                var sm = In(path);
                Assert.Equal(state, sm.State);
                var allowed = PlaybackStateMachine.Transitions.TryGetValue((state, trigger), out var expected);

                Assert.Equal(allowed, sm.TryFire(trigger));
                Assert.Equal(allowed ? expected : state, sm.State);
            }
        }
    }

    [Fact]
    public void Invalid_trigger_throws_from_Fire()
    {
        var sm = new PlaybackStateMachine();
        Assert.Throws<InvalidOperationException>(() => sm.Fire(PlaybackTrigger.Pause));
        Assert.Equal(PlaybackStatus.Idle, sm.State);
    }
}
