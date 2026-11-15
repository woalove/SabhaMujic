using SabhaMujic.Audio.Analysis;

namespace SabhaMujic.Audio.Engine;

/// <summary>
/// Looks up where a track's audio is and its cached analysis. Implemented by the Server over the database and the
/// protected store; the engine never sees the database. Called on the engine's control thread: it must not call
/// back into the engine and wait for it.
/// </summary>
public interface ITrackResolver
{
    /// <summary>Returns null when the track does not exist.</summary>
    Task<ResolvedTrack?> ResolveAsync(Guid trackId, CancellationToken ct = default);
}

/// <param name="TrackId">The track.</param>
/// <param name="Path">Absolute path of the audio file (never sent to clients).</param>
/// <param name="Analysis">
/// Cached analysis (with any admin loop override already applied via <see cref="TrackAnalysis.WithLoopOverride"/>),
/// or null: the engine then plays untrimmed and analyses the file in the background, raising
/// <see cref="PlaybackEngine.TrackAnalyzed"/> so the result can be stored.
/// </param>
public sealed record ResolvedTrack(Guid TrackId, string Path, TrackAnalysis? Analysis = null);

public sealed record PlaybackEngineOptions
{
    /// <summary>Initial crossfade between queue items, [0, 12] s.</summary>
    public double DefaultCrossfadeSeconds { get; init; } = 6;

    /// <summary>Read-ahead per voice (decoded by the feeder thread).</summary>
    public int VoiceBufferFrames { get; init; } = AudioFormat.SampleRate;

    /// <summary>A newly opened voice must have this much decoded before it is handed to the mixer.</summary>
    public int PrebufferFrames { get; init; } = AudioFormat.SampleRate / 4;

    public TimeSpan PrebufferTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>"Previous" restarts the current track when more than this has played.</summary>
    public double PreviousRestartThresholdSeconds { get; init; } = 3;

    /// <summary>De-click fade for seeks and restarts.</summary>
    public double SeekFadeMilliseconds { get; init; } = 30;

    /// <summary>Fade-in when starting from silence or after a track ended without a crossfade.</summary>
    public double StartFadeMilliseconds { get; init; } = 10;

    /// <summary>Analyse tracks whose <see cref="ResolvedTrack.Analysis"/> is null in the background.</summary>
    public bool AnalyzeMissingTracks { get; init; } = true;

    public AnalysisOptions Analysis { get; init; } = new();

    /// <summary>How often the control thread polls mixer events when no command arrives.</summary>
    public TimeSpan ControlInterval { get; init; } = TimeSpan.FromMilliseconds(10);
}

/// <summary>A track failed to play or analyse. <see cref="Exception"/> is for logs only (it may contain paths).</summary>
public sealed class PlaybackErrorEventArgs(Guid? trackId, Guid? queueItemId, string message, Exception? exception) : EventArgs
{
    public Guid? TrackId { get; } = trackId;

    public Guid? QueueItemId { get; } = queueItemId;

    /// <summary>Client-safe message (no file paths).</summary>
    public string Message { get; } = message;

    public Exception? Exception { get; } = exception;
}

public sealed class TrackAnalyzedEventArgs(Guid trackId, TrackAnalysis analysis) : EventArgs
{
    public Guid TrackId { get; } = trackId;

    public TrackAnalysis Analysis { get; } = analysis;
}
