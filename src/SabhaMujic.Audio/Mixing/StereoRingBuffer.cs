namespace SabhaMujic.Audio.Mixing;

/// <summary>
/// Lock-free single-producer/single-consumer ring of interleaved stereo frames.
/// The producer (decode thread) writes straight into the ring; the consumer (render thread) copies out.
/// </summary>
internal sealed class StereoRingBuffer
{
    private readonly float[] _data;
    private long _written;
    private long _read;

    public StereoRingBuffer(int capacityFrames)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacityFrames, 1);
        CapacityFrames = capacityFrames;
        _data = new float[capacityFrames * 2];
    }

    public int CapacityFrames { get; }

    public int AvailableFrames => (int)(Volatile.Read(ref _written) - Volatile.Read(ref _read));

    public int FreeFrames => CapacityFrames - AvailableFrames;

    /// <summary>Producer: spans for up to <paramref name="frames"/> frames (must be ≤ <see cref="FreeFrames"/>).</summary>
    public void GetWriteSpans(int frames, out Span<float> first, out Span<float> second)
    {
        int start = (int)(Volatile.Read(ref _written) % CapacityFrames);
        int firstFrames = Math.Min(frames, CapacityFrames - start);
        first = _data.AsSpan(start * 2, firstFrames * 2);
        second = _data.AsSpan(0, (frames - firstFrames) * 2);
    }

    /// <summary>Producer: publishes <paramref name="frames"/> written frames.</summary>
    public void CommitWrite(int frames) => Volatile.Write(ref _written, _written + frames);

    /// <summary>Consumer: copies up to <c>destination.Length / 2</c> frames. Returns frames copied.</summary>
    public int Read(Span<float> destination)
    {
        long read = _read;
        int frames = (int)Math.Min(destination.Length / 2, Volatile.Read(ref _written) - read);
        if (frames <= 0)
        {
            return 0;
        }

        int start = (int)(read % CapacityFrames);
        int firstFrames = Math.Min(frames, CapacityFrames - start);
        _data.AsSpan(start * 2, firstFrames * 2).CopyTo(destination);
        if (firstFrames < frames)
        {
            _data.AsSpan(0, (frames - firstFrames) * 2).CopyTo(destination[(firstFrames * 2)..]);
        }

        Volatile.Write(ref _read, read + frames);
        return frames;
    }
}
