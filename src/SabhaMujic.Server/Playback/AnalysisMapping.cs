using SabhaMujic.Audio;
using SabhaMujic.Audio.Analysis;
using SabhaMujic.Contracts;
using StoredAnalysis = SabhaMujic.Data.Repositories.TrackAnalysis;

namespace SabhaMujic.Server.Playback;

/// <summary>
/// Converts between the engine's frame-based <see cref="TrackAnalysis"/> and the database row (seconds).
/// <para>
/// The database keeps trims, the loop region, its crossfade and confidence. It does not keep the seam correlation, the
/// fallback flag, gaps or tempo; those survive only in memory (see <see cref="StoreTrackResolver"/>) until the next
/// service restart, after which a Ready analysis is replayed with seam correlation 0 (plain equal-power seam).
/// </para>
/// </summary>
public static class AnalysisMapping
{
    public static StoredAnalysis ToStored(Guid trackId, TrackAnalysis analysis) => new(
        trackId,
        AnalysisStatus.Ready,
        analysis.Version,
        AudioFormat.FramesToSeconds(analysis.LeadingTrimFrame),
        AudioFormat.FramesToSeconds(analysis.TrailingTrimFrame),
        AudioFormat.FramesToSeconds(analysis.Loop.StartFrame),
        AudioFormat.FramesToSeconds(analysis.Loop.EndFrame),
        AudioFormat.FramesToSeconds(analysis.Loop.CrossfadeFrames),
        Math.Clamp(analysis.Loop.Confidence, 0, 1));

    /// <summary>
    /// Rebuilds an engine analysis from a Ready row. Returns null when the row is not Ready or incomplete.
    /// <paramref name="durationSeconds"/> is the track's decoded length (0 when unknown).
    /// </summary>
    public static TrackAnalysis? FromStored(StoredAnalysis? stored, double durationSeconds)
    {
        if (stored is not { Status: AnalysisStatus.Ready, TrimStartSeconds: { } trimStart, TrimEndSeconds: { } trimEnd,
                LoopStartSeconds: { } loopStart, LoopEndSeconds: { } loopEnd })
        {
            return null;
        }

        long leading = AudioFormat.SecondsToFrames(trimStart);
        long trailing = Math.Max(leading, AudioFormat.SecondsToFrames(trimEnd));
        long total = Math.Max(trailing, AudioFormat.SecondsToFrames(durationSeconds));
        long start = Math.Clamp(AudioFormat.SecondsToFrames(loopStart), leading, trailing);
        long end = Math.Clamp(AudioFormat.SecondsToFrames(loopEnd), start, trailing);
        if (end <= start)
        {
            return null;
        }

        long fade = Math.Clamp(AudioFormat.SecondsToFrames(stored.LoopCrossfadeSeconds ?? 0), 0, Math.Min(start - leading, (end - start) / 2));
        return new TrackAnalysis
        {
            Version = stored.AnalysisVersion,
            TotalFrames = total,
            LeadingTrimFrame = leading,
            TrailingTrimFrame = trailing,
            Loop = new LoopRegion(start, end, (int)fade, stored.Confidence ?? 0),
        };
    }
}
