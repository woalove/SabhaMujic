using System.Buffers.Binary;

namespace SabhaMujic.Server.Tests.Integration;

/// <summary>Generates small 16-bit PCM WAV files for end-to-end tests.</summary>
public static class TestWav
{
    public const int Rate = 48_000;

    /// <summary>A sine tone with silent padding (so the analyzer trims something).</summary>
    public static byte[] Tone(double seconds, double hz, double padSeconds = 0.5)
    {
        var pad = (int)(padSeconds * Rate);
        var body = (int)(seconds * Rate);
        var samples = new float[pad + body + pad];
        for (var i = 0; i < body; i++)
        {
            samples[pad + i] = 0.4f * (float)Math.Sin(2 * Math.PI * hz * i / Rate);
        }

        return Encode(samples);
    }

    /// <summary>Intro, a beat-driven pattern repeated <paramref name="repeats"/> times, outro: material with a real loop.</summary>
    public static byte[] Song(double introSeconds = 4, double patternSeconds = 8, int repeats = 4, double outroSeconds = 4, int seed = 11)
    {
        var random = new Random(seed);
        var intro = Notes(introSeconds, random, 0.15f);
        var pattern = Notes(patternSeconds, random, 0.35f);
        var outro = Notes(outroSeconds, random, 0.15f);
        var all = new List<float>(intro);
        for (var r = 0; r < repeats; r++)
        {
            all.AddRange(pattern);
        }

        all.AddRange(outro);
        return Encode(all.ToArray());
    }

    /// <summary>Bytes that are not audio but carry an audio extension's worth of size.</summary>
    public static byte[] Garbage(int length = 64_000) => Enumerable.Range(0, length).Select(i => (byte)(i * 31 + 7)).ToArray();

    private static float[] Notes(double seconds, Random random, float amplitude)
    {
        var samples = new float[(int)(seconds * Rate)];
        var beat = Rate / 2; // 120 BPM
        for (var start = 0; start < samples.Length; start += beat)
        {
            var hz = 220 * Math.Pow(2, random.Next(0, 12) / 12.0);
            var length = Math.Min(beat, samples.Length - start);
            for (var i = 0; i < length; i++)
            {
                var envelope = Math.Exp(-6.0 * i / beat);
                samples[start + i] = (float)(amplitude * envelope * (Math.Sin(2 * Math.PI * hz * i / Rate) + 0.5 * Math.Sin(4 * Math.PI * hz * i / Rate)));
            }
        }

        return samples;
    }

    private static byte[] Encode(float[] mono)
    {
        var data = new byte[mono.Length * 2];
        for (var i = 0; i < mono.Length; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(i * 2), (short)Math.Clamp(mono[i] * short.MaxValue, short.MinValue, short.MaxValue));
        }

        var wav = new byte[44 + data.Length];
        var span = wav.AsSpan();
        "RIFF"u8.CopyTo(span);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], 36 + data.Length);
        "WAVE"u8.CopyTo(span[8..]);
        "fmt "u8.CopyTo(span[12..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(span[20..], 1); // PCM
        BinaryPrimitives.WriteInt16LittleEndian(span[22..], 1); // mono
        BinaryPrimitives.WriteInt32LittleEndian(span[24..], Rate);
        BinaryPrimitives.WriteInt32LittleEndian(span[28..], Rate * 2);
        BinaryPrimitives.WriteInt16LittleEndian(span[32..], 2);
        BinaryPrimitives.WriteInt16LittleEndian(span[34..], 16);
        "data"u8.CopyTo(span[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[40..], data.Length);
        data.CopyTo(span[44..]);
        return wav;
    }
}
