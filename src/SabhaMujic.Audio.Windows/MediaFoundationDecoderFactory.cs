using System.Runtime.Versioning;
using NAudio.Wave;
using SabhaMujic.Audio.Decoding;

namespace SabhaMujic.Audio.Windows;

/// <summary>
/// Decodes anything Windows Media Foundation can read (MP3, M4A/AAC, WMA, FLAC, WAV, ...) and normalizes it to
/// 48 kHz stereo float. Media Foundation is asked for float output at the file's own rate and channel count
/// (falling back to PCM output where the source reader cannot provide float, e.g. some uncompressed sources);
/// resampling and channel mapping are done by <see cref="NormalizedAudioSource"/> (NAudio's WDL resampler).
/// Seeks are exact: NAudio 3.3's <see cref="MediaFoundationReader"/> reads forward from the decoder's seek point.
/// The reader re-creates its COM source reader on the first read, so it may be opened on one thread (the engine's
/// control thread) and read on another (the decode thread).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MediaFoundationDecoderFactory : IAudioDecoderFactory
{
    public IAudioSource Open(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (!File.Exists(path))
        {
            throw new AudioDecoderException("The audio file does not exist.");
        }

        Exception? firstError = null;
        foreach (bool requestFloat in new[] { true, false })
        {
            MediaFoundationReader? reader = null;
            try
            {
                reader = new MediaFoundationReader(path, new MediaFoundationReader.MediaFoundationReaderSettings
                {
                    RequestFloatOutput = requestFloat,
                    RepositionInRead = true,
                    SingleReaderObject = false,
                });
                if (reader.Length <= 0)
                {
                    throw new AudioDecoderException("The audio length could not be determined.");
                }

                return new NormalizedAudioSource(reader);
            }
            catch (AudioDecoderException)
            {
                reader?.Dispose();
                throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                reader?.Dispose();
                firstError ??= ex;
            }
        }

        throw new AudioDecoderException($"The file could not be decoded by Media Foundation ({firstError!.GetType().Name}).", firstError);
    }
}
