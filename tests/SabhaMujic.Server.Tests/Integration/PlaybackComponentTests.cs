using System.Collections.Concurrent;
using SabhaMujic.Audio;
using SabhaMujic.Audio.Analysis;
using SabhaMujic.Audio.Decoding;
using SabhaMujic.Contracts;
using SabhaMujic.Server.Api;
using SabhaMujic.Server.Hosting;
using SabhaMujic.Server.Playback;
using StoredAnalysis = SabhaMujic.Data.Repositories.TrackAnalysis;

namespace SabhaMujic.Server.Tests.Integration;

public sealed class DecoderAudioProbeTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sabhamujic-probe-").FullName;
    private readonly DecoderAudioProbe _probe = new(new WaveFileDecoderFactory());

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public async Task Real_audio_is_supported_with_its_duration()
    {
        var result = await _probe.ProbeAsync(Write("tone.wav", TestWav.Tone(3, 440, padSeconds: 0)));
        Assert.True(result.IsSupported);
        Assert.InRange(result.DurationSeconds, 2.99, 3.01);
    }

    [Fact]
    public async Task Garbage_and_truncated_files_are_unsupported()
    {
        var garbage = await _probe.ProbeAsync(Write("garbage.wav", TestWav.Garbage()));
        Assert.False(garbage.IsSupported);
        Assert.False(string.IsNullOrWhiteSpace(garbage.Error));
        Assert.DoesNotContain(_dir, garbage.Error);

        var headerOnly = await _probe.ProbeAsync(Write("empty.wav", TestWav.Tone(1, 440)[..44]));
        Assert.False(headerOnly.IsSupported);

        var empty = await _probe.ProbeAsync(Write("zero.wav", []));
        Assert.False(empty.IsSupported);
    }
}

public sealed class AnalysisMappingTests
{
    [Fact]
    public void Ready_analysis_round_trips_through_the_database_representation()
    {
        var analysis = new TrackAnalysis
        {
            Version = LoopAnalyzer.AnalysisVersion,
            TotalFrames = 48_000 * 30,
            LeadingTrimFrame = 24_000,
            TrailingTrimFrame = 48_000 * 29,
            Loop = new LoopRegion(48_000 * 5, 48_000 * 25, 9_600, 0.8) { SeamCorrelation = 0.6, IsFallback = true },
            TempoBpm = 117.5,
            Gaps = [new SilenceGap(48_000 * 12, 48_000 * 13)],
        };
        var trackId = Guid.NewGuid();
        var stored = AnalysisMapping.ToStored(trackId, analysis);
        Assert.Equal(AnalysisStatus.Ready, stored.Status);

        var back = AnalysisMapping.FromStored(stored, durationSeconds: 30)!;
        Assert.Equal(analysis.LeadingTrimFrame, back.LeadingTrimFrame);
        Assert.Equal(analysis.TrailingTrimFrame, back.TrailingTrimFrame);
        Assert.Equal((analysis.Loop.StartFrame, analysis.Loop.EndFrame, analysis.Loop.CrossfadeFrames, analysis.Loop.Confidence),
            (back.Loop.StartFrame, back.Loop.EndFrame, back.Loop.CrossfadeFrames, back.Loop.Confidence));
        Assert.Equal(analysis.TotalFrames, back.TotalFrames);

        // Schema v2 details keep everything the columns cannot.
        Assert.Equal(0.6, back.Loop.SeamCorrelation);
        Assert.True(back.Loop.IsFallback);
        Assert.Equal(117.5, back.TempoBpm);
        Assert.Equal(analysis.Gaps, back.Gaps);

        // Rows written before v2 (no details) are rebuilt from the columns with a plain equal-power seam.
        var legacy = AnalysisMapping.FromStored(stored with { DetailsJson = null }, durationSeconds: 30)!;
        Assert.Equal(0, legacy.Loop.SeamCorrelation);
        Assert.Equal(analysis.Loop.StartFrame, legacy.Loop.StartFrame);

        // Details from another analysis version are ignored in favour of the columns.
        var mismatched = AnalysisMapping.FromStored(stored with { AnalysisVersion = analysis.Version + 1 }, durationSeconds: 30)!;
        Assert.Equal(0, mismatched.Loop.SeamCorrelation);
    }

