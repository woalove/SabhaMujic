using SabhaMujic.Audio.Decoding;

namespace SabhaMujic.Audio.Mixing;

/// <summary>
/// One read head into a decoded track: plays <c>[StartFrame, EndFrame)</c> of its source.
/// A <see cref="VoiceFeeder"/> thread decodes ahead into a ring buffer (about one second), so the render thread
/// only copies memory and never touches the decoder or the disk. Seeking or looping uses a new voice.
/// </summary>
public sealed class PlaybackVoice : IDisposable
{
    public const int DefaultBufferFrames = AudioFormat.SampleRate;
    private const int ChunkFrames = 8192;

    private readonly object _decodeLock = new();
    private readonly IAudioSource _source;
    private readonly StereoRingBuffer _ring;
    private long _decodePosition;
    private long _consumed;
    private volatile bool _decodeComplete;
    private volatile bool _disposed;
    private volatile Exception? _fault;
    private VoiceFeeder? _feeder;

    /// <summary>Creates the voice and seeks the source (call off the render thread). The voice owns <paramref name="source"/>.</summary>
    public PlaybackVoice(IAudioSource source, long startFrame, long endFrame, int bufferFrames = DefaultBufferFrames, object? tag = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfLessThan(bufferFrames, 1024);
        _source = source;
        StartFrame = Math.Clamp(startFrame, 0, source.TotalFrames);
        EndFrame = Math.Clamp(endFrame, StartFrame, source.TotalFrames);
        Tag = tag;
        _ring = new StereoRingBuffer(bufferFrames);
        _decodePosition = StartFrame;
        source.Seek(StartFrame);
        if (EndFrame <= StartFrame)
        {
            _decodeComplete = true;
        }
    }

    /// <summary>Owner data (the engine stores its track session here).</summary>
    public object? Tag { get; }

    public long StartFrame { get; }

    /// <summary>Exclusive end frame; decoding stops here.</summary>
    public long EndFrame { get; }

    /// <summary>Source frame the render thread will play next.</summary>
    public long Position => StartFrame + Volatile.Read(ref _consumed);

    /// <summary>Frames until <see cref="EndFrame"/> (may be reached earlier if the file is shorter than reported).</summary>
    public long RemainingFrames => EndFrame - Position;

    public int BufferedFrames => _ring.AvailableFrames;

    public int BufferCapacityFrames => _ring.CapacityFrames;

    /// <summary>The decoder will produce nothing more (end reached, decode error or disposed).</summary>
    public bool IsDecodeComplete => _decodeComplete;

    /// <summary>Everything decoded has been played.</summary>
    public bool IsDrained => _decodeComplete && _ring.AvailableFrames == 0;

    public bool IsFaulted => _fault is not null;

    /// <summary>The decode error, if any.</summary>
    public Exception? Fault => _fault;

    public bool IsDisposed => _disposed;

    internal VoiceFeeder? Feeder
    {
        get => Volatile.Read(ref _feeder);
        set => Volatile.Write(ref _feeder, value);
    }

    /// <summary>
    /// Decodes one chunk into the ring if there is room. Called by the feeder thread (or directly in tests).
    /// Returns true if any frames were written. Decode errors fault the voice instead of throwing.
    /// </summary>
    public bool FillChunk(int maxFrames = ChunkFrames)
    {
        if (_decodeComplete || _disposed)
        {
            return false;
        }

        lock (_decodeLock)
        {
            if (_decodeComplete || _disposed)
            {
                return false;
            }

            long remaining = EndFrame - _decodePosition;
            if (remaining <= 0)
            {
                _decodeComplete = true;
                return false;
            }

            int free = _ring.FreeFrames;
            int want = (int)Math.Min(Math.Min(free, maxFrames), remaining);
            if (want < Math.Min(1024, remaining))
            {
                return false;
            }

            try
            {
                _ring.GetWriteSpans(want, out var first, out var second);
                int got = ReadFully(first);
                if (got == first.Length / 2 && second.Length > 0)
                {
                    got += ReadFully(second);
                }

                _ring.CommitWrite(got);
                _decodePosition += got;
                if (got < want || _decodePosition >= EndFrame)
                {
                    _decodeComplete = true;
                }

                return got > 0;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _fault = ex;
                _decodeComplete = true;
                return false;
            }
        }
    }

    /// <summary>Fills the whole buffer synchronously (tests and tools).</summary>
    public void Prefill()
    {
        while (FillChunk())
        {
        }
    }

    /// <summary>Blocks (not on the render thread!) until at least <paramref name="frames"/> are buffered or decoding completed.</summary>
    public bool WaitForBuffered(int frames, TimeSpan timeout)
    {
        // FillChunk only writes when at least 1024 frames are free, so never wait for more than that allows.
        frames = Math.Min(frames, _ring.CapacityFrames - 1024);
        long deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (_ring.AvailableFrames < frames && !_decodeComplete)
        {
            if (Feeder is null)
            {
                if (!FillChunk())
                {
                    break;
                }

                continue;
            }

            if (Environment.TickCount64 > deadline)
            {
                return false;
            }

            Feeder.Signal();
            Thread.Sleep(1);
        }

        return true;
    }

    /// <summary>Render thread: copies up to <c>destination.Length / 2</c> buffered frames. Never blocks or allocates.</summary>
    internal int Read(Span<float> destination)
    {
        int frames = _ring.Read(destination);
        if (frames > 0)
        {
            Volatile.Write(ref _consumed, _consumed + frames);
        }

        if (!_decodeComplete && _ring.AvailableFrames < _ring.CapacityFrames / 2)
        {
            Feeder?.Signal();
        }

        return frames;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Feeder?.Unregister(this);
        lock (_decodeLock)
        {
            _disposed = true;
            _decodeComplete = true;
            _source.Dispose();
        }
    }

    private int ReadFully(Span<float> destination)
    {
        int total = 0;
        while (total < destination.Length)
        {
            int n = _source.Read(destination[total..]);
            if (n <= 0)
            {
                break;
            }

            total += n;
        }

        return total / 2;
    }
}
