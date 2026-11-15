using System.Collections.Concurrent;
using SabhaMujic.Audio.Analysis;
using SabhaMujic.Audio.Decoding;
using SabhaMujic.Audio.Engine;
using SabhaMujic.Audio.Output;
using SabhaMujic.Contracts;

namespace SabhaMujic.Audio.Tests;

internal sealed class FakeResolver : ITrackResolver
{
    private readonly ConcurrentDictionary<Guid, ResolvedTrack> _tracks = new();

    public Guid Add(string path, TrackAnalysis? analysis = null)
    {
        var id = Guid.NewGuid();
        _tracks[id] = new ResolvedTrack(id, path, analysis);
        return id;
    }

    /// <summary>Replaces the analysis the resolver returns for a track (e.g. a new loop override).</summary>
    public void Set(Guid trackId, TrackAnalysis? analysis) => _tracks[trackId] = _tracks[trackId] with { Analysis = analysis };

    public Task<ResolvedTrack?> ResolveAsync(Guid trackId, CancellationToken ct = default) =>
        Task.FromResult(_tracks.TryGetValue(trackId, out var track) ? track : null);
}

/// <summary>Engine over a manual <see cref="NullRealtimeOutput"/> and temp WAV files.</summary>
internal sealed class EngineHarness : IDisposable
{
    private readonly TempDir _dir = new();

    public EngineHarness(PlaybackEngineOptions? options = null)
    {
        Output = NullRealtimeOutput.CreateManual();
        Engine = new PlaybackEngine(Output, new WaveFileDecoderFactory(), Resolver, options ?? new PlaybackEngineOptions { AnalyzeMissingTracks = false });
        Engine.StateChanged += (_, s) => States.Enqueue(s);
        Engine.QueueChanged += (_, q) => Queues.Enqueue(q);
        Engine.PlaybackError += (_, e) => Errors.Enqueue(e);
    }

    public FakeResolver Resolver { get; } = new();

    public NullRealtimeOutput Output { get; }

    public PlaybackEngine Engine { get; }

    public ConcurrentQueue<PlaybackStateDto> States { get; } = new();

    public ConcurrentQueue<QueueDto> Queues { get; } = new();

    public ConcurrentQueue<PlaybackErrorEventArgs> Errors { get; } = new();

    public PlaybackStateDto State => Engine.GetState();

    public Guid AddTone(double seconds, double hz, TrackAnalysis? analysis = null) =>
        AddSignal(TestSignals.Tone(seconds, hz, 0.4f), analysis);

    public Guid AddSignal(float[] signal, TrackAnalysis? analysis = null) =>
        Resolver.Add(TestSignals.WriteWav(_dir.File(Guid.NewGuid().ToString("N") + ".wav"), signal), analysis);

    public Guid AddBroken()
    {
        var path = _dir.File(Guid.NewGuid().ToString("N") + ".wav");
        File.WriteAllBytes(path, Enumerable.Range(0, 2048).Select(i => (byte)(i * 13)).ToArray());
        return Resolver.Add(path);
    }

    /// <summary>Renders <paramref name="seconds"/> in 1 s chunks, letting the control thread catch up after each.</summary>
    public async Task PlayFor(double seconds, float[]? capture = null)
    {
        int total = TestSignals.Frames(seconds);
        int done = 0;
        while (done < total)
        {
            int n = Math.Min(AudioFormat.SampleRate, total - done);
            if (capture is null)
            {
                Output.Pump(n);
            }
            else
            {
                Output.Pump(capture.AsSpan(done * 2, n * 2));
            }

            done += n;
            await Engine.SyncAsync();
        }
    }

    public Task Sync() => Engine.SyncAsync();

    public void Dispose()
    {
        Engine.Dispose();
        Output.Dispose();
        _dir.Dispose();
    }
}