    [Theory]
    [InlineData(AnalysisStatus.Pending)]
    [InlineData(AnalysisStatus.Analyzing)]
    [InlineData(AnalysisStatus.Failed)]
    public void Only_ready_rows_become_engine_analyses(AnalysisStatus status)
    {
        Assert.Null(AnalysisMapping.FromStored(new StoredAnalysis(Guid.NewGuid(), status, 1, 0, 10, 1, 9, 0.1, 0.5), 10));
        Assert.Null(AnalysisMapping.FromStored(null, 10));
    }
}

/// <summary>Host-level behaviour of the playback pieces (no pipe; calls go through in-process <see cref="ApiSession"/>s).</summary>
public sealed class PlaybackComponentTests : IAsyncLifetime
{
    private HostHarness _h = null!;
    private ApiSession _admin = null!;

    public async Task InitializeAsync()
    {
        _h = await HostHarness.StartAsync(speed: 10);
        _admin = _h.App.CreateSession(ClientKind.Desktop, "PC\\owner", isElevatedAdministrator: true);
        await _admin.CompleteInitialSetupAsync("admin", "admin-pin");
    }

    public async Task DisposeAsync() => await _h.DisposeAsync();

    private async Task<Guid> ImportAsync(string name, byte[] bytes)
    {
        var id = await _admin.BeginImportAsync(name, bytes.Length);
        for (var offset = 0; offset < bytes.Length; offset += AdminLimits.MaxChunkBytes)
        {
            await _admin.UploadChunkAsync(id, offset, bytes[offset..Math.Min(bytes.Length, offset + AdminLimits.MaxChunkBytes)]);
        }

        return (await _admin.CompleteImportAsync(id, new ImportMetadata(Path.GetFileNameWithoutExtension(name), null, null))).TrackId;
    }

    private async Task<StoredAnalysis?> WaitForAnalysisAsync(Guid trackId, AnalysisStatus status)
    {
        await AssertEx.Eventually(() => _h.App.Analysis.GetAsync(trackId).GetAwaiter().GetResult()?.Status == status, $"analysis {status}", 60_000);
        return await _h.App.Analysis.GetAsync(trackId);
    }

    [Fact]
    public async Task Analysis_worker_reports_progress_and_marks_ready()
    {
        var progress = new ConcurrentQueue<ImportProgressDto>();
        var changes = new ConcurrentQueue<LibraryChangedDto>();
        await using var sub = _h.App.Events.Subscribe(new Recorder(progress, changes), _admin.Caller);

        var trackId = await ImportAsync("tone.wav", TestWav.Tone(6, 330));
        var stored = await WaitForAnalysisAsync(trackId, AnalysisStatus.Ready);

        Assert.Equal(LoopAnalyzer.AnalysisVersion, stored!.AnalysisVersion);
        await AssertEx.Eventually(() => progress.Any(p => p.Stage == ImportStages.Done), "Done after analysis");
        Assert.Equal(
            [ImportStages.Uploading, ImportStages.Validating, ImportStages.Storing, ImportStages.Analyzing, ImportStages.Done],
            progress.Select(p => p.Stage).Distinct());
        Assert.Contains(changes, c => c.Kind == LibraryChangeKind.AnalysisUpdated && c.EntityId == trackId);
    }

    [Fact]
    public async Task A_file_that_breaks_during_analysis_fails_only_its_track()
    {
        var bad = await ImportAsync("bad.wav", TestWav.Tone(4, 440));
        var good = await ImportAsync("good.wav", TestWav.Tone(4, 550));
        await WaitForAnalysisAsync(bad, AnalysisStatus.Ready);
        await WaitForAnalysisAsync(good, AnalysisStatus.Ready);

        // Corrupt the stored file behind the service's back, then ask for re-analysis.
        var path = _h.Host.Store.GetPhysicalPath((await _h.App.Tracks.GetFileInfoAsync(bad))!.FileKey);
        await File.WriteAllBytesAsync(path, TestWav.Garbage());
        await _admin.ReanalyzeTrackAsync(bad);
        await _admin.ReanalyzeTrackAsync(good);

        var failed = await WaitForAnalysisAsync(bad, AnalysisStatus.Failed);
        Assert.False(string.IsNullOrWhiteSpace(failed!.Error));
        Assert.DoesNotContain(_h.Root, failed.Error);
        await WaitForAnalysisAsync(good, AnalysisStatus.Ready);
        Assert.Equal(AnalysisStatus.Failed, (await _admin.GetTrackAsync(bad)).Analysis);
    }

