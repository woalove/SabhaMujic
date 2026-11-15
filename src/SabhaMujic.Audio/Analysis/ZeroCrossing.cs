using SabhaMujic.Audio.Decoding;

namespace SabhaMujic.Audio.Analysis;

public enum SearchDirection
{
    Both = 0,
    Backward = -1,
    Forward = 1,
}

/// <summary>Zero-crossing search on the mono sum (L + R) of interleaved stereo audio.</summary>
public static class ZeroCrossing
{
    /// <summary>
    /// Returns the frame index nearest to <paramref name="index"/> (within <paramref name="radius"/>) where the mono
    /// signal crosses zero: the sample is exactly zero or has a different sign from the previous one.
    /// Returns -1 when there is none.
    /// </summary>
    public static int FindNearest(ReadOnlySpan<float> stereo, int index, int radius, SearchDirection direction = SearchDirection.Both, bool risingOnly = false)
    {
        int frames = stereo.Length / 2;
        for (int d = 0; d <= radius; d++)
        {
            if (direction != SearchDirection.Forward && IsCrossing(stereo, frames, index - d, risingOnly))
            {
                return index - d;
            }

            if (d > 0 && direction != SearchDirection.Backward && IsCrossing(stereo, frames, index + d, risingOnly))
            {
                return index + d;
            }
        }

        return -1;
    }

    /// <summary>Snaps <paramref name="frame"/> of <paramref name="source"/> to the nearest zero crossing (or returns it unchanged).</summary>
    public static long Snap(IAudioSource source, long frame, int radius, SearchDirection direction = SearchDirection.Both, bool risingOnly = false)
    {
        ArgumentNullException.ThrowIfNull(source);
        long start = Math.Max(0, frame - radius - 1);
        long end = Math.Min(source.TotalFrames, frame + radius + 1);
        if (end - start < 2)
        {
            return frame;
        }

        var buffer = new float[(end - start) * 2];
        source.Seek(start);
        int read = ReadFully(source, buffer) / 2;
        int found = FindNearest(buffer.AsSpan(0, read * 2), (int)(frame - start), radius, direction, risingOnly);
        return found < 0 ? frame : start + found;
    }

    internal static int ReadFully(IAudioSource source, Span<float> buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = source.Read(buffer[total..]);
            if (n <= 0)
            {
                break;
            }

            total += n;
        }

        return total;
    }

    private static bool IsCrossing(ReadOnlySpan<float> stereo, int frames, int i, bool risingOnly)
    {
        if (i < 1 || i >= frames)
        {
            return false;
        }

        float prev = stereo[2 * i - 2] + stereo[2 * i - 1];
        float cur = stereo[2 * i] + stereo[2 * i + 1];
        if (risingOnly)
        {
            return prev < 0 && cur >= 0;
        }

        return cur == 0 || (prev < 0) != (cur < 0);
    }
}
