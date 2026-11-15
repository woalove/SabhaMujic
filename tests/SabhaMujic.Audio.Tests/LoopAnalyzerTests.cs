using System.Diagnostics;
using System.Text.Json;
using SabhaMujic.Audio.Analysis;
using SabhaMujic.Audio.Decoding;
using SabhaMujic.Contracts;
using Xunit.Abstractions;

namespace SabhaMujic.Audio.Tests;

public class LoopAnalyzerTests(ITestOutputHelper output)
{
    private const double PatternSeconds = 12;
    private const double ToleranceSeconds = 0.020;

    [Fact]
    public void Finds_a_whole_multiple_of_the_planted_pattern_after_the_intro()
    {
        using var dir = new TempDir();
        var path = TestSignals.WriteWav(dir.File("song.wav"), TestSignals.Song(introSeconds: 8, patternSeconds: PatternSeconds, repeats: 4, outroSeconds: 6));
        using var source = new WaveFileDecoderFactory().Open(path);

        var (analysis, diagnostics) = LoopAnalyzer.AnalyzeDetailed(source, null, default);
        output.WriteLine(diagnostics.ToString());
        output.WriteLine(analysis.Loop.ToString());

        var loop = analysis.Loop;
        Assert.False(loop.IsFallback, diagnostics.FallbackReason);
        Assert.True(loop.Confidence >= new AnalysisOptions().ConfidenceThreshold);
        Assert.Equal(LoopAnalyzer.AnalysisVersion, analysis.Version);

        double lengthSeconds = AudioFormat.FramesToSeconds(loop.LengthFrames);
        double multiples = Math.Round(lengthSeconds / PatternSeconds);
        Assert.True(multiples >= 1, $"loop length {lengthSeconds:F4} s");
        Assert.True(Math.Abs(lengthSeconds - multiples * PatternSeconds) <= ToleranceSeconds,
            $"loop length {lengthSeconds:F4} s is not a multiple of {PatternSeconds} s within ±20 ms");
        Assert.True(lengthSeconds >= 15, "loop must be at least 15 s");

        Assert.True(AudioFormat.FramesToSeconds(loop.StartFrame) >= 8.0, $"start {AudioFormat.FramesToSeconds(loop.StartFrame):F3} s is inside the intro");
        Assert.True(loop.EndFrame <= analysis.TrailingTrimFrame);
        Assert.True(loop.StartFrame - loop.CrossfadeFrames >= analysis.LeadingTrimFrame);
        Assert.InRange(loop.CrossfadeFrames, AudioFormat.MillisecondsToFrames(50), AudioFormat.MillisecondsToFrames(400));
        Assert.InRange(analysis.TempoBpm, 115, 125);
    }

    [Fact]
    public void Pure_noise_has_low_confidence_and_falls_back_to_the_whole_trimmed_track()
    {
        var source = new MemoryAudioSource(TestSignals.Noise(60, seed: 3));
        var (analysis, diagnostics) = LoopAnalyzer.AnalyzeDetailed(source, null, default);
        output.WriteLine(diagnostics.ToString());
        AssertFallback(analysis);
    }

    [Fact]
    public void A_single_tone_has_low_confidence_and_falls_back()
    {
        var source = new MemoryAudioSource(TestSignals.Concat(TestSignals.Silence(1), TestSignals.Tone(60, 440, 0.5f), TestSignals.Silence(1)));
        var (analysis, diagnostics) = LoopAnalyzer.AnalyzeDetailed(source, null, default);
        output.WriteLine(diagnostics.ToString());
        AssertFallback(analysis);
        Assert.InRange(analysis.LeadingTrimFrame, TestSignals.Rate - 960, TestSignals.Rate + 960);
    }

    [Fact]
    public void Too_short_tracks_fall_back_with_zero_confidence()
    {
        var source = new MemoryAudioSource(TestSignals.Pattern(10, seed: 1));
        var analysis = LoopAnalyzer.Analyze(source);
        AssertFallback(analysis);
        Assert.Equal(0, analysis.Loop.Confidence);
    }