    [Fact]
    public async Task Engine_results_are_persisted_and_the_resolver_keeps_full_fidelity()
    {
        var trackId = await ImportAsync("t.wav", TestWav.Tone(5, 440));
        await WaitForAnalysisAsync(trackId, AnalysisStatus.Ready);
        var resolved = await _h.Host.Resolver.ResolveAsync(trackId);
        Assert.NotNull(resolved!.Analysis);

        // An engine-computed analysis with a newer version replaces the stored one, keeping its seam correlation in memory.
        var newer = resolved.Analysis! with { Version = LoopAnalyzer.AnalysisVersion + 1, Loop = resolved.Analysis.Loop with { SeamCorrelation = 0.42 } };
        _h.Host.Analysis.AcceptEngineResult(trackId, newer);
        await AssertEx.Eventually(() => _h.App.Analysis.GetAsync(trackId).GetAwaiter().GetResult()!.AnalysisVersion == newer.Version, "stored");
        // The worker stores the row first and then drops the cached entry, so the resolver catches up right after.
        await AssertEx.Eventually(
            () => _h.Host.Resolver.ResolveAsync(trackId).GetAwaiter().GetResult()?.Analysis?.Loop.SeamCorrelation == 0.42,
            "the resolver serves the stored full-fidelity analysis");
    }

