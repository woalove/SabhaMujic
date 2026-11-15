using NAudio.Wave;

namespace SabhaMujic.Audio.Decoding;

/// <summary>
/// A seekable decoded audio stream, always normalized to <see cref="AudioFormat"/> (48 kHz stereo float).
/// Positions are frames (one sample per channel). Not thread-safe: one reader at a time.
/// </summary>
public interface IAudioSource : ISampleProvider, IDisposable
{
    /// <summary>Length in frames at 48 kHz. Compressed formats may report an estimate; reads end at the real end.</summary>
    long TotalFrames { get; }

    TimeSpan Duration { get; }

    /// <summary>The frame the next <see cref="ISampleProvider.Read"/> starts at.</summary>
    long PositionFrames { get; }

    /// <summary>Repositions to <paramref name="frame"/> (clamped to [0, TotalFrames]). Exact for 48 kHz sources.</summary>
    void Seek(long frame);
}

/// <summary>Opens audio files as normalized <see cref="IAudioSource"/>s.</summary>
public interface IAudioDecoderFactory
{
    /// <summary>Opens <paramref name="path"/>. Throws <see cref="AudioDecoderException"/> if it cannot be decoded.</summary>
    IAudioSource Open(string path);
}

/// <summary>A file could not be opened or decoded. The message never contains the file path.</summary>
public sealed class AudioDecoderException : Exception
{
    public AudioDecoderException(string message) : base(message)
    {
    }

    public AudioDecoderException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
