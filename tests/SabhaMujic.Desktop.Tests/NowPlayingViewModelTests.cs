using SabhaMujic.Contracts;
using SabhaMujic.Desktop.Tests.Fakes;
using SabhaMujic.Desktop.ViewModels;
using SabhaMujic.Desktop.ViewModels.Infrastructure;
using static SabhaMujic.Desktop.Tests.TestHelpers;

namespace SabhaMujic.Desktop.Tests;

public class NowPlayingViewModelTests
{
    private readonly FakeSabhaClient _client = new();
    private readonly TestUi _ui = new();
    private readonly NowPlayingViewModel _vm;

    public NowPlayingViewModelTests()
    {
        _client.Library.Tracks.Add(new TrackDto(TrackA, "Morning Dhun", "Choir", null, 300, AnalysisStatus.Ready, null, DateTimeOffset.UtcNow));
        _vm = new NowPlayingViewModel(_client, _ui.Services, new TrackInfoCache(_client));
    }

    private DateTimeOffset Now => _ui.Scheduler.UtcNow;

    [Fact]
    public void Applies_state_and_resolves_title_from_event()
    {
        _client.Events.PlaybackStateChanged(State(1, position: 10, timestamp: Now));

        Assert.Equal(PlaybackStatus.Playing, _vm.Status);
        Assert.Equal("Morning Dhun", _vm.Title);
        Assert.Equal("Choir", _vm.Artist);
        Assert.Equal(10, _vm.Position);
        Assert.Equal(300, _vm.Duration);
        Assert.True(_vm.IsPlaying);
    }

    [Fact]
    public void Ignores_stale_and_duplicate_revisions()
    {
        _vm.ApplyState(State(5, position: 50, timestamp: Now));
        _vm.ApplyState(State(4, PlaybackStatus.Paused, position: 10, timestamp: Now));
        _vm.ApplyState(State(5, PlaybackStatus.Stopped, position: 0, timestamp: Now));

        Assert.Equal(PlaybackStatus.Playing, _vm.Status);
        Assert.Equal(50, _vm.Position);
        Assert.Equal(5, _vm.LastRevision);

        _vm.ApplyState(State(6, PlaybackStatus.Paused, position: 51, timestamp: Now));
        Assert.Equal(PlaybackStatus.Paused, _vm.Status);
    }

    [Fact]
    public async Task Refresh_accepts_lower_revision_after_service_restart()
    {
        _vm.ApplyState(State(100, position: 50, timestamp: Now));
        _client.Playback.State = State(1, PlaybackStatus.Paused, position: 5, timestamp: Now);

        await _vm.RefreshAsync();

        Assert.Equal(PlaybackStatus.Paused, _vm.Status);
        Assert.Equal(1, _vm.LastRevision);
    }

    [Fact]
    public void Interpolates_position_from_timestamp_between_updates()
    {
        // The server sampled position 10 s at a timestamp 2 s ago.
        _vm.ApplyState(State(1, position: 10, timestamp: Now.AddSeconds(-2)));
        Assert.Equal(12, _vm.Position, precision: 6);

        _ui.Scheduler.Advance(TimeSpan.FromSeconds(1.5));
        Assert.Equal(13.5, _vm.Position, precision: 6);
        Assert.Equal(13.5, _vm.SliderPosition, precision: 6);
        Assert.Equal("0:13", _vm.PositionText);
    }

    [Fact]
    public void Does_not_interpolate_while_paused_and_clamps_to_duration()
    {
        _vm.ApplyState(State(1, PlaybackStatus.Paused, position: 10, timestamp: Now.AddSeconds(-5)));
        Assert.Equal(10, _vm.Position);
        _ui.Scheduler.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal(10, _vm.Position);
        Assert.Equal(0, _ui.Scheduler.ActiveCount); // no ticker while paused

        _vm.ApplyState(State(2, position: 299, duration: 300, timestamp: Now));
        _ui.Scheduler.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(300, _vm.Position);
    }

    [Fact]
    public async Task Slider_does_not_jump_while_user_is_dragging_and_seek_is_sent_on_release()
    {
        _vm.ApplyState(State(1, position: 10, timestamp: Now));

        _vm.BeginSeek();
        _vm.SliderPosition = 200;
        _client.Events.PlaybackStateChanged(State(2, position: 11, timestamp: Now));
        _ui.Scheduler.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(200, _vm.SliderPosition);
        Assert.Equal("3:20", _vm.PositionText); // shows the drag target
        Assert.Empty(_client.CallsTo("SeekAsync"));

        await _vm.EndSeek();

        var seek = Assert.Single(_client.CallsTo("SeekAsync"));
        Assert.Equal(200.0, seek.Args[0]);
        Assert.Equal(200, _vm.SliderPosition, precision: 6);
    }

    [Fact]
    public async Task Events_produced_before_the_seek_was_processed_do_not_pull_the_slider_back()
    {
        _vm.ApplyState(State(1, position: 10, timestamp: Now));
        _client.Playback.SeekGate = new TaskCompletionSource();

        _vm.BeginSeek();
        _vm.SliderPosition = 120;
        var seekTask = _vm.EndSeek();

        // A position heartbeat the service emitted just before it handled the seek.
        _client.Events.PlaybackStateChanged(State(2, position: 10.5, timestamp: Now));
        Assert.Equal(120, _vm.SliderPosition, precision: 6);

        _client.Playback.SeekGate.SetResult();
        await seekTask;

        // After the acknowledgement, server updates drive the slider again.
        _client.Events.PlaybackStateChanged(State(3, position: 121, timestamp: Now));
        Assert.Equal(121, _vm.SliderPosition, precision: 6);
    }

