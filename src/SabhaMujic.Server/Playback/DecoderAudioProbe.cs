using SabhaMujic.Audio;
using SabhaMujic.Audio.Decoding;
using SabhaMujic.Server.Application;

namespace SabhaMujic.Server.Playback;

/// <summary>
/// Validates an upload by actually decoding it with the platform decoder (MediaFoundation on Windows, WAV elsewhere):
/// it must open, report a positive length, and yield <see cref="ProbeSeconds"/> of finite samples (or the whole file if
/// shorter). The reported duration is what the decoder says; analysis later corrects it to the exact decoded length.
/// </summary>
public sealed class DecoderAudioProbe(IAudioDecoderFactory decoders, double probeSeconds = 2.0) : IAudioProbe
{
    public double ProbeSeconds { get; } = probeSeconds;

    public Task<AudioProbeResult> ProbeAsync(string path, CancellationToken ct = default) => Task.Run(() => Probe(path, ct), ct);

    private AudioProbeResult Probe(string path, CancellationToken ct)
    {
        try
        {
            using var source = decoders.Open(path);
            if (source.TotalFrames <= 0)
            {
                return AudioProbeResult.Unsupported("the file contains no audio");
            }

            var wanted = (int)Math.Min(source.TotalFrames, AudioFormat.SecondsToFrames(ProbeSeconds)) * AudioFormat.Channels;
            var buffer = new float[Math.Min(wanted, AudioFormat.SampleRate * AudioFormat.Channels / 10)];
            var decoded = 0;
            while (decoded < wanted)
            {
                ct.ThrowIfCancellationRequested();
                var read = source.Read(buffer.AsSpan(0, Math.Min(buffer.Length, wanted - decoded)));
                if (read <= 0)
                {
                    break;
                }

                for (var i = 0; i < read; i++)
                {
                    if (!float.IsFinite(buffer[i]))
                    {
                        return AudioProbeResult.Unsupported("the audio data is corrupt");
                    }
                }

                decoded += read;
            }

            if (decoded == 0)
            {
                return AudioProbeResult.Unsupported("no audio could be decoded");
            }

            var seconds = source.Duration.TotalSeconds > 0 ? source.Duration.TotalSeconds : AudioFormat.FramesToSeconds(source.TotalFrames);
            return AudioProbeResult.Supported(seconds);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (AudioDecoderException ex)
        {
            return AudioProbeResult.Unsupported(ex.Message);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Decoder internals (e.g. NAudio format errors) may mention paths: keep the message generic.
            return AudioProbeResult.Unsupported("the file could not be decoded");
        }
    }
}
