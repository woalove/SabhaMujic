using System.Diagnostics;
using SabhaMujic.Audio.Analysis;
using SabhaMujic.Audio.Decoding;

namespace SabhaMujic.Audio.Tests;

/// <summary>Skipped (reported as skipped) where the sox command-line tool is not installed.</summary>
public sealed class SoxFactAttribute : FactAttribute
{
    private static readonly Lazy<bool> Available = new(() =>
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("sox", "--version") { RedirectStandardOutput = true, RedirectStandardError = true });
            if (process is null)
            {
                return false;
            }

            process.WaitForExit(5000);
            return process.ExitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
    });

    public SoxFactAttribute()
    {
        if (!Available.Value)
        {
            Skip = "sox is not installed.";
        }
    }
}

public class SoxFixtureTests
{
    [SoxFact]
    public void Sox_generated_24_bit_44k1_file_is_decoded_and_trimmed()
    {
        using var dir = new TempDir();
        var path = dir.File("sox.wav");

        // 1.5 s silence + 3 s 440 Hz sine + 2 s silence, 24-bit, 44.1 kHz, stereo.
        RunSox($"-n -r 44100 -b 24 -c 2 \"{path}\" synth 3 sine 440 vol 0.5 pad 1.5 2");

        using var source = new WaveFileDecoderFactory().Open(path);
        Assert.Equal(6.5, source.Duration.TotalSeconds, 2);
        var result = SilenceDetector.Analyze(source);
        Assert.InRange(result.LeadingTrimFrame, TestSignals.Frames(1.5) - 960, TestSignals.Frames(1.5) + 960);
        Assert.InRange(result.TrailingTrimFrame, TestSignals.Frames(4.5) - 960, TestSignals.Frames(4.5) + 960);
    }

    private static void RunSox(string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo("sox", arguments) { RedirectStandardError = true, RedirectStandardOutput = true })!;
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit(30_000);
        Assert.True(process.ExitCode == 0, error);
    }
}
