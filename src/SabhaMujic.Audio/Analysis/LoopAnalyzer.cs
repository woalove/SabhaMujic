using SabhaMujic.Audio.Decoding;

namespace SabhaMujic.Audio.Analysis;

/// <summary>Intermediate values of a loop search (diagnostics and tests).</summary>
internal sealed record LoopSearchDiagnostics(
    double Bpm,
    int BeatCount,
    double BestSeamScore,
    double Baseline,
    double Contrast,
    double Correlation,
    double Confidence,
    long CandidateStartFrame,
    long CandidateEndFrame,
    string? FallbackReason);

/// <summary>
/// Offline Smart Loop analysis (plan §3). One streaming pass over the decoded track computes the silence trims and
/// per-hop features; the loop search then works on beat-synchronous features and finally refines the seam on a few
/// short excerpts read with <see cref="IAudioSource.Seek"/>. Memory is bounded by the feature series (~56 bytes per
/// 21 ms hop), never the whole decoded track.
/// </summary>
public static class LoopAnalyzer
{
    /// <summary>Bump whenever the algorithm or its defaults change; cached analyses with another version are recomputed.</summary>
    public const int AnalysisVersion = 1;

    private const int LocalBeats = 2;     // ±2 beats around the seam (plan §3 step 3)
    private const int ContextBeats = 8;   // wider diagonal context to reject in-chord offsets
    private const int ReferenceFrames = 4096;
    private const int FrameLagSearch = 6;

    /// <summary>Analyses the whole source: trims, gaps, tempo and loop region.</summary>
    public static TrackAnalysis Analyze(IAudioSource source, AnalysisOptions? options = null, CancellationToken ct = default)
        => AnalyzeDetailed(source, options, ct).Analysis;

    /// <summary>Opens <paramref name="path"/> with <paramref name="decoders"/> and analyses it.</summary>
    public static TrackAnalysis Analyze(IAudioDecoderFactory decoders, string path, AnalysisOptions? options = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(decoders);
        using var source = decoders.Open(path);
        return Analyze(source, options, ct);
    }

    /// <summary>The whole-track fallback loop: [leading + fade, trailing) with a fade of at most a quarter of the audio.</summary>
    public static LoopRegion CreateFallbackLoop(long leadingTrim, long trailingTrim, long fadeFrames, double confidence)
    {
        long length = Math.Max(0, trailingTrim - leadingTrim);
        long fade = Math.Clamp(fadeFrames, 0, length / 4);
        return new LoopRegion(leadingTrim + fade, Math.Max(leadingTrim + fade, trailingTrim), (int)fade, Math.Clamp(confidence, 0, 1))
        {
            IsFallback = true,
        };
    }