    [Fact]
    public void Volume_changes_are_debounced_and_server_echo_does_not_fight_the_slider()
    {
        _vm.ApplyState(State(1, volume: 1f, timestamp: Now));

        _vm.Volume = 0.7;
        _vm.Volume = 0.5;
        _client.Events.PlaybackStateChanged(State(2, volume: 1f, timestamp: Now)); // older value from the service
        Assert.Equal(0.5, _vm.Volume);
        Assert.Empty(_client.CallsTo("SetVolumeAsync"));

        _ui.Scheduler.Advance(NowPlayingViewModel.SliderSendDelay);

        var call = Assert.Single(_client.CallsTo("SetVolumeAsync"));
        Assert.Equal(0.5f, (float)call.Args[0]!);

        // Once sent, the service is the source of truth again.
        _client.Events.PlaybackStateChanged(State(3, volume: 0.4f, timestamp: Now));
        Assert.Equal(0.4, _vm.Volume, precision: 5);
    }

    [Fact]
    public void Server_values_do_not_trigger_sends()
    {
        _vm.ApplyState(State(1, volume: 0.3f, crossfade: 4, timestamp: Now));
        _ui.Scheduler.Advance(TimeSpan.FromSeconds(1));

        Assert.Empty(_client.CallsTo("SetVolumeAsync"));
        Assert.Empty(_client.CallsTo("SetCrossfadeAsync"));
        Assert.Equal(4, _vm.CrossfadeSeconds);
    }

    [Fact]
    public void Crossfade_is_clamped_to_0_12_and_debounced()
    {
        _vm.CrossfadeSeconds = 20;
        Assert.Equal(12, _vm.CrossfadeSeconds);
        _ui.Scheduler.Advance(NowPlayingViewModel.SliderSendDelay);

        var call = Assert.Single(_client.CallsTo("SetCrossfadeAsync"));
        Assert.Equal(12.0, call.Args[0]);
    }

    [Fact]
    public async Task Loop_mode_cycles_off_smart_repeat()
    {
        await _vm.CycleLoopModeCommand.ExecuteAsync(null);
        await _vm.CycleLoopModeCommand.ExecuteAsync(null);
        await _vm.CycleLoopModeCommand.ExecuteAsync(null);

        Assert.Equal(
            [LoopMode.SmartLoop, LoopMode.RepeatQueue, LoopMode.Off],
            _client.CallsTo("SetLoopModeAsync").Select(c => (LoopMode)c.Args[0]!).ToArray());
        Assert.Equal(LoopMode.Off, _vm.LoopMode);
    }

    [Theory]
    [InlineData(PlaybackStatus.Playing, "PauseAsync")]
    [InlineData(PlaybackStatus.Crossfading, "PauseAsync")]
    [InlineData(PlaybackStatus.Paused, "ResumeAsync")]
    [InlineData(PlaybackStatus.Stopped, "PlayQueueItemAsync")]
    public async Task Play_pause_maps_to_the_right_call(PlaybackStatus status, string expected)
    {
        _vm.ApplyState(State(1, status, timestamp: Now, queueItemId: Id(7)));

        await _vm.PlayPauseCommand.ExecuteAsync(null);

        Assert.Single(_client.CallsTo(expected));
    }

    [Fact]
    public async Task Play_pause_while_loading_sends_nothing()
    {
        _vm.ApplyState(State(1, PlaybackStatus.Loading, timestamp: Now));

        await _vm.PlayPauseCommand.ExecuteAsync(null);

        Assert.DoesNotContain(_client.Calls, c => c.Method != "GetTrackAsync");
    }

    [Fact]
    public async Task Play_when_idle_starts_the_current_queue_item()
    {
        _client.Playback.Queue = Queue(3, 1, Id(1), Id(2));
        _vm.ApplyState(State(1, PlaybackStatus.Idle, timestamp: Now) with { CurrentTrackId = null });

        await _vm.PlayPauseCommand.ExecuteAsync(null);

        Assert.Equal(Id(2), Assert.Single(_client.CallsTo("PlayQueueItemAsync")).Args[0]);
    }

    [Fact]
    public async Task Transport_commands_call_the_service()
    {
        await _vm.NextCommand.ExecuteAsync(null);
        await _vm.PreviousCommand.ExecuteAsync(null);
        await _vm.StopCommand.ExecuteAsync(null);
        await _vm.ToggleMuteCommand.ExecuteAsync(null);

        Assert.Equal(
            ["NextAsync", "PreviousAsync", "StopAsync", "SetMutedAsync"],
            _client.Calls.Select(c => c.Method).ToArray());
        Assert.Equal(true, _client.CallsTo("SetMutedAsync").Single().Args[0]);
    }

    [Fact]
    public void Shows_crossfading_indicator_and_playback_error()
    {
        _vm.ApplyState(State(1, PlaybackStatus.Crossfading, timestamp: Now));
        Assert.True(_vm.IsCrossfading);

        _vm.ApplyState(State(2, PlaybackStatus.Error, timestamp: Now, error: "Could not decode file"));
        Assert.False(_vm.IsCrossfading);
        Assert.Equal("Could not decode file", _vm.PlaybackError);
        Assert.True(_vm.HasPlaybackError);
    }

    [Fact]
    public async Task Command_errors_become_messages_not_exceptions()
    {
        _client.Playback.FailWith = new SabhaMujicException(ErrorCode.Forbidden, "x");

        await _vm.NextCommand.ExecuteAsync(null);

        Assert.Equal(ErrorMessages.For(ErrorCode.Forbidden), _vm.ErrorMessage);
    }
}
