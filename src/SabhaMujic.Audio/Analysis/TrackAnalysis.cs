using SabhaMujic.Contracts;

namespace SabhaMujic.Audio.Analysis;

/// <summary>
/// Everything the engine needs to play a track well: silence trims, the Smart Loop region and the
/// analysis version (cache key together with the content hash). Plain data; serializes with System.Text.Json.
/// </summary>
public sealed record TrackAnalysis
{
    /// <summary>Version of the algorithm that produced this result (<see cref="LoopAnalyzer.AnalysisVersion"/>).</summary>
    public required int Version { get; init; }

    /// <summary>Decoded length in 48 kHz frames.</summary>
    public required long TotalFrames { get; init; }

    /// <summary>First frame of audible audio (zero-crossing snapped).</summary>
    public required long LeadingTrimFrame { get; init; }

    /// <summary>End of audible audio, exclusive (zero-crossing snapped).</summary>
    public required long TrailingTrimFrame { get; init; }

    public IReadOnlyList<SilenceGap> Gaps { get; init; } = [];

    public required LoopRegion Loop { get; init; }

    /// <summary>Estimated tempo, 0 when unknown.</summary>
    public double TempoBpm { get; init; }

    public bool IsCurrentVersion => Version == LoopAnalyzer.AnalysisVersion;

    public double DurationSeconds => AudioFormat.FramesToSeconds(TotalFrames);

    public LoopRegionDto ToLoopRegionDto() => Loop.ToDto();

    /// <summary>
    /// Returns a copy using an administrator's loop region (or this analysis unchanged for null).
    /// The region is clamped to the trimmed audio and its crossfade is shortened so it stays inside it.
    /// </summary>
    public TrackAnalysis WithLoopOverride(LoopRegionDto? manual)
    {
        if (manual is null)
        {
            return this;
        }

        var region = LoopRegion.FromDto(manual with { IsManualOverride = true });
        long start = Math.Clamp(region.StartFrame, LeadingTrimFrame, TrailingTrimFrame);
        long end = Math.Clamp(region.EndFrame, start, TrailingTrimFrame);
        if (end - start < AudioFormat.SampleRate / 2)
        {
            throw new SabhaMujicException(ErrorCode.InvalidArgument, "The loop region must be at least 0.5 s of audible audio.");
        }

        long maxFade = Math.Min(start - LeadingTrimFrame, (end - start) / 2);
        int fade = (int)Math.Clamp(region.CrossfadeFrames, 0, maxFade);
        return this with { Loop = region with { StartFrame = start, EndFrame = end, CrossfadeFrames = fade } };
    }

    /// <summary>A placeholder for a track that has not been analysed: no trimming, whole-track fallback loop.</summary>
    public static TrackAnalysis Unanalyzed(long totalFrames, double fallbackCrossfadeSeconds = 2.0)
    {
        var loop = LoopAnalyzer.CreateFallbackLoop(0, totalFrames, AudioFormat.SecondsToFrames(fallbackCrossfadeSeconds), 0);
        return new TrackAnalysis
        {
            Version = 0,
            TotalFrames = totalFrames,
            LeadingTrimFrame = 0,
            TrailingTrimFrame = totalFrames,
            Loop = loop,
        };
    }
}
