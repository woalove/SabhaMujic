using NAudio.Wave;
using SabhaMujic.Audio;

namespace SabhaMujic.Audio.Tests;

/// <summary>Synthetic fixtures generated at test time (no binary files are committed).</summary>
internal static class TestSignals
{
    public const int Rate = AudioFormat.SampleRate;

    public static float[] Silence(double seconds) => new float[Frames(seconds) * 2];

    public static float[] Tone(double seconds, double hz, float amplitude = 0.5f, double phase = 0)
    {
        int n = Frames(seconds);
        var s = new float[n * 2];
        for (int i = 0; i < n; i++)
        {
            float v = amplitude * (float)Math.Sin(2 * Math.PI * hz * i / Rate + phase);
            s[2 * i] = v;
            s[2 * i + 1] = v;
        }

        return s;
    }

    /// <summary>Independent uniform white noise on both channels.</summary>
    public static float[] Noise(double seconds, int seed, float amplitude = 0.25f)
    {
        var rng = new Random(seed);
        int n = Frames(seconds);
        var s = new float[n * 2];
        for (int i = 0; i < s.Length; i++)
        {
            s[i] = amplitude * (float)(rng.NextDouble() * 2 - 1);
        }

        return s;
    }

    public static float[] Concat(params float[][] parts)
    {
        var result = new float[parts.Sum(p => p.Length)];
        int offset = 0;
        foreach (var p in parts)
        {
            p.CopyTo(result, offset);
            offset += p.Length;
        }

        return result;
    }

    /// <summary>
    /// "Song" for loop detection: <paramref name="introSeconds"/> of a different chord with noise and no beat,
    /// then a <paramref name="patternSeconds"/> pattern (4 chords, a kick on every beat at 120 BPM and a melody
    /// note per beat) repeated <paramref name="repeats"/> times sample-identically, then an outro chord that fades out.
    /// </summary>
    public static float[] Song(double introSeconds = 8, double patternSeconds = 12, int repeats = 4, double outroSeconds = 6, int seed = 7)
    {
        var intro = Intro(introSeconds, seed);
        var pattern = Pattern(patternSeconds, seed);
        var outro = Outro(outroSeconds);
        var parts = new List<float[]> { intro };
        for (int r = 0; r < repeats; r++)
        {
            parts.Add(pattern);
        }

        parts.Add(outro);
        return Concat(parts.ToArray());
    }

    public static float[] Pattern(double seconds, int seed)
    {
        const double beat = 0.5;
        double[][] chords =
        [
            [261.63, 329.63, 392.00], // C
            [220.00, 261.63, 329.63], // Am
            [174.61, 220.00, 261.63], // F
            [196.00, 246.94, 293.66], // G
        ];
        double[] scale = [523.25, 587.33, 659.25, 783.99, 880.00, 1046.5, 1174.7, 1318.5];
        int beats = (int)Math.Round(seconds / beat);
        var rng = new Random(seed);
        var melody = Enumerable.Range(0, beats).Select(_ => scale[rng.Next(scale.Length)]).ToArray();

        int n = Frames(seconds);
        var s = new float[n * 2];
        double chordLength = seconds / chords.Length;
        for (int i = 0; i < n; i++)
        {
            double t = (double)i / Rate;
            var chord = chords[Math.Min(chords.Length - 1, (int)(t / chordLength))];
            double v = 0;
            foreach (var f in chord)
            {
                v += 0.07 * Math.Sin(2 * Math.PI * f * t) + 0.02 * Math.Sin(2 * Math.PI * 2 * f * t);
            }

            int b = Math.Min(beats - 1, (int)(t / beat));
            double tb = t - b * beat;
            v += 0.12 * Math.Exp(-tb / 0.25) * Math.Sin(2 * Math.PI * melody[b] * t);
            v += 0.35 * Math.Exp(-tb / 0.06) * Math.Sin(2 * Math.PI * 55 * tb);
            float x = (float)v;
            s[2 * i] = x;
            s[2 * i + 1] = x * 0.9f;
        }

        return s;
    }

    private static float[] Intro(double seconds, int seed)
    {
        var rng = new Random(seed + 1000);
        int n = Frames(seconds);
        var s = new float[n * 2];
        double[] chord = [146.83, 174.61, 220.00]; // Dm
        for (int i = 0; i < n; i++)
        {
            double t = (double)i / Rate;
            double swell = Math.Min(1, t / 2);
            double v = 0;
            foreach (var f in chord)
            {
                v += 0.08 * swell * Math.Sin(2 * Math.PI * f * t);
            }

            v += 0.01 * (rng.NextDouble() * 2 - 1);
            s[2 * i] = (float)v;
            s[2 * i + 1] = (float)v;
        }

        return s;
    }

    private static float[] Outro(double seconds)
    {
        int n = Frames(seconds);
        var s = new float[n * 2];
        double[] chord = [164.81, 207.65, 246.94]; // E
        for (int i = 0; i < n; i++)
        {
            double t = (double)i / Rate;
            double fade = 1 - t / seconds;
            double v = 0;
            foreach (var f in chord)
            {
                v += 0.08 * fade * Math.Sin(2 * Math.PI * f * t);
            }

            s[2 * i] = (float)v;
            s[2 * i + 1] = (float)v;
        }

        return s;
    }

    public static int Frames(double seconds) => (int)Math.Round(seconds * Rate);

    /// <summary>Writes 32-bit float WAV at 48 kHz stereo (or the given format) with NAudio's <see cref="WaveFileWriter"/>.</summary>
    public static string WriteWav(string path, float[] interleaved, int sampleRate = Rate, int channels = 2)
    {
        using var writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels));
        writer.WriteSamples(interleaved, 0, interleaved.Length);
        return path;
    }

    /// <summary>Writes 16-bit PCM WAV.</summary>
    public static string WriteWav16(string path, float[] interleaved, int sampleRate, int channels)
    {
        using var writer = new WaveFileWriter(path, new WaveFormat(sampleRate, 16, channels));
        foreach (var sample in interleaved)
        {
            writer.WriteSample(sample);
        }

        return path;
    }
}

/// <summary>A temp directory deleted on dispose.</summary>
internal sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sabhamujic-audio-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
