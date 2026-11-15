using NAudio.Wave;

namespace SabhaMujic.Audio;

/// <summary>
/// The single internal mix format: 48 kHz, stereo, 32-bit IEEE float, interleaved.
/// Every decoder output, analysis position and mixer frame count uses this format.
/// </summary>
public static class AudioFormat
{
    public const int SampleRate = 48_000;

    public const int Channels = 2;

    /// <summary>A shared instance; treat it as read-only.</summary>
    public static WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels);

    public static double FramesToSeconds(long frames) => frames / (double)SampleRate;

    public static long SecondsToFrames(double seconds) => (long)Math.Round(seconds * SampleRate);

    public static int MillisecondsToFrames(double milliseconds) => (int)Math.Round(milliseconds * SampleRate / 1000.0);
}
