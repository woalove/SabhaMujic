#if WINDOWS
using System.Runtime.Versioning;
using NAudio.MediaFoundation;
using NAudio.Wave;
using SabhaMujic.Audio.Decoding;
using SabhaMujic.Audio.Engine;
using SabhaMujic.Audio.Mixing;
using SabhaMujic.Audio.Output;
using SabhaMujic.Audio.Windows;
using SabhaMujic.Contracts;
using Xunit.Abstractions;

namespace SabhaMujic.Audio.Tests.Windows;

[SupportedOSPlatform("windows")]
internal static class MediaFoundationProbe
{
    private static readonly Lazy<bool> LazyAvailable = new(() =>
    {
        try
        {
            MediaFoundationApi.Startup();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    });

    public static bool Available => LazyAvailable.Value;

    public static bool CanEncode(string format)
    {
        if (!Available)
        {
            return false;
        }

        try
        {
            var input = new WaveFormat(44_100, 16, 2);
            return format switch
            {
                "mp3" => MediaFoundationEncoder.SelectMediaType(AudioSubtypes.MFAudioFormat_MP3, input, 128_000) is not null,
                "aac" => MediaFoundationEncoder.SelectMediaType(AudioSubtypes.MFAudioFormat_AAC, input, 128_000) is not null,
                "wma" => MediaFoundationEncoder.SelectMediaType(AudioSubtypes.MFAudioFormat_WMAudioV8, input, 128_000) is not null,
                "flac" => MediaFoundationEncoder.GetOutputMediaTypes(AudioSubtypes.MFAudioFormat_FLAC).Length > 0,
                _ => false,
            };
        }
        catch (Exception)
        {
            return false;
        }
    }
}

/// <summary>Skipped (reported as skipped, not passed) where Media Foundation is missing.</summary>
public sealed class MediaFoundationFactAttribute : FactAttribute
{
    public MediaFoundationFactAttribute()
    {
        if (!OperatingSystem.IsWindows() || !MediaFoundationProbe.Available)
        {
            Skip = "Media Foundation is not available on this machine.";
        }
    }
}

/// <summary>Skipped where this Windows edition has no Media Foundation encoder for the format (e.g. some Server SKUs).</summary>
public sealed class MediaFoundationEncoderFactAttribute : FactAttribute
{
    public MediaFoundationEncoderFactAttribute(string format)
    {
        if (!OperatingSystem.IsWindows() || !MediaFoundationProbe.CanEncode(format))
        {
            Skip = $"No Media Foundation {format} encoder on this machine; decoding of {format} is unverified here.";
        }
    }
}

[SupportedOSPlatform("windows")]
public class MediaFoundationDecoderTests(ITestOutputHelper log)
{
    [MediaFoundationFact]
    public void Decodes_wav_at_44k1_to_48k_stereo_with_exact_seeks()
    {
        using var dir = new TempDir();
        var path = WriteTone44k(dir.File("tone.wav"), 3);
        using var source = new MediaFoundationDecoderFactory().Open(path);
        Assert.InRange(source.TotalFrames, 143_900, 144_100);
        AssertTone(source, 1000, expectedSeconds: 3, toleranceSeconds: 0.01);

        // Seeking is sample-accurate on the 48 kHz timeline: compare with a straight read.
        source.Seek(0);
        var all = new float[source.TotalFrames * 2];
        int read = ReadAll(source, all);
        source.Seek(70_000);
        var part = new float[2 * 2_000];
        Assert.Equal(part.Length, ReadAll(source, part));
        Assert.True(read > 2 * 72_000);
        for (int i = 0; i < 2_000; i++)
        {
            Assert.True(Math.Abs(part[2 * i] - all[2 * (70_000 + i)]) < 0.01, $"frame {i}");
        }
    }

    [MediaFoundationEncoderFact("mp3")]
    public void Decodes_mp3() => DecodeEncoded("mp3", (w, p) => MediaFoundationEncoder.EncodeToMp3(w, p, 128_000), 0.1);

    [MediaFoundationEncoderFact("aac")]
    public void Decodes_m4a_aac() => DecodeEncoded("m4a", (w, p) => MediaFoundationEncoder.EncodeToAac(w, p, 128_000), 0.1);

    [MediaFoundationEncoderFact("wma")]
    public void Decodes_wma() => DecodeEncoded("wma", (w, p) => MediaFoundationEncoder.EncodeToWma(w, p, 128_000), 0.2);

    [MediaFoundationEncoderFact("flac")]
    public void Decodes_flac() => DecodeEncoded("flac", (w, p) => MediaFoundationEncoder.EncodeToFlac(w, p), 0.01);

    [MediaFoundationFact]
    public void Garbage_and_missing_files_throw_AudioDecoderException()
    {
        using var dir = new TempDir();
        var garbage = dir.File("garbage.mp3");
        File.WriteAllBytes(garbage, Enumerable.Range(0, 8192).Select(i => (byte)(i * 31)).ToArray());
        var factory = new MediaFoundationDecoderFactory();
        Assert.Throws<AudioDecoderException>(() => factory.Open(garbage));
        Assert.Throws<AudioDecoderException>(() => factory.Open(dir.File("missing.mp3")));
    }

