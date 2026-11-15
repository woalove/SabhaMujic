using SabhaMujic.Audio.Analysis;
using SabhaMujic.Audio.Decoding;

namespace SabhaMujic.Audio.Tests;

public class SilenceDetectorTests
{
    private const int Tolerance = 960; // ±20 ms

    [Theory]
    [InlineData(0.0)]
    [InlineData(1.3)] // starts and ends mid-cycle: trims must snap to zero crossings
    public void Trims_leading_and_trailing_silence_within_20_ms(double phase)
    {
        using var dir = new TempDir();
        var signal = TestSignals.Concat(TestSignals.Silence(2), TestSignals.Tone(5, 440, 0.5f, phase), TestSignals.Silence(3));
        var path = TestSignals.WriteWav(dir.File("tone.wav"), signal);
        using var source = new WaveFileDecoderFactory().Open(path);

        var result = SilenceDetector.Analyze(source);

        Assert.False(result.IsSilent);
        Assert.Equal(TestSignals.Frames(10), result.TotalFrames);
        Assert.InRange(result.LeadingTrimFrame, TestSignals.Frames(2) - Tolerance, TestSignals.Frames(2) + Tolerance);
        Assert.InRange(result.TrailingTrimFrame, TestSignals.Frames(7) - Tolerance, TestSignals.Frames(7) + Tolerance);
        Assert.Empty(result.Gaps);
        AssertZeroCrossing(signal, result.LeadingTrimFrame);
        AssertZeroCrossing(signal, result.TrailingTrimFrame);

        // Nothing audible is cut.
        Assert.True(result.LeadingTrimFrame <= TestSignals.Frames(2));
        Assert.True(result.TrailingTrimFrame >= TestSignals.Frames(7));
    }

    [Fact]
    public void Reports_internal_gaps_of_at_least_the_minimum_length()
    {
        var signal = TestSignals.Concat(
            TestSignals.Tone(1, 300), TestSignals.Silence(0.5),
            TestSignals.Tone(1, 300), TestSignals.Silence(0.1),
            TestSignals.Tone(1, 300));
        var result = SilenceDetector.Analyze(new MemoryAudioSource(signal));

        var gap = Assert.Single(result.Gaps);
        Assert.InRange(gap.StartFrame, TestSignals.Frames(1) - Tolerance, TestSignals.Frames(1) + Tolerance);
        Assert.InRange(gap.EndFrame, TestSignals.Frames(1.5) - Tolerance, TestSignals.Frames(1.5) + Tolerance);
    }

    [Fact]
    public void Threshold_and_minimum_are_configurable()
    {
        var quiet = TestSignals.Concat(TestSignals.Silence(1), TestSignals.Tone(2, 300, 0.0015f), TestSignals.Silence(1)); // ≈ -59 dBFS RMS
        Assert.True(SilenceDetector.Analyze(new MemoryAudioSource(quiet)).IsSilent);

        var sensitive = SilenceDetector.Analyze(new MemoryAudioSource(quiet), new SilenceDetectorOptions { ThresholdDb = -70 });
        Assert.False(sensitive.IsSilent);
        Assert.InRange(sensitive.LeadingTrimFrame, TestSignals.Frames(1) - Tolerance, TestSignals.Frames(1) + Tolerance);

        var gapped = TestSignals.Concat(TestSignals.Tone(1, 300), TestSignals.Silence(0.2), TestSignals.Tone(1, 300));
        Assert.Empty(SilenceDetector.Analyze(new MemoryAudioSource(gapped)).Gaps);
        Assert.Single(SilenceDetector.Analyze(new MemoryAudioSource(gapped), new SilenceDetectorOptions { MinSilenceMilliseconds = 100 }).Gaps);
    }

    [Fact]
    public void Streaming_in_odd_chunks_gives_the_same_result_as_one_pass()
    {
        var signal = TestSignals.Concat(TestSignals.Silence(0.7), TestSignals.Noise(2, 5), TestSignals.Silence(0.6), TestSignals.Tone(1, 500), TestSignals.Silence(0.3));
        var whole = new SilenceDetector();
        whole.Process(signal);
        var expected = whole.Complete();

        var chunked = new SilenceDetector();
        var rng = new Random(1);
        int offset = 0;
        while (offset < signal.Length)
        {
            int n = Math.Min(signal.Length - offset, 2 * rng.Next(1, 3000));
            chunked.Process(signal.AsSpan(offset, n));
            offset += n;
        }

        var actual = chunked.Complete();
        Assert.Equal(expected.LeadingTrimFrame, actual.LeadingTrimFrame);
        Assert.Equal(expected.TrailingTrimFrame, actual.TrailingTrimFrame);
        Assert.Equal(expected.Gaps, actual.Gaps);
        Assert.Single(actual.Gaps);
    }

    [Fact]
    public void All_silent_input_is_reported_as_silent_without_trimming()
    {
        var result = SilenceDetector.Analyze(new MemoryAudioSource(TestSignals.Silence(3)));
        Assert.True(result.IsSilent);
        Assert.Equal(0, result.LeadingTrimFrame);
        Assert.Equal(TestSignals.Frames(3), result.TrailingTrimFrame);
    }

    private static void AssertZeroCrossing(float[] signal, long frame)
    {
        float Mono(long i) => i < 0 || i * 2 >= signal.Length ? 0 : signal[2 * i] + signal[2 * i + 1];
        float cur = Mono(frame);
        float prev = Mono(frame - 1);
        Assert.True(cur == 0 || (prev < 0) != (cur < 0), $"frame {frame} is not a zero crossing ({prev}, {cur})");
    }
}
