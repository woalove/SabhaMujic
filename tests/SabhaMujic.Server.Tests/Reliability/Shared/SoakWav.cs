using System.Buffers.Binary;

namespace SabhaMujic.Reliability;

/// <summary>The kinds of generated material in a soak library.</summary>
public enum SoakTrackKind
{
    /// <summary>Intro, an 8 s beat pattern repeated to fill the length, outro: a real Smart Loop region.</summary>
    Loopable,

    /// <summary>A tone with several seconds of leading and trailing silence (exercises trimming).</summary>
    SilencePadded,

    /// <summary>Random notes with no repetition (low loop confidence, whole-track fallback loop).</summary>
    Through,
}

/// <summary>
/// Deterministic 16-bit mono 48 kHz WAV material for soak and reliability runs. Shared by the soak console app
/// (linked source) and <c>SabhaMujic.Server.Tests</c>, so it depends on nothing but the BCL.
/// </summary>
public static class SoakWav
{
    public const int Rate = 48_000;

    /// <summary>Builds a track of <paramref name="seconds"/> of the given kind; <paramref name="seed"/> makes its content unique.</summary>
    public static byte[] Create(SoakTrackKind kind, double seconds, int seed) => kind switch
    {
        SoakTrackKind.Loopable => Loopable(seconds, seed),
        SoakTrackKind.SilencePadded => SilencePadded(seconds, seed),
        _ => Through(seconds, seed),
    };

    /// <summary>Intro + repeated pattern + outro, about <paramref name="seconds"/> long.</summary>
    public static byte[] Loopable(double seconds, int seed)
    {
        var random = new Random(seed);
        var intro = Math.Min(4, seconds / 6);
        var outro = intro;
        const double patternSeconds = 8;
        var repeats = Math.Max(2, (int)Math.Round((seconds - intro - outro) / patternSeconds));
        var pattern = Notes(patternSeconds, random, 0.35f);
        var all = new List<float>((int)(seconds * Rate) + Rate);
        all.AddRange(Notes(intro, random, 0.15f));
        for (var r = 0; r < repeats; r++)
        {
            all.AddRange(pattern);
        }

        all.AddRange(Notes(outro, random, 0.15f));
        return Encode(all.ToArray());
    }

    /// <summary>A gliding tone with 2–5 s of digital silence on both sides (the analyzer trims it).</summary>
    public static byte[] SilencePadded(double seconds, int seed)
    {
        var random = new Random(seed);
        var lead = 2 + random.NextDouble() * 3;
        var tail = 2 + random.NextDouble() * 3;
        var body = Math.Max(1, seconds - lead - tail);
        var hz = 180 + random.Next(0, 400);
        var samples = new float[(int)((lead + body + tail) * Rate)];
        var start = (int)(lead * Rate);
        var length = (int)(body * Rate);
        var phase = 0.0;
        for (var i = 0; i < length; i++)
        {
            var f = hz * (1 + 0.1 * Math.Sin(2 * Math.PI * i / (Rate * 3.0)));
            phase += 2 * Math.PI * f / Rate;
            samples[start + i] = 0.3f * (float)Math.Sin(phase);
        }

        return Encode(samples);
    }

    /// <summary>Random notes for the whole length (no repeated section).</summary>
    public static byte[] Through(double seconds, int seed) => Encode(Notes(seconds, new Random(seed), 0.3f));

    /// <summary>Bytes that are not audio at all.</summary>
    public static byte[] Garbage(int length, int seed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    /// <summary>
    /// A WAV whose header promises <paramref name="seconds"/> of audio but whose data stops after
    /// <paramref name="keepFraction"/> of it (as left by an interrupted copy).
    /// </summary>
    public static byte[] Truncated(double seconds, int seed, double keepFraction = 0.4)
    {
        var full = Through(seconds, seed);
        var keep = 44 + (int)((full.Length - 44) * keepFraction) / 2 * 2;
        return full[..keep];
    }

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
        var data = mono.Length * 2;
        var wav = new byte[44 + data];
        var span = wav.AsSpan();
        "RIFF"u8.CopyTo(span);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], 36 + data);
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
        BinaryPrimitives.WriteInt32LittleEndian(span[40..], data);
        for (var i = 0; i < mono.Length; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(span[(44 + i * 2)..], (short)Math.Clamp(mono[i] * short.MaxValue, short.MinValue, short.MaxValue));
        }

        return wav;
    }
}
