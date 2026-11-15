using SabhaMujic.Contracts;

namespace SabhaMujic.Audio.Analysis;

/// <summary>
/// A Smart Loop region in 48 kHz frames.
/// <para>
/// Seam semantics: when playback reaches <see cref="EndFrame"/> it continues at <see cref="StartFrame"/>.
/// The crossfade is aligned so both sides are musically in step: the outgoing audio
/// <c>[EndFrame - CrossfadeFrames, EndFrame)</c> fades out while <c>[StartFrame - CrossfadeFrames, StartFrame)</c>
/// fades in, so after the seam the incoming stream is exactly at <see cref="StartFrame"/>.
/// Hence <c>StartFrame - CrossfadeFrames</c> must not be before the leading trim, and the loop period is
/// <c>EndFrame - StartFrame</c>.
/// </para>
/// </summary>
/// <param name="StartFrame">Loop start (A).</param>
/// <param name="EndFrame">Loop end (B), exclusive.</param>
/// <param name="CrossfadeFrames">Equal-power seam crossfade length.</param>
/// <param name="Confidence">0..1 how sure the analyzer is that this is a musical loop.</param>
public sealed record LoopRegion(long StartFrame, long EndFrame, int CrossfadeFrames, double Confidence)
{
    /// <summary>True when the analyzer was not confident and loops the whole trimmed track with a longer crossfade.</summary>
    public bool IsFallback { get; init; }

    /// <summary>True when an administrator set the region.</summary>
    public bool IsManualOverride { get; init; }

    /// <summary>
    /// Normalized cross-correlation (0..1) between the two sides of the seam, measured by the analyzer.
    /// The mixer uses it to keep the seam's power constant for correlated material (see <see cref="Mixing.EqualPowerCrossfade"/>).
    /// </summary>
    public double SeamCorrelation { get; init; }

    public long LengthFrames => EndFrame - StartFrame;

    public LoopRegionDto ToDto() => new(
        AudioFormat.FramesToSeconds(StartFrame),
        AudioFormat.FramesToSeconds(EndFrame),
        AudioFormat.FramesToSeconds(CrossfadeFrames),
        Confidence,
        IsManualOverride);

    /// <summary>Converts an (e.g. admin-entered) DTO. Throws <see cref="SabhaMujicException"/> for an invalid region.</summary>
    public static LoopRegion FromDto(LoopRegionDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);
        if (!double.IsFinite(dto.StartSeconds) || !double.IsFinite(dto.EndSeconds) || !double.IsFinite(dto.CrossfadeSeconds)
            || dto.StartSeconds < 0 || dto.EndSeconds <= dto.StartSeconds || dto.CrossfadeSeconds < 0)
        {
            throw new SabhaMujicException(ErrorCode.InvalidArgument, "The loop region is invalid.");
        }

        return new LoopRegion(
            AudioFormat.SecondsToFrames(dto.StartSeconds),
            AudioFormat.SecondsToFrames(dto.EndSeconds),
            (int)Math.Min(int.MaxValue, AudioFormat.SecondsToFrames(dto.CrossfadeSeconds)),
            Math.Clamp(dto.Confidence, 0, 1))
        {
            IsManualOverride = dto.IsManualOverride,
        };
    }
}

/// <summary>An internal silent gap, [StartFrame, EndFrame).</summary>
public readonly record struct SilenceGap(long StartFrame, long EndFrame);
