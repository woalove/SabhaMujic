namespace SabhaMujic.Audio.Mixing;

public enum TransitionKind
{
    /// <summary>A short de-click fade (seek, restart, start from silence). No crossfade state.</summary>
    Cut = 0,
    /// <summary>A user-initiated crossfade to another item (play/next/previous while playing).</summary>
    Crossfade = 1,
    /// <summary>Automatic crossfade into the next queue item, starting at the trimmed end minus the fade.</summary>
    QueueAdvance = 2,
    /// <summary>Smart Loop seam: sample-exact, aligned crossfade from the loop end back to the loop start.</summary>
    LoopSeam = 3,
    /// <summary>Smart Loop restart from the end of the track when the seam point was already passed.</summary>
    LoopRestart = 4,
}

public enum MixerEventKind
{
    /// <summary><see cref="MixerEvent.Voice"/> became the current voice (fading in over the transition).</summary>
    TransitionStarted = 0,
    /// <summary>The fade-in of <see cref="MixerEvent.Voice"/> finished.</summary>
    TransitionCompleted = 1,
    /// <summary>A scheduled transition could not start (its trigger passed or the current voice changed); the voice was released.</summary>
    TransitionMissed = 2,
    /// <summary>The current voice played to its end and nothing followed it.</summary>
    VoiceEnded = 3,
    /// <summary>The mixer no longer references <see cref="MixerEvent.Voice"/>; its owner may dispose it.</summary>
    VoiceReleased = 4,
}

/// <summary>Notification from the render thread. <see cref="Other"/> is the outgoing voice of a transition.</summary>
public readonly record struct MixerEvent(MixerEventKind Kind, TransitionKind Transition, PlaybackVoice Voice, PlaybackVoice? Other);
