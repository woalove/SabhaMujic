using SabhaMujic.Audio.Decoding;

namespace SabhaMujic.Audio.Tests;

public class DecodingTests
{
    [Fact]
    public void Wav_at_48k_stereo_float_round_trips_exactly_and_seeks_sample_accurately()
    {
        using var dir = new TempDir();
        var signal = TestSignals.Noise(2, seed: 11);
        var path = TestSignals.WriteWav(dir.File("a.wav"), signal);
        using var source = new WaveFileDecoderFactory().Open(path);

        Assert.Equal(signal.Length / 2, source.TotalFrames);
        Assert.Equal(2.0, source.Duration.TotalSeconds, 6);
        Assert.Equal(AudioFormat.SampleRate, source.WaveFormat.SampleRate);
        Assert.Equal(2, source.WaveFormat.Channels);

        var all = new float[signal.Length];
        Assert.Equal(all.Length, source.Read(all));
        Assert.Equal(signal, all);
        Assert.Equal(0, source.Read(new float[64]));

        source.Seek(12_345);
        Assert.Equal(12_345, source.PositionFrames);
        var part = new float[2000];
        source.Read(part);
        Assert.Equal(signal.AsSpan(12_345 * 2, 2000).ToArray(), part);
    }

    [Fact]
    public void Mono_44k1_pcm16_is_resampled_to_48k_stereo()
    {
        using var dir = new TempDir();
        const int rate = 44_100;
        int frames = rate * 2;
        var mono = new float[frames];
        for (int i = 0; i < frames; i++)
        {
            mono[i] = 0.5f * (float)Math.Sin(2 * Math.PI * 1000 * i / rate);
        }

        var path = TestSignals.WriteWav16(dir.File("mono.wav"), mono, rate, 1);
        using var source = new WaveFileDecoderFactory().Open(path);
        Assert.Equal(96_000, source.TotalFrames);

        var output = new float[source.TotalFrames * 2];
        int read = source.Read(output) / 2;
        Assert.Equal(96_000, read);

        // 1 kHz preserved: count rising zero crossings over the middle second.
        int crossings = 0;
        double peak = 0;
        for (int i = 24_000; i < 72_000; i++)
        {
            Assert.Equal(output[2 * i], output[2 * i + 1]);
            if (output[2 * i - 2] < 0 && output[2 * i] >= 0)
            {
                crossings++;
            }

            peak = Math.Max(peak, Math.Abs(output[2 * i]));
        }

        Assert.InRange(crossings, 999, 1001);
        Assert.InRange(peak, 0.49, 0.51);

        // Seeking a resampled source lands on the same samples (within interpolation error).
        source.Seek(60_000);
        var part = new float[2 * 4800];
        source.Read(part);
        for (int i = 0; i < 4800; i++)
        {
            Assert.True(Math.Abs(part[2 * i] - output[2 * (60_000 + i)]) < 0.01, $"frame {i}");
        }
    }

    [Fact]
    public void Surround_is_downmixed_to_stereo()
    {
        using var dir = new TempDir();
        const int channels = 6;
        int frames = 4800;
        var samples = new float[frames * channels];
        for (int i = 0; i < frames; i++)
        {
            samples[i * channels + 0] = 0.1f; // FL
            samples[i * channels + 1] = 0.2f; // FR
            samples[i * channels + 2] = 0.3f; // C
            samples[i * channels + 3] = 0.9f; // LFE (dropped)
        }

        var path = TestSignals.WriteWav(dir.File("surround.wav"), samples, AudioFormat.SampleRate, channels);
        using var source = new WaveFileDecoderFactory().Open(path);
        var output = new float[frames * 2];
        Assert.Equal(output.Length, source.Read(output));
        Assert.Equal(0.1f + 0.3f * 0.70710678f, output[100], 5);
        Assert.Equal(0.2f + 0.3f * 0.70710678f, output[101], 5);
    }

    [Fact]
    public void Unreadable_or_missing_files_throw_AudioDecoderException_without_the_path()
    {
        using var dir = new TempDir();
        var garbage = dir.File("garbage.wav");
        File.WriteAllBytes(garbage, Enumerable.Range(0, 4096).Select(i => (byte)(i * 7)).ToArray());
        var factory = new WaveFileDecoderFactory();

        var ex1 = Assert.Throws<AudioDecoderException>(() => factory.Open(garbage));
        var ex2 = Assert.Throws<AudioDecoderException>(() => factory.Open(dir.File("missing.wav")));
        Assert.DoesNotContain(dir.Path, ex1.Message);
        Assert.DoesNotContain(dir.Path, ex2.Message);
    }
}
