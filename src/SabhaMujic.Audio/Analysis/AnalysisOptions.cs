namespace SabhaMujic.Audio.Analysis;

public sealed record SilenceDetectorOptions
{
    /// <summary>RMS window length.</summary>
    public double WindowMilliseconds { get; init; } = 20;

    /// <summary>Windows quieter than this (RMS, dBFS) are silent.</summary>
    public double ThresholdDb { get; init; } = -50;

    /// <summary>Internal gaps shorter than this are not reported.</summary>
    public double MinSilenceMilliseconds { get; init; } = 300;

    /// <summary>How far a trim point may move to reach a zero crossing.</summary>
    public double ZeroCrossingSearchMilliseconds { get; init; } = 10;

    internal int WindowFrames => Math.Max(16, AudioFormat.MillisecondsToFrames(WindowMilliseconds));
}

public sealed record AnalysisOptions
{
    public SilenceDetectorOptions Silence { get; init; } = new();

    /// <summary>The loop must be at least this long (and at least <see cref="MinLoopBeats"/>).</summary>
    public double MinLoopSeconds { get; init; } = 15;

    public int MinLoopBeats { get; init; } = 8;

    /// <summary>Below this confidence the analyzer falls back to looping the whole trimmed track.</summary>
    public double ConfidenceThreshold { get; init; } = 0.4;

    /// <summary>Crossfade used for the whole-track fallback loop.</summary>
    public double FallbackCrossfadeSeconds { get; init; } = 2.0;

    public double MinSeamCrossfadeMilliseconds { get; init; } = 50;

    public double MaxSeamCrossfadeMilliseconds { get; init; } = 400;

    /// <summary>Search radius of the sample-level cross-correlation refinement of the loop end.</summary>
    public double RefineRadiusMilliseconds { get; init; } = 20;
}