    [MediaFoundationFact]
    public async Task Engine_plays_media_foundation_tracks_with_a_crossfade()
    {
        using var dir = new TempDir();
        var resolver = new FakeResolver();
        var a = resolver.Add(WriteTone44k(dir.File("a.wav"), 6));
        var b = resolver.Add(WriteTone44k(dir.File("b.wav"), 6));
        using var output = NullRealtimeOutput.CreateManual();
        using var engine = new PlaybackEngine(output, new MediaFoundationDecoderFactory(), resolver, new PlaybackEngineOptions { AnalyzeMissingTracks = false });
        await engine.SetCrossfadeAsync(1);
        var queue = await engine.EnqueueAsync([a, b], false);
        await engine.PlayQueueItemAsync(queue.Items[0].QueueItemId);
        for (int i = 0; i < 11; i++)
        {
            output.Pump(TestSignals.Frames(0.5));
            await engine.SyncAsync();
        }

        var state = engine.GetState();
        log.WriteLine($"{state.Status} {state.CurrentTrackId} {state.PositionSeconds}");
        Assert.Equal(b, state.CurrentTrackId);
        Assert.Equal(PlaybackStatus.Playing, state.Status);
        Assert.Equal(0.5, state.PositionSeconds, 1);
    }

    private void DecodeEncoded(string extension, Action<IWaveProvider, string> encode, double durationTolerance)
    {
        using var dir = new TempDir();
        var wav = WriteTone44k(dir.File("source.wav"), 3);
        var encoded = dir.File("encoded." + extension);
        using (var reader = new WaveFileReader(wav))
        {
            encode(reader, encoded);
        }

        using var source = new MediaFoundationDecoderFactory().Open(encoded);
        log.WriteLine($"{extension}: {source.TotalFrames} frames ({source.Duration.TotalSeconds:F3} s)");
        AssertTone(source, 1000, expectedSeconds: 3, durationTolerance);
        source.Seek(TestSignals.Frames(1.5));
        Assert.Equal(TestSignals.Frames(1.5), source.PositionFrames);
        var part = new float[2 * 4800];
        Assert.Equal(part.Length, ReadAll(source, part));
    }

    private static void AssertTone(IAudioSource source, double hz, double expectedSeconds, double toleranceSeconds)
    {
        Assert.InRange(source.Duration.TotalSeconds, expectedSeconds - toleranceSeconds, expectedSeconds + toleranceSeconds);
        source.Seek(0);
        var all = new float[(source.TotalFrames + 48_000) * 2];
        int frames = ReadAll(source, all) / 2;
        Assert.InRange(frames, (int)((expectedSeconds - toleranceSeconds) * 48_000), (int)((expectedSeconds + toleranceSeconds) * 48_000));
        int crossings = 0;
        for (int i = 24_001; i < 72_000; i++)
        {
            if (all[2 * i - 2] < 0 && all[2 * i] >= 0)
            {
                crossings++;
            }
        }

        Assert.InRange(crossings, (int)hz - 2, (int)hz + 2);
    }

    private static int ReadAll(IAudioSource source, float[] buffer)
    {
        int total = 0;
        int n;
        while (total < buffer.Length && (n = source.Read(buffer.AsSpan(total))) > 0)
        {
            total += n;
        }

        return total;
    }

    private static string WriteTone44k(string path, double seconds)
    {
        const int rate = 44_100;
        int frames = (int)(rate * seconds);
        var samples = new float[frames * 2];
        for (int i = 0; i < frames; i++)
        {
            float v = 0.5f * (float)Math.Sin(2 * Math.PI * 1000 * i / rate);
            samples[2 * i] = v;
            samples[2 * i + 1] = v;
        }

        return TestSignals.WriteWav16(path, samples, rate, 2);
    }
}

[SupportedOSPlatform("windows")]
public class WasapiOutputTests(ITestOutputHelper log)
{
    /// <summary>
    /// CI runners have no audio device: the output must report DeviceLost and keep retrying instead of throwing.
    /// On a PC with a device it must actually pull audio. Either way Stop/Dispose must not hang.
    /// </summary>
    [Fact]
    public async Task Plays_on_a_device_or_reports_device_loss_without_throwing()
    {
        var counter = new CountingProvider(new CrossfadeMixer());
        var lost = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var output = new WasapiOutput(100);
        output.DeviceLost += (_, e) => lost.TrySetResult(e.Message);
        output.Init(counter);
        output.Play();
        await Task.WhenAny(lost.Task, Task.Delay(1500));

        string? lossMessage = lost.Task.IsCompleted ? await lost.Task : null;
        log.WriteLine(lossMessage is not null
            ? $"No usable device: {lossMessage}"
            : $"Device '{output.DeviceName}' pulled {counter.Samples} samples");
        Assert.True(lost.Task.IsCompleted || counter.Samples > 0, "neither audio was pulled nor device loss reported");

        output.Stop();
        var dispose = Task.Run(output.Dispose);
        Assert.Same(dispose, await Task.WhenAny(dispose, Task.Delay(TimeSpan.FromSeconds(10))));
    }

    private sealed class CountingProvider(ISampleProvider inner) : ISampleProvider
    {
        private long _samples;

        public long Samples => Interlocked.Read(ref _samples);

        public WaveFormat WaveFormat => inner.WaveFormat;

        public int Read(Span<float> buffer)
        {
            int n = inner.Read(buffer);
            Interlocked.Add(ref _samples, n);
            return n;
        }
    }
}
#endif
