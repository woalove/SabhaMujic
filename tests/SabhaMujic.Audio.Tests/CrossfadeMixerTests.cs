using SabhaMujic.Audio.Decoding;
using SabhaMujic.Audio.Mixing;

namespace SabhaMujic.Audio.Tests;

public class CrossfadeMixerTests
{
    [Fact]
    public void Equal_power_gains_sum_to_unit_power()
    {
        for (double t = 0; t <= 1.0; t += 0.05)
        {
            float o = EqualPowerCrossfade.FadeOutGain(t);
            float i = EqualPowerCrossfade.FadeInGain(t);
            Assert.Equal(1.0, o * o + i * i, 5);
        }

        Assert.Equal(1f, EqualPowerCrossfade.FadeOutGain(0));
        Assert.Equal(0f, EqualPowerCrossfade.FadeInGain(0));
        Assert.Equal(1f, EqualPowerCrossfade.FadeInGain(1), 6);
        Assert.Equal(1f, EqualPowerCrossfade.CorrelationCompensation(0.5, 0));
        float c = EqualPowerCrossfade.FadeOutGain(0.5);
        float s = EqualPowerCrossfade.FadeInGain(0.5);
        Assert.Equal(1.0, (c + s) * EqualPowerCrossfade.CorrelationCompensation(0.5, 1), 5); // identical signals stay at unit gain
    }

    [Fact]
    public void Crossfade_of_uncorrelated_noise_keeps_power_within_half_a_dB()
    {
        var mixer = new CrossfadeMixer();
        var a = Voice(TestSignals.Noise(5, seed: 1));
        var b = Voice(TestSignals.Noise(5, seed: 2));
        mixer.Play(a, 0);
        int trigger = TestSignals.Frames(1);
        int fade = TestSignals.Frames(2);
        mixer.Schedule(a, b, trigger, fade, TransitionKind.Crossfade);

        var output = Render(mixer, TestSignals.Frames(4));

        double reference = Power(output, 0, trigger);
        int window = TestSignals.Frames(0.1);
        double worst = 0;
        for (int start = trigger - window; start < trigger + fade + window; start += window / 2)
        {
            double db = 10 * Math.Log10(Power(output, start, start + window) / reference);
            worst = Math.Max(worst, Math.Abs(db));
        }

        Assert.True(worst <= 0.5, $"power deviated by {worst:F3} dB");
    }

    [Fact]
    public void Transition_starts_exactly_at_the_trigger_frame_with_cos_sin_gains()
    {
        var mixer = new CrossfadeMixer();
        var a = Voice(Dc(1f, 3));
        var b = Voice(Dc(0.5f, 3));
        mixer.Play(a, 0);
        const int trigger = 30_000;
        const int fade = 4_800;
        mixer.Schedule(a, b, trigger, fade, TransitionKind.QueueAdvance);

        var output = Render(mixer, 48_000);

        Assert.Equal(1f, output[2 * (trigger - 1)]);
        for (int k = 0; k <= fade; k += 600)
        {
            double t = (double)k / fade;
            double expected = Math.Cos(t * Math.PI / 2) + 0.5 * Math.Sin(t * Math.PI / 2);
            Assert.Equal(expected, output[2 * (trigger + k)], 4);
        }

        Assert.Equal(0.5f, output[2 * (trigger + fade + 10)], 5);
        Assert.Equal(48_000 - trigger, b.Position); // b has played exactly since the trigger frame
        var events = Drain(mixer);
        Assert.Contains(events, e => e.Kind == MixerEventKind.TransitionStarted && e.Voice == b && e.Transition == TransitionKind.QueueAdvance);
        Assert.Contains(events, e => e.Kind == MixerEventKind.TransitionCompleted && e.Voice == b);
        Assert.Contains(events, e => e.Kind == MixerEventKind.VoiceReleased && e.Voice == a);
    }

