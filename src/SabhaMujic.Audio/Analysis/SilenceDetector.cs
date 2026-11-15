using SabhaMujic.Audio.Decoding;

namespace SabhaMujic.Audio.Analysis;

/// <summary>Result of silence detection, in 48 kHz frames.</summary>
/// <param name="TotalFrames">Frames scanned.</param>
/// <param name="LeadingTrimFrame">First audible frame (snapped to a zero crossing at or before the onset).</param>
/// <param name="TrailingTrimFrame">End of audible audio, exclusive (snapped to a zero crossing at or after the last sound).</param>
/// <param name="Gaps">Internal silent gaps of at least the minimum length (window resolution).</param>
/// <param name="IsSilent">No window reached the threshold; the trims then cover the whole track.</param>
public sealed record SilenceAnalysis(long TotalFrames, long LeadingTrimFrame, long TrailingTrimFrame, IReadOnlyList<SilenceGap> Gaps, bool IsSilent);

/// <summary>
/// Streaming silence detector: RMS over fixed windows (default 20 ms) against a dBFS threshold (default -50).
/// Memory is O(number of gaps). Feed it with <see cref="Process"/> and call <see cref="Complete"/>, or use
/// <see cref="Analyze"/>, which also refines the trims to sample accuracy and snaps them to zero crossings.
/// </summary>
public sealed class SilenceDetector
{
    private readonly SilenceDetectorOptions _options;
    private readonly int _window;
    private readonly long _minGapFrames;
    private readonly double _thresholdMeanSquare;
    private readonly List<SilenceGap> _gaps = [];
    private double _acc;
    private int _accFrames;
    private long _windowStart;
    private long _firstLoudStart = -1;
    private long _lastLoudEnd = -1;
    private long _silentRunStart = -1;
    private long _framesSeen;

    public SilenceDetector(SilenceDetectorOptions? options = null)
    {
        _options = options ?? new SilenceDetectorOptions();
        _window = _options.WindowFrames;
        _minGapFrames = AudioFormat.MillisecondsToFrames(_options.MinSilenceMilliseconds);
        double rms = Math.Pow(10, _options.ThresholdDb / 20.0);
        _thresholdMeanSquare = rms * rms;
    }

    public SilenceDetectorOptions Options => _options;

    /// <summary>Feeds interleaved 48 kHz stereo samples.</summary>
    public void Process(ReadOnlySpan<float> stereo)
    {
        int frames = stereo.Length / 2;
        int i = 0;
        while (i < frames)
        {
            int take = Math.Min(frames - i, _window - _accFrames);
            double acc = 0;
            var chunk = stereo.Slice(i * 2, take * 2);
            for (int k = 0; k < chunk.Length; k++)
            {
                float s = chunk[k];
                acc += s * s;
            }

            _acc += acc;
            _accFrames += take;
            i += take;
            if (_accFrames == _window)
            {
                CloseWindow();
            }
        }

        _framesSeen += frames;
    }

    /// <summary>Window-resolution result (trims not yet refined).</summary>
    public SilenceAnalysis Complete()
    {
        if (_accFrames > 0)
        {
            CloseWindow();
        }

        if (_firstLoudStart < 0)
        {
            return new SilenceAnalysis(_framesSeen, 0, _framesSeen, [], IsSilent: true);
        }

        return new SilenceAnalysis(_framesSeen, _firstLoudStart, Math.Min(_lastLoudEnd, _framesSeen), _gaps.ToArray(), IsSilent: false);
    }