    [Fact]
    public void Analyzing_a_five_minute_track_takes_less_than_five_seconds()
    {
        using var dir = new TempDir();
        // 8 s intro + 24 × 12 s + 4 s outro = 300 s.
        var path = TestSignals.WriteWav(dir.File("long.wav"), TestSignals.Song(8, PatternSeconds, 24, 4));
        using var source = new WaveFileDecoderFactory().Open(path);
        Assert.Equal(300.0, source.Duration.TotalSeconds, 3);

        LoopAnalyzer.Analyze(new MemoryAudioSource(TestSignals.Song(2, 12, 2, 2))); // JIT warm-up
        var sw = Stopwatch.StartNew();
        var (analysis, diagnostics) = LoopAnalyzer.AnalyzeDetailed(source, null, default);
        sw.Stop();
        output.WriteLine($"5-minute analysis took {sw.Elapsed.TotalMilliseconds:F0} ms; {diagnostics}");
        Console.WriteLine($"[perf] 5-minute 48 kHz stereo analysis: {sw.Elapsed.TotalMilliseconds:F0} ms");
        // Budget: 5 s for optimized builds (CI runs Release; measured ~0.5 s). Debug builds run the same code
        // unoptimized and often alongside other test assemblies, so they get headroom instead of failing on load.
#if DEBUG
        var budget = TimeSpan.FromSeconds(15);
#else
        var budget = TimeSpan.FromSeconds(5);
#endif
        Assert.True(sw.Elapsed < budget, $"took {sw.Elapsed} (budget {budget})");
        Assert.False(analysis.Loop.IsFallback);
        double length = AudioFormat.FramesToSeconds(analysis.Loop.LengthFrames);
        Assert.True(Math.Abs(length - Math.Round(length / PatternSeconds) * PatternSeconds) <= ToleranceSeconds, $"length {length}");
    }

    [Fact]
    public void Analysis_round_trips_through_json_and_converts_to_the_contract_dto()
    {
        var analysis = LoopAnalyzer.Analyze(new MemoryAudioSource(TestSignals.Song(4, 12, 3, 2)));
        var json = JsonSerializer.Serialize(analysis);
        var back = JsonSerializer.Deserialize<TrackAnalysis>(json);
        Assert.NotNull(back);
        Assert.Equal(analysis.Loop, back!.Loop);
        Assert.Equal(analysis.Gaps, back.Gaps);
        Assert.Equal(analysis.LeadingTrimFrame, back.LeadingTrimFrame);

        var dto = analysis.ToLoopRegionDto();
        Assert.Equal(AudioFormat.FramesToSeconds(analysis.Loop.StartFrame), dto.StartSeconds, 9);
        Assert.Equal(AudioFormat.FramesToSeconds(analysis.Loop.EndFrame), dto.EndSeconds, 9);
        Assert.Equal(analysis.Loop.Confidence, dto.Confidence);
        Assert.False(dto.IsManualOverride);
    }

    [Fact]
    public void Manual_override_is_clamped_into_the_trimmed_audio()
    {
        var analysis = new TrackAnalysis
        {
            Version = 1,
            TotalFrames = TestSignals.Frames(30),
            LeadingTrimFrame = TestSignals.Frames(1),
            TrailingTrimFrame = TestSignals.Frames(29),
            Loop = LoopAnalyzer.CreateFallbackLoop(TestSignals.Frames(1), TestSignals.Frames(29), TestSignals.Frames(2), 0.1),
        };

        var overridden = analysis.WithLoopOverride(new LoopRegionDto(1.1, 40, 0.5, 1, false));
        Assert.True(overridden.Loop.IsManualOverride);
        Assert.Equal(TestSignals.Frames(29), overridden.Loop.EndFrame);
        Assert.Equal(TestSignals.Frames(1.1), overridden.Loop.StartFrame);
        Assert.Equal(TestSignals.Frames(0.1), overridden.Loop.CrossfadeFrames); // limited by the leading trim
        Assert.Same(analysis, analysis.WithLoopOverride(null));
        Assert.Throws<SabhaMujicException>(() => analysis.WithLoopOverride(new LoopRegionDto(5, 4, 0.1, 1, true)));
    }

    private static void AssertFallback(TrackAnalysis analysis)
    {
        var loop = analysis.Loop;
        Assert.True(loop.IsFallback);
        Assert.True(loop.Confidence < new AnalysisOptions().ConfidenceThreshold, $"confidence {loop.Confidence}");
        Assert.Equal(analysis.TrailingTrimFrame, loop.EndFrame);
        Assert.Equal(analysis.LeadingTrimFrame + loop.CrossfadeFrames, loop.StartFrame);
        Assert.True(loop.CrossfadeFrames > AudioFormat.MillisecondsToFrames(400), "fallback uses a longer crossfade");
    }
}