    [Fact]
    public void Aligned_loop_seam_is_sample_continuous()
    {
        // 375 Hz = exactly 128 samples per cycle, so a seam between whole cycles is perfectly aligned.
        var signal = TestSignals.Tone(10, 375, 0.5f);
        long start = 96_000;  // 2 s
        long end = 336_000;   // 7 s
        int fade = 2_400;     // 50 ms
        var mixer = new CrossfadeMixer();
        var first = Voice(signal);
        var seam = Voice(signal, start - fade);
        mixer.Play(first, 0);
        mixer.Schedule(first, seam, end - fade, fade, TransitionKind.LoopSeam, correlation: 1);

        var output = Render(mixer, TestSignals.Frames(9));

        // Identical aligned material with correlation compensation reproduces the continuous tone exactly.
        for (long n = end - fade - 100; n < end + 100; n++)
        {
            long source = n < end ? n : n - (end - start);
            Assert.Equal(signal[2 * source], output[2 * n], 4);
        }

        Assert.True(MaxDelta(output, end - fade - 500, end + 500) <= MaxDelta(output, 1000, 20_000) * 1.001);
        Assert.Equal(start + (TestSignals.Frames(9) - end), seam.Position);
    }

    [Fact]
    public void Misaligned_seam_and_queue_crossfades_have_no_discontinuity()
    {
        var a = TestSignals.Tone(6, 440, 0.5f);
        var b = TestSignals.Tone(6, 660, 0.5f, phase: 2.0);
        var mixer = new CrossfadeMixer();
        var va = Voice(a);
        var vb = Voice(b, 777);
        mixer.Play(va, 0);
        mixer.Schedule(va, vb, 100_003, 2_400, TransitionKind.LoopSeam);

        var output = Render(mixer, 200_000);
        double inside = Math.Max(MaxDelta(output, 1000, 90_000), MaxDelta(output, 110_000, 190_000));
        double seamDelta = MaxDelta(output, 99_000, 104_000);
        Assert.True(seamDelta <= inside * 1.5, $"seam delta {seamDelta} vs signal {inside}");

        // Hard cut for comparison: shows the measure detects a discontinuity.
        var cutMixer = new CrossfadeMixer();
        var ca = Voice(a);
        var cb = Voice(b, 777);
        cutMixer.Play(ca, 0);
        cutMixer.Schedule(ca, cb, 100_003, 0, TransitionKind.LoopSeam);
        var cut = Render(cutMixer, 200_000);
        Assert.True(MaxDelta(cut, 99_000, 104_000) > inside * 3);
    }

    [Fact]
    public void Late_queue_transition_finishes_its_fade_at_the_outgoing_end()
    {
        var mixer = new CrossfadeMixer();
        var a = Voice(Dc(1f, 1)); // 48 000 frames
        var b = Voice(Dc(0.25f, 2));
        mixer.Play(a, 0);
        Render(mixer, 40_000);
        mixer.Schedule(a, b, 30_000, 24_000, TransitionKind.QueueAdvance); // trigger already passed
        var output = Render(mixer, 20_000);

        // The fade is shortened to the 8 000 frames a has left, so a is silent from its end on.
        Assert.Equal(1f, output[0], 4);
        Assert.Equal(0.25f, output[2 * 8_000 + 2], 3);
        Assert.True(a.IsDrained);
    }

    [Fact]
    public void Loop_seam_whose_trigger_has_passed_is_reported_missed()
    {
        var mixer = new CrossfadeMixer();
        var a = Voice(Dc(1f, 2));
        var seam = Voice(Dc(1f, 2));
        mixer.Play(a, 0);
        Render(mixer, 10_000);
        mixer.Schedule(a, seam, 5_000, 480, TransitionKind.LoopSeam);
        Render(mixer, 480);
        var events = Drain(mixer);
        Assert.Contains(events, e => e.Kind == MixerEventKind.TransitionMissed && e.Voice == seam);
        Assert.Contains(events, e => e.Kind == MixerEventKind.VoiceReleased && e.Voice == seam);
        Assert.Same(a, mixer.CurrentVoice);
    }

    [Fact]
    public void Pause_ramps_down_freezes_and_resumes_where_it_was()
    {
        var mixer = new CrossfadeMixer();
        var a = Voice(Dc(1f, 3));
        mixer.Play(a, 0);
        Render(mixer, 10_000);
        mixer.SetPaused(true);
        var paused = Render(mixer, 4_800);
        long frozenAt = a.Position;
        Assert.InRange(frozenAt, 10_000 + CrossfadeMixer.DeclickFrames - 1, 10_000 + CrossfadeMixer.DeclickFrames + 1);
        Assert.True(paused[2 * 100] is > 0f and < 1f);
        Assert.Equal(0f, paused[2 * 1000]);
        Render(mixer, 4_800);
        Assert.Equal(frozenAt, a.Position);

        mixer.SetPaused(false);
        var resumed = Render(mixer, 4_800);
        Assert.Equal(1f, resumed[2 * 4_000], 5);
        Assert.Equal(frozenAt + 4_800, a.Position);
    }

