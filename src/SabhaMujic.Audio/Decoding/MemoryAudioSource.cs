using NAudio.Wave;

namespace SabhaMujic.Audio.Decoding;

/// <summary>An in-memory 48 kHz stereo float source (tests, generated signals, short clips).</summary>
public sealed class MemoryAudioSource : IAudioSource
{
    private readonly float[] _samples;
    private long _position;

    /// <param name="interleavedStereo">Interleaved L/R samples at 48 kHz; not copied.</param>
    public MemoryAudioSource(float[] interleavedStereo)
    {
        ArgumentNullException.ThrowIfNull(interleavedStereo);
        _samples = interleavedStereo;
        TotalFrames = interleavedStereo.Length / AudioFormat.Channels;
    }

    public WaveFormat WaveFormat => AudioFormat.WaveFormat;

    public long TotalFrames { get; }

    public TimeSpan Duration => TimeSpan.FromSeconds(AudioFormat.FramesToSeconds(TotalFrames));

    public long PositionFrames => _position;

    public bool IsDisposed { get; private set; }

    public void Seek(long frame) => _position = Math.Clamp(frame, 0, TotalFrames);

    public int Read(Span<float> buffer)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        int frames = (int)Math.Min(buffer.Length / 2, TotalFrames - _position);
        if (frames <= 0)
        {
            return 0;
        }

        _samples.AsSpan((int)(_position * 2), frames * 2).CopyTo(buffer);
        _position += frames;
        return frames * 2;
    }

    public void Dispose() => IsDisposed = true;
}
