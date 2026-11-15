namespace SabhaMujic.Contracts;

/// <summary>Application role. Capabilities are decided by role, not by Windows account.</summary>
public enum Role
{
    Volunteer = 0,
    Admin = 1,
}

public enum PlaybackStatus
{
    Idle = 0,
    Loading = 1,
    Playing = 2,
    Paused = 3,
    Crossfading = 4,
    Stopped = 5,
    Error = 6,
}

public enum LoopMode
{
    /// <summary>Play the queue once and stop at the end.</summary>
    Off = 0,
    /// <summary>Loop the current track's detected (or admin-overridden) loop region seamlessly.</summary>
    SmartLoop = 1,
    /// <summary>When the queue ends, continue from the first item (with crossfade).</summary>
    RepeatQueue = 2,
}

public enum AnalysisStatus
{
    Pending = 0,
    Analyzing = 1,
    Ready = 2,
    Failed = 3,
}

public enum ClientKind
{
    Desktop = 0,
    Remote = 1,
}