    /// <summary>Full analysis of <paramref name="source"/> from the start, with refined trims.</summary>
    public static SilenceAnalysis Analyze(IAudioSource source, SilenceDetectorOptions? options = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        var detector = new SilenceDetector(options);
        var buffer = new float[8192 * 2];
        source.Seek(0);
        int n;
        while ((n = source.Read(buffer)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            detector.Process(buffer.AsSpan(0, n));
        }

        return Refine(source, detector.Complete(), detector.Options);
    }

    /// <summary>
    /// Refines window-resolution trims to the first/last sample above the threshold amplitude and snaps them to
    /// the nearest zero crossing (leading: at or before the onset, trailing: at or after the last sound).
    /// Seeks <paramref name="source"/>.
    /// </summary>
    public static SilenceAnalysis Refine(IAudioSource source, SilenceAnalysis coarse, SilenceDetectorOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(coarse);
        if (coarse.IsSilent)
        {
            return coarse;
        }

        options ??= new SilenceDetectorOptions();
        int window = options.WindowFrames;
        int zc = AudioFormat.MillisecondsToFrames(options.ZeroCrossingSearchMilliseconds);
        float threshold = (float)Math.Pow(10, options.ThresholdDb / 20.0);
        long total = coarse.TotalFrames;

        // Leading edge.
        long leadStart = Math.Max(0, coarse.LeadingTrimFrame - window - zc - 1);
        long leadEnd = Math.Min(total, coarse.LeadingTrimFrame + window);
        var lead = ReadRange(source, leadStart, leadEnd);
        int onset = (int)(coarse.LeadingTrimFrame - leadStart);
        for (int i = Math.Max(0, (int)(coarse.LeadingTrimFrame - window - leadStart)); i < lead.Length / 2; i++)
        {
            if (Math.Max(Math.Abs(lead[2 * i]), Math.Abs(lead[2 * i + 1])) >= threshold)
            {
                onset = i;
                break;
            }
        }

        int snappedLead = ZeroCrossing.FindNearest(lead, onset, zc, SearchDirection.Backward);
        long leading = leadStart + (snappedLead >= 0 ? snappedLead : onset);

        // Trailing edge.
        long trailStart = Math.Max(0, coarse.TrailingTrimFrame - window - 1);
        long trailEnd = Math.Min(total, coarse.TrailingTrimFrame + window + zc + 1);
        var trail = ReadRange(source, trailStart, trailEnd);
        int trailFrames = trail.Length / 2;
        int last = (int)Math.Min(trailFrames, coarse.TrailingTrimFrame - trailStart) - 1;
        for (int i = Math.Min(trailFrames - 1, (int)(coarse.TrailingTrimFrame + window - trailStart)); i >= 0; i--)
        {
            if (Math.Max(Math.Abs(trail[2 * i]), Math.Abs(trail[2 * i + 1])) >= threshold)
            {
                last = i;
                break;
            }
        }

        int endIndex = last + 1;
        int snappedTrail = ZeroCrossing.FindNearest(trail, endIndex, zc, SearchDirection.Forward);
        long trailing = trailStart + (snappedTrail >= 0 ? snappedTrail : endIndex);
        trailing = Math.Min(trailing, total);

        if (trailing <= leading)
        {
            return coarse;
        }

        return coarse with { LeadingTrimFrame = leading, TrailingTrimFrame = trailing };
    }

    private static float[] ReadRange(IAudioSource source, long start, long end)
    {
        var buffer = new float[Math.Max(0, end - start) * 2];
        source.Seek(start);
        int read = ZeroCrossing.ReadFully(source, buffer);
        return read == buffer.Length ? buffer : buffer[..read];
    }

    private void CloseWindow()
    {
        double meanSquare = _acc / (_accFrames * 2.0);
        long start = _windowStart;
        long end = start + _accFrames;
        if (meanSquare >= _thresholdMeanSquare)
        {
            if (_firstLoudStart < 0)
            {
                _firstLoudStart = start;
            }
            else if (_silentRunStart >= 0 && start - _silentRunStart >= _minGapFrames)
            {
                _gaps.Add(new SilenceGap(_silentRunStart, start));
            }

            _silentRunStart = -1;
            _lastLoudEnd = end;
        }
        else if (_firstLoudStart >= 0 && _silentRunStart < 0)
        {
            _silentRunStart = start;
        }

        _windowStart = end;
        _acc = 0;
        _accFrames = 0;
    }
}
