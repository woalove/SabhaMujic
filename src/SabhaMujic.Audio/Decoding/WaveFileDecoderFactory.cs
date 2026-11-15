using NAudio.Wave;

namespace SabhaMujic.Audio.Decoding;

/// <summary>Cross-platform decoder for RIFF/WAVE files (PCM 8/16/24/32-bit and IEEE float, any rate and channel count).</summary>
public sealed class WaveFileDecoderFactory : IAudioDecoderFactory
{
    public IAudioSource Open(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        WaveFileReader? reader = null;
        try
        {
            reader = new WaveFileReader(path);
            return new NormalizedAudioSource(reader);
        }
        catch (AudioDecoderException)
        {
            reader?.Dispose();
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException
                                       or InvalidDataException or ArgumentException or InvalidOperationException
                                       or NotSupportedException or EndOfStreamException)
        {
            reader?.Dispose();
            throw new AudioDecoderException($"The file could not be read as WAV audio ({ex.GetType().Name}).", ex);
        }
    }
}
