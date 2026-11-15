using NAudio.Dsp;
using NAudio.Wave;

namespace SabhaMujic.Audio.Decoding;

/// <summary>
/// Wraps any seekable PCM/float <see cref="WaveStream"/> and presents it as 48 kHz stereo float:
/// converts the sample format with NAudio's converters, maps the channels to stereo
/// (mono is duplicated, surround is down-mixed with ITU-style -3 dB coefficients) and resamples
/// with NAudio's <see cref="WdlResampler"/> (64-tap windowed sinc) when the source rate differs.
/// </summary>
public sealed class NormalizedAudioSource : IAudioSource
{
    private const int StageFrames = 4096;
    private const int ResampleBlockFrames = 4096;
    private const float Minus3Db = 0.70710678f;

    /// <summary>Output frames decoded and discarded before a seek target so the resampler's filter is fully primed.</summary>
    private const int SeekPreRollFrames = 2048;

    private readonly WaveStream _stream;
    private readonly ISampleProvider _samples;
    private readonly int _srcChannels;
    private readonly int _srcRate;
    private readonly int _blockAlign;
    private readonly WdlResampler? _resampler;
    private readonly float[] _stage;
    private long _position;
    private bool _sourceEnded;
    private bool _disposed;
    private float[]? _discard;

    public NormalizedAudioSource(WaveStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanSeek)
        {
            throw new AudioDecoderException("The audio stream is not seekable.");
        }

        var format = stream.WaveFormat;
        _srcChannels = format.Channels;
        _srcRate = format.SampleRate;
        _blockAlign = format.BlockAlign;
        if (_srcChannels < 1 || _srcChannels > 32 || _srcRate < 4000 || _srcRate > 768_000 || _blockAlign < 1)
        {
            throw new AudioDecoderException("The audio format is not supported.");
        }

        try
        {
            _samples = stream.ToSampleProvider();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            throw new AudioDecoderException("The audio encoding is not supported.", ex);
        }

        _stream = stream;
        var sourceFrames = Math.Max(0, stream.Length / _blockAlign);
        TotalFrames = _srcRate == AudioFormat.SampleRate
            ? sourceFrames
            : (long)Math.Floor(sourceFrames * (double)AudioFormat.SampleRate / _srcRate);
        _stage = new float[StageFrames * _srcChannels];

