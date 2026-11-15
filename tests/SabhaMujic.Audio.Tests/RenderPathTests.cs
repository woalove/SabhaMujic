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

    [Fact]
    public async Task Realtime_null_output_pulls_at_the_speed_factor()
    {
        var output = new NullRealtimeOutput(speedFactor: 20);
        using var dir = new TempDir();
        var resolver = new FakeResolver();
        var track = resolver.Add(TestSignals.WriteWav(dir.File("t.wav"), TestSignals.Tone(30, 440)));
        using var engine = new PlaybackEngine(output, new Decoding.WaveFileDecoderFactory(), resolver, new PlaybackEngineOptions { AnalyzeMissingTracks = false });
        await engine.PlayTrackAsync(track);
        var sw = Stopwatch.StartNew();
        await Task.Delay(500);
        double position = engine.GetState().PositionSeconds;
        double expected = sw.Elapsed.TotalSeconds * 20;
        log.WriteLine($"position {position:F2} s after {sw.Elapsed.TotalSeconds:F2} s at 20x");
        Assert.InRange(position, expected * 0.6, expected * 1.2);
        Assert.Equal(PlaybackStatus.Playing, engine.GetState().Status);
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