    internal static (TrackAnalysis Analysis, LoopSearchDiagnostics Diagnostics) AnalyzeDetailed(IAudioSource source, AnalysisOptions? options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        options ??= new AnalysisOptions();
        var silence = new SilenceDetector(options.Silence);
        var features = new FeatureExtractor();
        var buffer = new float[8192 * 2];
        long total = 0;
        source.Seek(0);
        int n;
        while ((n = source.Read(buffer)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            var block = buffer.AsSpan(0, n);
            silence.Process(block);
            features.Process(block);
            total += n / 2;
        }

        features.Complete();
        var trims = SilenceDetector.Refine(source, silence.Complete(), options.Silence);
        ct.ThrowIfCancellationRequested();
        var (loop, diagnostics) = FindLoop(source, features.Series, trims, options, ct);
        var analysis = new TrackAnalysis
        {
            Version = AnalysisVersion,
            TotalFrames = total,
            LeadingTrimFrame = trims.LeadingTrimFrame,
            TrailingTrimFrame = trims.TrailingTrimFrame,
            Gaps = trims.Gaps,
            Loop = loop,
            TempoBpm = diagnostics.Bpm,
        };
        return (analysis, diagnostics);
    }

    private static (LoopRegion, LoopSearchDiagnostics) FindLoop(IAudioSource source, FeatureSeries series, SilenceAnalysis trims, AnalysisOptions options, CancellationToken ct)
    {
        long leading = trims.LeadingTrimFrame;
        long trailing = trims.TrailingTrimFrame;
        long fallbackFade = AudioFormat.SecondsToFrames(options.FallbackCrossfadeSeconds);
        double fps = FeatureExtractor.FramesPerSecond;
        const int hop = FeatureExtractor.HopFrames;

        (LoopRegion, LoopSearchDiagnostics) Fallback(double bpm, int beatCount, double best, double baseline, double contrast, double q, double confidence, long a, long b, string reason) =>
            (CreateFallbackLoop(leading, trailing, fallbackFade, confidence),
             new LoopSearchDiagnostics(bpm, beatCount, best, baseline, contrast, q, confidence, a, b, reason));

        int f0 = (int)Math.Ceiling(leading / (double)hop);
        int f1 = Math.Min(series.Count, (int)Math.Floor(trailing / (double)hop));
        if (trims.IsSilent || f1 - f0 < (options.MinLoopSeconds + 2) * fps)
        {
            return Fallback(0, 0, 0, 0, 0, 0, 0, -1, -1, "too short");
        }

        var (period, bpm, beats) = BeatTracker.Track(series.Onset, f0, f1, fps);
        int beatCount = beats.Length;
        if (beatCount < 2 * ContextBeats + options.MinLoopBeats)
        {
            return Fallback(bpm, beatCount, 0, 0, 0, 0, 0, -1, -1, "too few beats");
        }

        // Beat-synchronous features: centred, L2-normalized chroma and mean energy (dB).
        var chroma = new float[beatCount * FeatureSeries.ChromaBins];
        var energy = new float[beatCount];
        var rms = series.RmsDb;
        for (int i = 0; i < beatCount; i++)
        {
            int s = beats[i];
            int e = i + 1 < beatCount ? beats[i + 1] : Math.Min(f1, s + (int)Math.Round(period));
            e = Math.Clamp(e, s + 1, series.Count);
            var c = chroma.AsSpan(i * FeatureSeries.ChromaBins, FeatureSeries.ChromaBins);
            double eSum = 0;
            for (int t = s; t < e; t++)
            {
                var frameChroma = series.Chroma(t);
                for (int k = 0; k < c.Length; k++)
                {
                    c[k] += frameChroma[k];
                }

                eSum += rms[t];
            }

            energy[i] = (float)(eSum / (e - s));
            CenterAndNormalize(c);
        }

        int minLoopBeats = Math.Max(options.MinLoopBeats, (int)Math.Ceiling(options.MinLoopSeconds * fps / period) - 1);
        int minLoopFeatureFrames = (int)Math.Ceiling(options.MinLoopSeconds * fps);
        double span = f1 - f0;
        var diagonal = new double[beatCount];
        var prefix = new double[beatCount + 1];
        double bestScore = double.NegativeInfinity;
        double bestSeam = 0;
        int bestA = -1;
        int bestL = -1;
        double seamSum = 0;
        long seamCount = 0;

        for (int lag = minLoopBeats; lag <= beatCount - 1 - 2 * LocalBeats; lag++)
        {
            ct.ThrowIfCancellationRequested();
            int m = beatCount - lag;
            prefix[0] = 0;
            for (int i = 0; i < m; i++)
            {
                diagonal[i] = Similarity(chroma, energy, i, i + lag);
                prefix[i + 1] = prefix[i] + diagonal[i];
            }

            for (int a = LocalBeats; a <= m - LocalBeats; a++)
            {
                int lengthFrames = beats[a + lag] - beats[a];
                if (lengthFrames < minLoopFeatureFrames)
                {
                    continue;
                }

                double local = (prefix[a + LocalBeats] - prefix[a - LocalBeats]) / (2 * LocalBeats);
                int c0 = Math.Max(0, a - ContextBeats);
                int c1 = Math.Min(m, a + ContextBeats);
                double context = (prefix[c1] - prefix[c0]) / (c1 - c0);
                double seam = 0.6 * local + 0.4 * context;
                double jump = Math.Min(20, Math.Abs(energy[a] - energy[a + lag]));
                double score = seam - 0.01 * jump + 0.04 * lengthFrames / span;
                seamSum += seam;
                seamCount++;
                if (score > bestScore)
                {
                    bestScore = score;
                    bestSeam = seam;
                    bestA = a;
                    bestL = lag;
                }
            }
        }

        if (bestA < 0)
        {
            return Fallback(bpm, beatCount, 0, 0, 0, 0, 0, -1, -1, "no candidate");
        }

        double baseline = seamSum / seamCount;
        double contrast = Math.Clamp((bestSeam - baseline) / Math.Max(0.25, 1 - baseline), 0, 1);

        // Step 4a: refine the lag at feature-frame resolution (parabolic peak of a local similarity).
        int startFeature = beats[bestA];
        double lagFrames = RefineFrameLag(series, startFeature, beats[bestA + bestL] - startFeature, (int)Math.Max(4, Math.Round(2 * period)));

        // Step 4b: sample level. Snap A to a rising zero crossing, cross-correlate the audio just before A with the
        // audio around the predicted B (±RefineRadius), then snap B to the nearest rising zero crossing.
        long a0 = (long)startFeature * hop;
        long aFrame = ZeroCrossing.Snap(source, a0, AudioFormat.MillisecondsToFrames(2), SearchDirection.Both, risingOnly: true);
        long b0 = aFrame + (long)Math.Round(lagFrames * hop);
        int radius = AudioFormat.MillisecondsToFrames(options.RefineRadiusMilliseconds);
        if (aFrame - ReferenceFrames < leading || b0 + radius > trailing)
        {
            return Fallback(bpm, beatCount, bestSeam, baseline, contrast, 0, 0, aFrame, b0, "seam outside trimmed audio");
        }

        var (delta, correlation) = CrossCorrelate(source, aFrame, b0, radius);
        long bFrame = ZeroCrossing.Snap(source, b0 + delta, AudioFormat.MillisecondsToFrames(0.5), SearchDirection.Both, risingOnly: true);
        bFrame = Math.Min(bFrame, trailing);

        double confidence = contrast * (0.5 + 0.5 * Math.Max(0, correlation));
        long minLoopFrames = Math.Max(AudioFormat.SecondsToFrames(options.MinLoopSeconds), (long)Math.Round(options.MinLoopBeats * period * hop));
        if (bFrame - aFrame < minLoopFrames * 0.98)
        {
            return Fallback(bpm, beatCount, bestSeam, baseline, contrast, correlation, confidence, aFrame, bFrame, "loop too short after refinement");
        }

        if (confidence < options.ConfidenceThreshold)
        {
            return Fallback(bpm, beatCount, bestSeam, baseline, contrast, correlation, confidence, aFrame, bFrame, "low confidence");
        }

        // Step 5: shorter crossfades for better-aligned seams, within [min, max] ms and inside the audio.
        double quality = Math.Clamp((correlation - 0.5) / 0.45, 0, 1);
        double fadeMs = options.MaxSeamCrossfadeMilliseconds - (options.MaxSeamCrossfadeMilliseconds - options.MinSeamCrossfadeMilliseconds) * quality;
        long fade = AudioFormat.MillisecondsToFrames(fadeMs);
        fade = Math.Min(fade, Math.Min(aFrame - leading, (bFrame - aFrame) / 4));
        var region = new LoopRegion(aFrame, bFrame, (int)Math.Max(0, fade), Math.Clamp(confidence, 0, 1))
        {
            SeamCorrelation = Math.Clamp(correlation, 0, 1),
        };
        return (region, new LoopSearchDiagnostics(bpm, beatCount, bestSeam, baseline, contrast, correlation, confidence, aFrame, bFrame, null));
    }

    private static double Similarity(float[] chroma, float[] energy, int i, int j)
    {
        var a = chroma.AsSpan(i * FeatureSeries.ChromaBins, FeatureSeries.ChromaBins);
        var b = chroma.AsSpan(j * FeatureSeries.ChromaBins, FeatureSeries.ChromaBins);
        double dot = 0;
        for (int k = 0; k < a.Length; k++)
        {
            dot += a[k] * b[k];
        }

        double energySim = 1 - Math.Min(1, Math.Abs(energy[i] - energy[j]) / 10.0);
        return 0.75 * dot + 0.25 * energySim;
    }

    private static void CenterAndNormalize(Span<float> v)
    {
        float mean = 0;
        for (int k = 0; k < v.Length; k++)
        {
            mean += v[k];
        }

        mean /= v.Length;
        double norm = 0;
        for (int k = 0; k < v.Length; k++)
        {
            v[k] -= mean;
            norm += v[k] * v[k];
        }

        norm = Math.Sqrt(norm);
        if (norm < 1e-9)
        {
            v.Clear();
            return;
        }

        for (int k = 0; k < v.Length; k++)
        {
            v[k] = (float)(v[k] / norm);
        }
    }

    /// <summary>Best lag (fractional feature frames) near <paramref name="lag0"/>, comparing onset and chroma around the start.</summary>
    private static double RefineFrameLag(FeatureSeries series, int start, int lag0, int radius)
    {
        int count = series.Count;
        var scores = new double[2 * FrameLagSearch + 1];
        var onset = series.Onset;
        Span<float> ca = stackalloc float[FeatureSeries.ChromaBins];
        Span<float> cb = stackalloc float[FeatureSeries.ChromaBins];
        for (int j = -FrameLagSearch; j <= FrameLagSearch; j++)
        {
            int lag = lag0 + j;
            int t0 = Math.Max(0, start - radius);
            int t1 = Math.Min(start + radius, count - lag);
            if (t1 - t0 < 4 || lag <= 0)
            {
                scores[j + FrameLagSearch] = double.NegativeInfinity;
                continue;
            }

            double meanA = 0;
            double meanB = 0;
            for (int t = t0; t < t1; t++)
            {
                meanA += onset[t];
                meanB += onset[t + lag];
            }

            meanA /= t1 - t0;
            meanB /= t1 - t0;
            double sab = 0;
            double saa = 0;
            double sbb = 0;
            double chromaSum = 0;
            for (int t = t0; t < t1; t++)
            {
                double x = onset[t] - meanA;
                double y = onset[t + lag] - meanB;
                sab += x * y;
                saa += x * x;
                sbb += y * y;
                series.Chroma(t).CopyTo(ca);
                series.Chroma(t + lag).CopyTo(cb);
                CenterAndNormalize(ca);
                CenterAndNormalize(cb);
                double dot = 0;
                for (int k = 0; k < ca.Length; k++)
                {
                    dot += ca[k] * cb[k];
                }

                chromaSum += dot;
            }

            double onsetNcc = saa > 1e-12 && sbb > 1e-12 ? sab / Math.Sqrt(saa * sbb) : 0;
            scores[j + FrameLagSearch] = 0.5 * onsetNcc + 0.5 * chromaSum / (t1 - t0);
        }

        int best = FrameLagSearch;
        for (int i = 0; i < scores.Length; i++)
        {
            if (scores[i] > scores[best])
            {
                best = i;
            }
        }

        double offset = 0;
        if (best > 0 && best < scores.Length - 1 && double.IsFinite(scores[best - 1]) && double.IsFinite(scores[best + 1]))
        {
            double y0 = scores[best - 1];
            double y1 = scores[best];
            double y2 = scores[best + 1];
            double denom = y0 - 2 * y1 + y2;
            if (Math.Abs(denom) > 1e-12)
            {
                offset = Math.Clamp(0.5 * (y0 - y2) / denom, -0.5, 0.5);
            }
        }

        return lag0 + (best - FrameLagSearch) + offset;
    }

    /// <summary>
    /// Normalized cross-correlation of the mono audio just before <paramref name="aFrame"/> against the audio just before
    /// <c>b0 + delta</c> for delta in [-radius, radius]. Returns the best delta and its correlation.
    /// </summary>
    private static (long Delta, double Correlation) CrossCorrelate(IAudioSource source, long aFrame, long b0, int radius)
    {
        var reference = ReadMono(source, aFrame - ReferenceFrames, ReferenceFrames);
        var target = ReadMono(source, b0 - radius - ReferenceFrames, ReferenceFrames + 2 * radius);
        if (reference.Length < ReferenceFrames || target.Length < ReferenceFrames + 2 * radius)
        {
            return (0, 0);
        }

        double refEnergy = 0;
        for (int i = 0; i < reference.Length; i++)
        {
            refEnergy += reference[i] * reference[i];
        }

        // Sliding energy of the target window.
        double segEnergy = 0;
        for (int i = 0; i < ReferenceFrames; i++)
        {
            segEnergy += target[i] * target[i];
        }

        double best = double.NegativeInfinity;
        int bestOffset = radius;
        for (int offset = 0; offset <= 2 * radius; offset++)
        {
            if (offset > 0)
            {
                float leaving = target[offset - 1];
                float entering = target[offset + ReferenceFrames - 1];
                segEnergy += entering * entering - leaving * leaving;
            }

            double dot = 0;
            for (int i = 0; i < ReferenceFrames; i++)
            {
                dot += reference[i] * target[offset + i];
            }

            double denom = Math.Sqrt(refEnergy * Math.Max(segEnergy, 0));
            double ncc = denom > 1e-12 ? dot / denom : 0;
            if (ncc > best)
            {
                best = ncc;
                bestOffset = offset;
            }
        }

        return (bestOffset - radius, double.IsFinite(best) ? best : 0);
    }

    private static float[] ReadMono(IAudioSource source, long start, int frames)
    {
        if (start < 0)
        {
            return [];
        }

        var stereo = new float[frames * 2];
        source.Seek(start);
        int read = ZeroCrossing.ReadFully(source, stereo) / 2;
        var mono = new float[read];
        for (int i = 0; i < read; i++)
        {
            mono[i] = 0.5f * (stereo[2 * i] + stereo[2 * i + 1]);
        }

        return mono;
    }
}