    [Fact]
    public void Gain_and_stop_ramp_without_clicks()
    {
        var mixer = new CrossfadeMixer();
        var a = Voice(Dc(1f, 3));
        mixer.Play(a, 0);
        mixer.SetGain(0.25f);
        var output = Render(mixer, 2_000);
        Assert.Equal(0.25f, output[2 * 1_000], 4);
        Assert.True(output[2 * 100] < 1f && output[2 * 100] > 0.25f);

        mixer.Stop();
        var stopped = Render(mixer, 2_000);
        Assert.True(MaxDelta(stopped, 0, 2_000) < 0.01);
        Assert.Equal(0f, stopped[2 * 1_000]);
        Assert.Null(mixer.CurrentVoice);
        Assert.Contains(Drain(mixer), e => e.Kind == MixerEventKind.VoiceReleased && e.Voice == a);
    }

    [Fact]
    public void Voice_end_without_a_transition_is_reported()
    {
        var mixer = new CrossfadeMixer();
        var a = Voice(Dc(1f, 0.5));
        mixer.Play(a, 0);
        var output = Render(mixer, 30_000);
        Assert.Equal(0f, output[2 * 25_000]);
        var events = Drain(mixer);
        Assert.Contains(events, e => e.Kind == MixerEventKind.VoiceEnded && e.Voice == a);
        Assert.Contains(events, e => e.Kind == MixerEventKind.VoiceReleased && e.Voice == a);
    }

    /// <summary>R-11: a stalled consumer never loses VoiceReleased/VoiceEnded; overflow events arrive later, in order.</summary>
    [Fact]
    public void Events_beyond_the_ring_are_kept_in_order_and_none_is_lost()
    {
        var mixer = new CrossfadeMixer();
        var voices = new List<PlaybackVoice>();
        for (int i = 0; i < 700; i++)
        {
            var voice = Voice(Dc(0.1f, 0.05));
            voices.Add(voice);
            mixer.Play(voice, 0); // each hard cut releases the previous voice: TransitionStarted + VoiceReleased (+ more)
            Render(mixer, 960);
        }

        Assert.True(mixer.DroppedEvents > 0, "the ring overflowed");
        var events = Drain(mixer);
        Assert.True(events.Count > 1024);
        var released = events.Where(e => e.Kind == MixerEventKind.VoiceReleased).Select(e => e.Voice).ToList();
        Assert.Equal(released.Count, released.Distinct().Count());
        // Every voice but the last one (still current or just ended) was released exactly once, in play order.
        Assert.Equal(voices.Take(released.Count), released);
        Assert.True(released.Count >= voices.Count - 1);
        Assert.False(mixer.TryDequeueEvent(out _));
    }

    internal static PlaybackVoice Voice(float[] signal, long start = 0)
    {
        var voice = new PlaybackVoice(new MemoryAudioSource(signal), start, signal.Length / 2, bufferFrames: signal.Length / 2 + 1);
        voice.Prefill();
        return voice;
    }

    internal static float[] Dc(float value, double seconds)
    {
        var s = new float[TestSignals.Frames(seconds) * 2];
        Array.Fill(s, value);
        return s;
    }

    internal static float[] Render(CrossfadeMixer mixer, int frames)
    {
        var output = new float[frames * 2];
        for (int offset = 0; offset < output.Length; offset += 960)
        {
            mixer.Read(output.AsSpan(offset, Math.Min(960, output.Length - offset)));
        }

        return output;
    }

    internal static double MaxDelta(float[] stereo, long from, long to)
    {
        double max = 0;
        for (long i = Math.Max(1, from); i < to; i++)
        {
            max = Math.Max(max, Math.Abs(stereo[2 * i] - stereo[2 * i - 2]));
            max = Math.Max(max, Math.Abs(stereo[2 * i + 1] - stereo[2 * i - 1]));
        }

        return max;
    }

    private static double Power(float[] stereo, int from, int to)
    {
        double sum = 0;
        for (int i = from * 2; i < to * 2; i++)
        {
            sum += stereo[i] * stereo[i];
        }

        return sum / ((to - from) * 2);
    }

    private static List<MixerEvent> Drain(CrossfadeMixer mixer)
    {
        var list = new List<MixerEvent>();
        while (mixer.TryDequeueEvent(out var e))
        {
            list.Add(e);
        }

        return list;
    }
}
