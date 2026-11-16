using System.Diagnostics;
using SabhaMujic.Audio.Analysis;
using SabhaMujic.Audio.Engine;
using SabhaMujic.Audio.Output;
using SabhaMujic.Contracts;
using Xunit.Abstractions;

namespace SabhaMujic.Audio.Tests;

public class RenderPathTests(ITestOutputHelper log)
{
    [Fact]
    public async Task Steady_state_rendering_through_the_engine_does_not_allocate()
    {
        using var h = new EngineHarness();
        long total = TestSignals.Frames(30);
        var loopAnalysis = new TrackAnalysis
        {
            Version = LoopAnalyzer.AnalysisVersion,
            TotalFrames = total,
            LeadingTrimFrame = 0,
            TrailingTrimFrame = total,
            Loop = new LoopRegion(TestSignals.Frames(4), TestSignals.Frames(7), TestSignals.Frames(0.2), 1),
        };
        var first = h.AddTone(8, 440);
        var second = h.AddTone(30, 550, loopAnalysis);
        await h.Engine.SetCrossfadeAsync(2);
        var queue = await h.Engine.EnqueueAsync([first, second], false);
        await h.Engine.PlayQueueItemAsync(queue.Items[0].QueueItemId);

        // Warm-up: JIT, ring buffers wrapped, first command/event paths exercised.
        await h.PlayFor(2);

        // 10 s measured on the rendering thread: includes a queue crossfade (at 6 s) and, after switching to
        // Smart Loop, several loop seams. The control and decode threads may allocate; the render path must not.
        // Only the Pump calls are measured; the engine is synced (on other threads) between 1 s chunks.
        var buffer = new float[NullRealtimeOutput.BlockFrames * 2];
        long allocated = await MeasurePumpAllocations(h, buffer, seconds: 10);
        Assert.Equal(second, h.State.CurrentTrackId);
        Assert.Equal(PlaybackStatus.Playing, h.State.Status);

        await h.Engine.SetLoopModeAsync(LoopMode.SmartLoop);
        await h.PlayFor(1);
        long allocatedLoop = await MeasurePumpAllocations(h, buffer, seconds: 10);
        Assert.Equal(second, h.State.CurrentTrackId);
        Assert.InRange(h.State.PositionSeconds, 3.8, 7.0); // still inside the 4-7 s loop after 10 s: seams happened

        log.WriteLine($"Render thread allocations: {allocated} bytes over 10 s with a crossfade; {allocatedLoop} bytes over 10 s with loop seams");
        Console.WriteLine($"[alloc] render thread: {allocated} B / 10 s (crossfade), {allocatedLoop} B / 10 s (loop seams)");
        Assert.True(allocated <= 256, $"{allocated} bytes allocated");
        Assert.True(allocatedLoop <= 256, $"{allocatedLoop} bytes allocated");
    }

    private static async Task<long> MeasurePumpAllocations(EngineHarness h, float[] buffer, int seconds)
    {
        long total = 0;
        int blocksPerSecond = AudioFormat.SampleRate / NullRealtimeOutput.BlockFrames;
        for (int s = 0; s < seconds; s++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < blocksPerSecond; i++)
            {
                h.Output.Pump(buffer);
            }

            total += GC.GetAllocatedBytesForCurrentThread() - before;
            await h.Sync();
        }

        return total;
    }

    /// <summary>
    /// The output's pull rate, measured from frames actually rendered between two samples taken after the first audio
    /// was heard. A busy machine (a late timer, a slow first decode) cannot fail it: under load the realtime output
    /// catches up to its clock, and a 30 s track at 20x would otherwise end inside one overslept 1.5 s delay (CI run 24
    /// saw "position 0.00 s after 2.42 s": the track had ended).
    /// </summary>
    [Fact]
    public async Task Realtime_null_output_pulls_at_the_speed_factor()
    {
        var output = new NullRealtimeOutput(speedFactor: 20);
        using var dir = new TempDir();
        var resolver = new FakeResolver();
        var track = resolver.Add(TestSignals.WriteWav(dir.File("t.wav"), TestSignals.Tone(30, 440)));
        using var engine = new PlaybackEngine(output, new Decoding.WaveFileDecoderFactory(), resolver, new PlaybackEngineOptions { AnalyzeMissingTracks = false });
        await engine.SetLoopModeAsync(LoopMode.RepeatQueue); // keeps playing however late the samples are taken
        await engine.PlayTrackAsync(track);
        // At 20x a 30 s track reaches its default 6 s crossfade (into its own repeat) 1.2 s after starting, so a slow
        // runner can legitimately observe Crossfading here (CI run 29).
        Assert.Contains(engine.GetState().Status, new[] { PlaybackStatus.Playing, PlaybackStatus.Crossfading });

        // Wait (generously) for the first rendered audio, then time from there.
        var wait = Stopwatch.StartNew();
        while (engine.GetState().PositionSeconds <= 0 && wait.Elapsed < TimeSpan.FromSeconds(20))
        {
            await Task.Delay(10);
        }

        Assert.True(engine.GetState().PositionSeconds > 0, $"playback did not start within {wait.Elapsed.TotalSeconds:F1} s");
        long frames0 = output.FramesRendered;
        var sw = Stopwatch.StartNew();
        await Task.Delay(500);
        long frames1 = output.FramesRendered;
        double elapsed = sw.Elapsed.TotalSeconds;
        double rate = (frames1 - frames0) / (double)AudioFormat.SampleRate / elapsed;
        log.WriteLine($"{rate:F1}x over {elapsed:F2} s (first audio after {wait.Elapsed.TotalSeconds:F2} s)");
        // Upper bound allows the output catching up on a backlog from before the window (it renders to its clock).
        Assert.InRange(rate, 20 * 0.6, 20 * 1.6);
        Assert.True(engine.GetState().Status is PlaybackStatus.Playing or PlaybackStatus.Crossfading); // RepeatQueue may be fading into itself
        engine.Dispose();
        output.Dispose();
    }

    [Fact]
    public void Manual_output_renders_only_while_playing()
    {
        using var output = NullRealtimeOutput.CreateManual();
        var provider = new Mixing.CrossfadeMixer();
        output.Init(provider);
        Assert.Equal(0, output.Pump(480));
        output.Play();
        Assert.Equal(960, output.Pump(960));
        output.Pause();
        Assert.Equal(0, output.Pump(480));
        Assert.Equal(960, output.FramesRendered);
    }
}