        if (_srcRate != AudioFormat.SampleRate)
        {
            _resampler = new WdlResampler();
            _resampler.SetMode(false, 0, true, 64, 32);
            _resampler.SetFeedMode(false);
            _resampler.SetRates(_srcRate, AudioFormat.SampleRate);
            _resampler.Reset();
        }
    }

    public WaveFormat WaveFormat => AudioFormat.WaveFormat;

    public long TotalFrames { get; }

    public TimeSpan Duration => TimeSpan.FromSeconds(AudioFormat.FramesToSeconds(TotalFrames));

    public long PositionFrames => _position;

    /// <summary>Sample rate of the underlying stream (before resampling).</summary>
    public int SourceSampleRate => _srcRate;

    public int SourceChannels => _srcChannels;

    public void Seek(long frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        frame = Math.Clamp(frame, 0, TotalFrames);
        if (_resampler is null)
        {
            _stream.Position = frame * _blockAlign;
            _sourceEnded = false;
            _position = frame;
            return;
        }

        // Restart the resampler on a frame where the source and 48 kHz grids coincide (so there is no fractional-phase
        // error), some pre-roll before the target, then decode and discard up to the target. The output then matches a
        // continuous read from the start sample-for-sample (up to float rounding), including the filter's history.
        long gcd = Gcd(_srcRate, AudioFormat.SampleRate);
        long outStep = AudioFormat.SampleRate / gcd;
        long srcStep = _srcRate / gcd;
        long start = Math.Max(0, frame - SeekPreRollFrames);
        start -= start % outStep;

        _stream.Position = start / outStep * srcStep * _blockAlign;
        _sourceEnded = false;
        _resampler.Reset();
        _position = start;

        _discard ??= new float[ResampleBlockFrames * AudioFormat.Channels];
        while (_position < frame)
        {
            int frames = (int)Math.Min(frame - _position, ResampleBlockFrames);
            if (Read(_discard.AsSpan(0, frames * AudioFormat.Channels)) == 0)
            {
                break;
            }
        }
    }

    private static long Gcd(long a, long b)
    {
        while (b != 0)
        {
            (a, b) = (b, a % b);
        }

        return a;
    }

    public int Read(Span<float> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        long remaining = TotalFrames - _position;
        int frames = (int)Math.Min(buffer.Length / AudioFormat.Channels, Math.Max(0, remaining));
        if (frames == 0)
        {
            return 0;
        }

        int done = _resampler is null
            ? ReadDirect(buffer[..(frames * 2)])
            : ReadResampled(buffer[..(frames * 2)]);
        _position += done;
        return done * AudioFormat.Channels;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stream.Dispose();
    }

    private int ReadDirect(Span<float> destination)
    {
        int frames = destination.Length / 2;
        int done = 0;
        while (done < frames)
        {
            int n = ReadStereo(destination[(done * 2)..]);
            if (n == 0)
            {
                break;
            }

            done += n;
        }

        return done;
    }

    private int ReadResampled(Span<float> destination)
    {
        var resampler = _resampler!;
        int frames = destination.Length / 2;
        int done = 0;
        while (done < frames)
        {
            int want = Math.Min(frames - done, ResampleBlockFrames);
            int inputNeeded = resampler.ResamplePrepare(want, AudioFormat.Channels, out Span<float> input);
            int got = 0;
            if (!_sourceEnded)
            {
                while (got < inputNeeded)
                {
                    int n = ReadStereo(input.Slice(got * 2, (inputNeeded - got) * 2));
                    if (n == 0)
                    {
                        _sourceEnded = true;
                        break;
                    }

                    got += n;
                }
            }

            // WDL compensates the sinc filter latency itself (verified by NormalizedAudioSourceTests).
            int produced = resampler.ResampleOut(destination[(done * 2)..], got, want, AudioFormat.Channels);
            done += produced;
            if (produced == 0 && _sourceEnded)
            {
                break;
            }
        }

        return done;
    }

    /// <summary>Reads up to <c>destination.Length / 2</c> frames at the source rate, mapped to stereo.</summary>
    private int ReadStereo(Span<float> destination)
    {
        int frames = Math.Min(destination.Length / 2, StageFrames);
        if (frames == 0)
        {
            return 0;
        }

        if (_srcChannels == 2)
        {
            int samples = _samples.Read(destination[..(frames * 2)]);
            return samples / 2;
        }

        var stage = _stage.AsSpan(0, frames * _srcChannels);
        int read = _samples.Read(stage) / _srcChannels;
        if (_srcChannels == 1)
        {
            for (int i = 0; i < read; i++)
            {
                float s = stage[i];
                destination[2 * i] = s;
                destination[2 * i + 1] = s;
            }

            return read;
        }

        // Down-mix (WAVE_FORMAT_EXTENSIBLE default order: FL FR FC LFE BL BR SL SR ...).
        int ch = _srcChannels;
        for (int i = 0; i < read; i++)
        {
            int b = i * ch;
            float l = stage[b];
            float r = stage[b + 1];
            if (ch >= 3)
            {
                float c = stage[b + 2] * Minus3Db;
                l += c;
                r += c;
            }

            // Channel 3 is LFE and is dropped; further channels alternate left/right.
            for (int k = 4; k < ch; k++)
            {
                if ((k & 1) == 0)
                {
                    l += stage[b + k] * Minus3Db;
                }
                else
                {
                    r += stage[b + k] * Minus3Db;
                }
            }

            destination[2 * i] = l;
            destination[2 * i + 1] = r;
        }

        return read;
    }
}
