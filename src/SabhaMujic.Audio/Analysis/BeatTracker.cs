namespace SabhaMujic.Audio.Analysis;

/// <summary>
/// Tempo estimation by autocorrelation of the onset-strength envelope (plan §3 step 2), followed by
/// dynamic-programming beat tracking (Ellis 2007) so beats follow moderate tempo drift.
/// </summary>
internal static class BeatTracker
{
    private const double MinBpm = 50;
    private const double MaxBpm = 200;
    private const double PreferredBpm = 120;
    private const double Tightness = 100;

    /// <summary>Beat positions (feature-frame indices) within [start, end) and the beat period in frames.</summary>
    public static (double PeriodFrames, double Bpm, int[] Beats) Track(ReadOnlySpan<float> onsetRaw, int start, int end, double framesPerSecond)
    {
        start = Math.Clamp(start, 0, onsetRaw.Length);
        end = Math.Clamp(end, start, onsetRaw.Length);
        int n = end - start;
        double defaultPeriod = 60.0 * framesPerSecond / PreferredBpm;
        if (n < 4)
        {
            return (defaultPeriod, 0, []);
        }

        var onset = Normalize(onsetRaw.Slice(start, n));
        double period = EstimatePeriod(onset, framesPerSecond, out double bpm);
        if (period <= 0)
        {
            period = defaultPeriod;
            bpm = 0;
        }

        var beats = TrackBeats(onset, period);
        for (int i = 0; i < beats.Length; i++)
        {
            beats[i] += start;
        }

        return (period, bpm, beats);
    }

    /// <summary>High-pass (subtract local mean), half-wave rectify and scale to unit standard deviation.</summary>
    private static float[] Normalize(ReadOnlySpan<float> raw)
    {
        int n = raw.Length;
        var o = new float[n];
        const int half = 8;
        double sum = 0;
        int count = 0;
        int lo = 0;
        int hi = 0;
        for (int i = 0; i < n; i++)
        {
            while (hi < n && hi <= i + half)
            {
                sum += raw[hi++];
                count++;
            }

            while (lo < i - half)
            {
                sum -= raw[lo++];
                count--;
            }

            o[i] = Math.Max(0f, raw[i] - (float)(sum / count));
        }

        double mean = 0;
        for (int i = 0; i < n; i++)
        {
            mean += o[i];
        }

        mean /= n;
        double var = 0;
        for (int i = 0; i < n; i++)
        {
            var += (o[i] - mean) * (o[i] - mean);
        }

        double std = Math.Sqrt(var / n);
        if (std < 1e-6)
        {
            Array.Clear(o);
            return o;
        }

        for (int i = 0; i < n; i++)
        {
            o[i] = (float)(o[i] / std);
        }

        return o;
    }

    private static double EstimatePeriod(float[] onset, double fps, out double bpm)
    {
        int n = onset.Length;
        int minLag = Math.Max(2, (int)Math.Floor(60 * fps / MaxBpm));
        int maxLag = Math.Min(n / 2, (int)Math.Ceiling(60 * fps / MinBpm));
        bpm = 0;
        if (maxLag <= minLag + 2)
        {
            return 0;
        }

        var score = new double[maxLag + 2];
        double best = 0;
        int bestLag = -1;
        for (int lag = minLag - 1; lag <= maxLag + 1; lag++)
        {
            double ac = 0;
            for (int i = 0; i + lag < n; i++)
            {
                ac += onset[i] * onset[i + lag];
            }

            ac /= n - lag;
            double lagBpm = 60 * fps / lag;
            double octaves = Math.Log2(lagBpm / PreferredBpm);
            score[lag] = ac * Math.Exp(-0.5 * octaves * octaves);
            if (lag >= minLag && lag <= maxLag && score[lag] > best)
            {
                best = score[lag];
                bestLag = lag;
            }
        }

        if (bestLag < 0 || best <= 1e-9)
        {
            return 0;
        }

        // Parabolic interpolation for a fractional period.
        double y0 = score[bestLag - 1];
        double y1 = score[bestLag];
        double y2 = score[bestLag + 1];
        double denom = y0 - 2 * y1 + y2;
        double offset = Math.Abs(denom) > 1e-12 ? Math.Clamp(0.5 * (y0 - y2) / denom, -0.5, 0.5) : 0;
        double period = bestLag + offset;
        bpm = 60 * fps / period;
        return period;
    }

    private static int[] TrackBeats(float[] onset, double period)
    {
        int n = onset.Length;
        var score = new double[n];
        var back = new int[n];
        int minGap = Math.Max(1, (int)Math.Round(period / 2));
        int maxGap = Math.Max(minGap + 1, (int)Math.Round(period * 2));
        for (int t = 0; t < n; t++)
        {
            double best = double.NegativeInfinity;
            int bestPrev = -1;
            for (int prev = t - maxGap; prev <= t - minGap; prev++)
            {
                if (prev < 0)
                {
                    continue;
                }

                double ratio = Math.Log((t - prev) / period);
                double s = score[prev] - Tightness * ratio * ratio;
                if (s > best)
                {
                    best = s;
                    bestPrev = prev;
                }
            }

            if (bestPrev >= 0 && best > 0)
            {
                score[t] = onset[t] + best;
                back[t] = bestPrev;
            }
            else
            {
                score[t] = onset[t];
                back[t] = -1;
            }
        }

        // Last beat: best score within the final period.
        int lastWindow = Math.Max(0, n - (int)Math.Ceiling(period));
        int last = n - 1;
        double lastScore = double.NegativeInfinity;
        for (int t = lastWindow; t < n; t++)
        {
            if (score[t] > lastScore)
            {
                lastScore = score[t];
                last = t;
            }
        }

        var beats = new List<int>();
        for (int t = last; t >= 0; t = back[t])
        {
            beats.Add(t);
            if (back[t] < 0)
            {
                break;
            }
        }

        beats.Reverse();

        // If the chain stopped early (e.g. silence at the start), extend it backwards on the tempo grid.
        var result = new List<int>();
        double first = beats.Count > 0 ? beats[0] : n - 1;
        for (double t = first - period; t >= 0; t -= period)
        {
            result.Add((int)Math.Round(t));
        }

        result.Reverse();
        result.AddRange(beats);
        return result.ToArray();
    }
}