    [Fact]
    public async Task Resolver_applies_the_admin_loop_override()
    {
        var trackId = await ImportAsync("song.wav", TestWav.Song());
        await WaitForAnalysisAsync(trackId, AnalysisStatus.Ready);
        await _admin.SetLoopOverrideAsync(trackId, new LoopRegionDto(10, 20, 0.1, 0, false));

        var resolved = await _h.Host.Resolver.ResolveAsync(trackId);
        Assert.True(resolved!.Analysis!.Loop.IsManualOverride);
        Assert.Equal(AudioFormat.SecondsToFrames(10), resolved.Analysis.Loop.StartFrame);
        Assert.Equal(AudioFormat.SecondsToFrames(20), resolved.Analysis.Loop.EndFrame);
        Assert.True(File.Exists(resolved.Path));

        await _admin.SetLoopOverrideAsync(trackId, null);
        Assert.False((await _h.Host.Resolver.ResolveAsync(trackId))!.Analysis!.Loop.IsManualOverride);
        Assert.Null(await _h.Host.Resolver.ResolveAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task Heartbeat_pushes_fresh_states_with_increasing_revisions_while_playing()
    {
        var trackId = await ImportAsync("long.wav", TestWav.Tone(60, 440));
        var states = new ConcurrentQueue<PlaybackStateDto>();
        _h.Host.Playback.StateChanged += states.Enqueue;
        await _admin.PlayTrackAsync(trackId);
        await AssertEx.Eventually(() => _h.Host.Engine.GetState().Status == PlaybackStatus.Playing, "playing");
        states.Clear();

        // No commands from here on: only heartbeats (every 300 ms in this harness). Wait for them instead of sleeping a
        // fixed time, so a slow CI runner can't make the count fall short.
        await AssertEx.Eventually(() => states.Count >= 3, "at least three heartbeats");

        var beats = states.ToArray();
        Assert.All(beats.Zip(beats.Skip(1)), p => Assert.True(p.Second.Revision > p.First.Revision));
        Assert.True(beats[^1].PositionSeconds > beats[0].PositionSeconds);
        Assert.Equal(_h.App.ServerEpoch, (await _admin.GetStateAsync()).ServerEpoch);
    }

    [Fact]
    public async Task Graceful_stop_saves_state_while_playing()
    {
        var trackId = await ImportAsync("long.wav", TestWav.Tone(60, 440));
        await _admin.SetVolumeAsync(0.3f);
        await _admin.PlayTrackAsync(trackId);
        await AssertEx.Eventually(() => _h.Host.Engine.GetState() is { Status: PlaybackStatus.Playing, PositionSeconds: > 2 }, "playing for a while");

        var stop = System.Diagnostics.Stopwatch.StartNew();
        await _h.Host.StopAsync();
        Assert.True(stop.Elapsed >= TimeSpan.FromMilliseconds(300), "fades out before stopping");
        Assert.False(_h.Host.IsRunning);
        await _h.Host.StopAsync(); // idempotent

        await _h.StartHostAsync();
        var queue = _h.App.PlaybackState;
        var persisted = await queue.LoadQueueAsync();
        Assert.Equal([trackId], persisted.Items.Select(i => i.TrackId));
        Assert.Equal(0.3f, (await queue.LoadSettingsAsync()).Volume);
        Assert.True(persisted.PositionSeconds > 0.5, $"saved position {persisted.PositionSeconds}");

        // The restored queue keeps its ids and Resume continues at the saved position.
        Assert.Equal(persisted.Items, _h.Host.Engine.GetQueue().Items);
        var admin = _h.App.CreateSession(ClientKind.Desktop, "PC\\owner");
        await admin.LoginAsync("admin", "admin-pin");
        await admin.ResumeAsync();
        await AssertEx.Eventually(() => _h.Host.Engine.GetState().Status == PlaybackStatus.Playing, "resumed");
        Assert.True(_h.Host.Engine.GetState().PositionSeconds >= persisted.PositionSeconds - 0.05);
    }

    [Fact]
    public async Task A_loop_override_reaches_the_voice_that_is_already_playing()
    {
        var trackId = await ImportAsync("song.wav", TestWav.Song(introSeconds: 4, patternSeconds: 8, repeats: 6, outroSeconds: 4)); // 56 s
        await WaitForAnalysisAsync(trackId, AnalysisStatus.Ready);
        await _admin.SetLoopModeAsync(LoopMode.SmartLoop);
        await _admin.PlayTrackAsync(trackId);
        await AssertEx.Eventually(() => _h.Host.Engine.GetState().Status == PlaybackStatus.Playing, "playing");

        // While it plays (well before 30 s), the admin sets a loop of 20..30 s: from the first seam on it must hold.
        await _admin.SetLoopOverrideAsync(trackId, new LoopRegionDto(20, 30, 0.2, 1, true));
        Assert.True(_h.Host.Engine.GetState().PositionSeconds < 25, "the override must arrive before its seam for this test");

        var positions = new List<double>();
        for (var i = 0; i < 60; i++)
        {
            positions.Add(_h.Host.Engine.GetState().PositionSeconds);
            await Task.Delay(100); // 1 s of audio at 10x
        }

        Assert.All(positions, p => Assert.True(p <= 30.1, $"position {p:F2} passed the overridden loop end"));
        Assert.Contains(positions.Zip(positions.Skip(1)), p => p.Second < p.First);
    }

    [Fact]
    public async Task Hosted_components_start_after_the_core_and_stop_first()
    {
        await _h.Host.StopAsync();
        var component = new RecordingComponent();
        var host = await SabhaHost.StartAsync(new SabhaHostOptions
        {
            DataRoot = _h.Root,
            PipeName = _h.PipeName,
            StartPipe = false,
            Components = [component],
            OutputFactory = () => new Audio.Output.NullRealtimeOutput(10),
        });
        Assert.True(component.SawRunningApp);
        await host.StopAsync();
        Assert.True(component.Stopped);
        await _h.StartHostAsync(); // so DisposeAsync has a host to dispose
    }

    private sealed class RecordingComponent : IHostedComponent
    {
        public bool SawRunningApp { get; private set; }

        public bool Stopped { get; private set; }

        public async Task StartAsync(SabhaHost host, CancellationToken ct) =>
            SawRunningApp = host.App is not null && await host.App.Auth.IsSetupRequiredAsync(ct) == false && host.Engine is not null;

        public Task StopAsync(CancellationToken ct)
        {
            Stopped = true;
            return Task.CompletedTask;
        }
    }

    private sealed class Recorder(ConcurrentQueue<ImportProgressDto> progress, ConcurrentQueue<LibraryChangedDto> changes) : Application.IEventSink
    {
        public ValueTask DeliverAsync(Application.ServerEvent serverEvent, CancellationToken ct)
        {
            switch (serverEvent)
            {
                case Application.ImportProgressEvent e:
                    progress.Enqueue(e.Progress);
                    break;
                case Application.LibraryEvent e:
                    changes.Enqueue(e.Change);
                    break;
            }

            return ValueTask.CompletedTask;
        }
    }
}
