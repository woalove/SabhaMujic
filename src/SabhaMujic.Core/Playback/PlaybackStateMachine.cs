using System.Collections.Frozen;
using SabhaMujic.Contracts;

namespace SabhaMujic.Core.Playback;

public enum PlaybackTrigger
{
    /// <summary>Start opening a track (play, next, previous, jump).</summary>
    Load,
    /// <summary>Decoder opened and audio is flowing.</summary>
    Loaded,
    LoadFailed,
    Pause,
    Resume,
    Stop,
    /// <summary>The next track has started fading in over the current one.</summary>
    BeginCrossfade,
    /// <summary>The outgoing track has fully faded out.</summary>
    CrossfadeComplete,
    /// <summary>Last track ended and there is nothing to continue with.</summary>
    QueueExhausted,
    /// <summary>Unrecoverable runtime error (e.g. output device lost and not recoverable).</summary>
    Fail,
    /// <summary>Clear everything back to Idle (e.g. queue cleared while stopped).</summary>
    Reset,
}

/// <summary>
/// The playback state machine. This table IS the specification (see docs/ARCHITECTURE.md).
/// Not thread-safe: the engine serializes access.
/// </summary>
public sealed class PlaybackStateMachine
{
    private static readonly FrozenDictionary<(PlaybackStatus, PlaybackTrigger), PlaybackStatus> Table =
        new Dictionary<(PlaybackStatus, PlaybackTrigger), PlaybackStatus>
        {
            [(PlaybackStatus.Idle, PlaybackTrigger.Load)] = PlaybackStatus.Loading,

            [(PlaybackStatus.Loading, PlaybackTrigger.Loaded)] = PlaybackStatus.Playing,
            [(PlaybackStatus.Loading, PlaybackTrigger.LoadFailed)] = PlaybackStatus.Error,
            [(PlaybackStatus.Loading, PlaybackTrigger.Load)] = PlaybackStatus.Loading,
            [(PlaybackStatus.Loading, PlaybackTrigger.Stop)] = PlaybackStatus.Stopped,

            [(PlaybackStatus.Playing, PlaybackTrigger.Pause)] = PlaybackStatus.Paused,
            [(PlaybackStatus.Playing, PlaybackTrigger.Stop)] = PlaybackStatus.Stopped,
            [(PlaybackStatus.Playing, PlaybackTrigger.Load)] = PlaybackStatus.Loading,
            [(PlaybackStatus.Playing, PlaybackTrigger.BeginCrossfade)] = PlaybackStatus.Crossfading,
            [(PlaybackStatus.Playing, PlaybackTrigger.QueueExhausted)] = PlaybackStatus.Stopped,
            [(PlaybackStatus.Playing, PlaybackTrigger.Fail)] = PlaybackStatus.Error,

            [(PlaybackStatus.Crossfading, PlaybackTrigger.CrossfadeComplete)] = PlaybackStatus.Playing,
            [(PlaybackStatus.Crossfading, PlaybackTrigger.Pause)] = PlaybackStatus.Paused,
            [(PlaybackStatus.Crossfading, PlaybackTrigger.Stop)] = PlaybackStatus.Stopped,
            [(PlaybackStatus.Crossfading, PlaybackTrigger.Load)] = PlaybackStatus.Loading,
            [(PlaybackStatus.Crossfading, PlaybackTrigger.Fail)] = PlaybackStatus.Error,

            // Paused + Resume is resolved dynamically (back to Playing or Crossfading).
            [(PlaybackStatus.Paused, PlaybackTrigger.Resume)] = PlaybackStatus.Playing,
            [(PlaybackStatus.Paused, PlaybackTrigger.Stop)] = PlaybackStatus.Stopped,
            [(PlaybackStatus.Paused, PlaybackTrigger.Load)] = PlaybackStatus.Loading,
            [(PlaybackStatus.Paused, PlaybackTrigger.Fail)] = PlaybackStatus.Error,

            [(PlaybackStatus.Stopped, PlaybackTrigger.Load)] = PlaybackStatus.Loading,
            [(PlaybackStatus.Stopped, PlaybackTrigger.Reset)] = PlaybackStatus.Idle,

            [(PlaybackStatus.Error, PlaybackTrigger.Load)] = PlaybackStatus.Loading,
            [(PlaybackStatus.Error, PlaybackTrigger.Stop)] = PlaybackStatus.Stopped,
            [(PlaybackStatus.Error, PlaybackTrigger.Reset)] = PlaybackStatus.Idle,
        }.ToFrozenDictionary();

    private PlaybackStatus _pausedFrom = PlaybackStatus.Playing;

    public PlaybackStatus State { get; private set; } = PlaybackStatus.Idle;

    public static IReadOnlyDictionary<(PlaybackStatus From, PlaybackTrigger Trigger), PlaybackStatus> Transitions => Table;

    public bool CanFire(PlaybackTrigger trigger) => Table.ContainsKey((State, trigger));

    /// <summary>Applies the trigger. Returns false (state unchanged) when the trigger is not valid in the current state.</summary>
    public bool TryFire(PlaybackTrigger trigger)
    {
        if (!Table.TryGetValue((State, trigger), out var next))
        {
            return false;
        }

        if (trigger == PlaybackTrigger.Pause)
        {
            _pausedFrom = State;
        }
        else if (State == PlaybackStatus.Paused && trigger == PlaybackTrigger.Resume)
        {
            next = _pausedFrom;
        }

        State = next;
        return true;
    }

    public void Fire(PlaybackTrigger trigger)
    {
        if (!TryFire(trigger))
        {
            throw new InvalidOperationException($"Trigger {trigger} is not valid in state {State}.");
        }
    }
}
